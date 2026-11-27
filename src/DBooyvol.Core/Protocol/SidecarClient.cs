using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DBooyvol.Core.Protocol;

/// <summary>An error returned by the sidecar, with the fields the UI classifies by.</summary>
public sealed class SidecarException : Exception
{
    public string? SqlState { get; init; }
    public int? Code { get; init; }
    public string? ClassName { get; init; }
    /// <summary>The JVM or the server connection is gone.</summary>
    public bool Lost { get; init; }

    public SidecarException(string message, string? sqlState = null, int? code = null,
        string? className = null, bool lost = false, Exception? inner = null) : base(message, inner)
    {
        SqlState = sqlState;
        Code = code;
        ClassName = className;
        Lost = lost;
    }

    public bool IsConnectionLost =>
        Lost || (SqlState?.StartsWith("08", StringComparison.Ordinal) ?? false);
}

public sealed record SidecarOptions(
    string JavaPath,
    string JarPath,
    string WorkingDirectory,
    string? DriversDirectory = null,
    string JvmArguments = "-Dfile.encoding=UTF-8 -Xss4m");

/// <summary>
/// One long-lived JVM speaking newline-delimited JSON over stdio. Responses may
/// arrive in any order; each request carries an integer id.
/// </summary>
public sealed class SidecarClient : IAsyncDisposable
{
    private readonly SidecarOptions _options;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _writeLock = new();
    private Process? _process;
    private StreamWriter? _writer;
    private Task? _readTask;
    private Task? _errorTask;
    private int _seq;
    private volatile bool _stopping;

    public event Action<string>? StderrReceived;
    public string LastStderr { get; private set; } = "";
    public bool IsRunning => _process is { HasExited: false };

    public SidecarClient(SidecarOptions options) => _options = options;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRunning) return;
            if (!File.Exists(_options.JarPath))
                throw new SidecarException($"sidecar.jar не найден: {_options.JarPath}", lost: true);
            if (!File.Exists(_options.JavaPath))
                throw new SidecarException(
                    $"Java не найдена: {_options.JavaPath}. Укажите путь в настройках или положите runtime рядом с приложением.",
                    lost: true);

            var args = $"{_options.JvmArguments} -jar \"{_options.JarPath}\"";
            var psi = new ProcessStartInfo
            {
                FileName = _options.JavaPath,
                Arguments = args,
                WorkingDirectory = _options.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Exited += (_, _) => OnProcessLost("Java-процесс завершился");
            process.Start();
            _process = process;
            _writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            _readTask = Task.Run(() => ReadLoopAsync(process));
            _errorTask = Task.Run(() => ErrorLoopAsync(process));
            _stopping = false;
        }
        finally { _startGate.Release(); }
    }

    private async Task ReadLoopAsync(Process process)
    {
        try
        {
            while (!process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;
                Dispatch(line);
            }
        }
        catch (Exception) { /* process died; handled by Exited */ }
        finally { OnProcessLost("Java-процесс завершился"); }
    }

    private async Task ErrorLoopAsync(Process process)
    {
        try
        {
            while (!process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                LastStderr = line;
                StderrReceived?.Invoke(line);
            }
        }
        catch (Exception) { }
    }

    private void Dispatch(string line)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return; }
        if (node is not JsonObject obj) return;
        if (!obj.TryGetPropertyValue("id", out var idNode) || idNode is null) return;
        int id;
        try { id = idNode.GetValue<int>(); }
        catch (Exception) { return; }
        if (!_pending.TryRemove(id, out var tcs)) return;

        if (obj["ok"]?.GetValue<bool>() == true)
        {
            tcs.TrySetResult(obj["result"]);
            return;
        }

        var error = obj["error"] as JsonObject;
        var message = error?["message"]?.GetValue<string>() ?? "Неизвестная ошибка sidecar";
        var sqlState = error?["sqlState"]?.GetValue<string>();
        var className = error?["class"]?.GetValue<string>();
        int? code = null;
        try
        {
            if (error?["code"] is JsonNode c && c.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                code = c.GetValue<int>();
        }
        catch (Exception) { }
        tcs.TrySetException(new SidecarException(message, sqlState, code, className));
    }

    private void OnProcessLost(string reason)
    {
        if (_stopping) return;
        foreach (var (id, tcs) in _pending)
        {
            if (_pending.TryRemove(id, out _))
                tcs.TrySetException(new SidecarException(reason, lost: true));
        }
    }

    public async Task<JsonNode?> CallAsync(string method, object? parameters = null,
        CancellationToken ct = default)
    {
        await StartAsync(ct).ConfigureAwait(false);
        var id = Interlocked.Increment(ref _seq);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new JsonObject { ["id"] = id, ["method"] = method };
        if (parameters is not null)
            request["params"] = JsonSerializer.SerializeToNode(parameters, ProtocolJson.Options);

        try
        {
            WriteLine(request.ToJsonString(ProtocolJson.Options));
        }
        catch (Exception e)
        {
            _pending.TryRemove(id, out _);
            throw new SidecarException("Не удалось отправить запрос sidecar", lost: true, inner: e);
        }

        using var registration = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
                pending.TrySetCanceled(ct);
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    private void WriteLine(string line)
    {
        lock (_writeLock)
        {
            if (_writer is null) throw new SidecarException("Sidecar не запущен", lost: true);
            _writer.WriteLine(line);
        }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await CallAsync("ping", new { }, ct).ConfigureAwait(false);
            return result?["pong"]?.GetValue<bool>() == true;
        }
        catch (SidecarException) { return false; }
    }

    public async Task StopAsync()
    {
        _stopping = true;
        try
        {
            if (_process is { HasExited: false })
            {
                try { _process.Kill(entireProcessTree: true); } catch (Exception) { }
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _writer = null;
        }
        OnProcessLost("Sidecar остановлен");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _startGate.Dispose();
    }
}

internal static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}

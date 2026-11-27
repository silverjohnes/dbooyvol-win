using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DBooyvol.Core.Models;

namespace DBooyvol.Core.Db;

/// <summary>
/// Local port forwarding through the system OpenSSH client. Uses
/// <c>ssh -N -L</c> so <c>~/.ssh/config</c>, keys, the agent and ProxyJump all
/// behave exactly as in a terminal.
/// </summary>
public sealed class SshTunnel : IDisposable
{
    private Process? _process;
    public int LocalPort { get; private set; }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    public static bool IsOpen(int port, int timeoutMs = 500)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(IPAddress.Loopback, port);
            return task.Wait(timeoutMs) && client.Connected;
        }
        catch (Exception) { return false; }
    }

    public static string FindSshExecutable()
    {
        var windows = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "OpenSSH", "ssh.exe");
        if (File.Exists(windows)) return windows;
        return "ssh.exe";
    }

    public static async Task<SshTunnel> StartAsync(SshProfile ssh, string remoteHost, int remotePort,
        string sshDirectory, string? sshExecutable = null, CancellationToken ct = default)
    {
        var host = ssh.Host.Trim();
        if (host.Length == 0)
            throw new InvalidOperationException(
                "SSH включён, но хост не указан. Укажите хост или алиас из ~/.ssh/config — либо выключите SSH в профиле.");

        var port = FreePort();
        if (port <= 0) throw new InvalidOperationException("Не удалось выделить локальный порт для SSH-туннеля");

        var tunnel = new SshTunnel { LocalPort = port };
        var args = new List<string>
        {
            "-N",
            "-o", "ExitOnForwardFailure=yes",
            "-o", "BatchMode=yes",
            "-o", "ConnectTimeout=10",
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", "ServerAliveInterval=30",
            "-L", $"127.0.0.1:{port}:{remoteHost}:{remotePort}"
        };

        var config = Path.Combine(sshDirectory, "config");
        if (File.Exists(config)) { args.Add("-F"); args.Add(config); }
        if (!string.IsNullOrWhiteSpace(ssh.KeyPath))
        {
            args.Add("-i");
            args.Add(Storage.AppSettings.Expand(ssh.KeyPath));
        }
        args.Add(string.IsNullOrWhiteSpace(ssh.User) ? host : $"{ssh.User}@{host}");
        if (ssh.Port > 0 && ssh.Port != 22) { args.Insert(0, "-p"); args.Insert(1, ssh.Port.ToString()); }

        var psi = new ProcessStartInfo
        {
            FileName = sshExecutable ?? FindSshExecutable(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException("ssh не запустился"); }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                "Не удалось запустить ssh.exe. Установите OpenSSH Client: Параметры → Приложения → Дополнительные компоненты.", e);
        }
        tunnel._process = process;

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                tunnel.Dispose();
                throw new InvalidOperationException(
                    $"SSH-туннель завершился: {error.Trim()}".TrimEnd(':', ' '));
            }
            if (IsOpen(port)) return tunnel;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        tunnel.Dispose();
        throw new TimeoutException("SSH-туннель не открылся за 15 секунд");
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
            _process?.Dispose();
        }
        catch (Exception) { }
        _process = null;
    }
}

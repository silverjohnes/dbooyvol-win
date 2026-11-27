using System.IO;
using System.Security.Cryptography;
using System.Text;
using DBooyvol.Core.Storage;

namespace DBooyvol.App.Services;

/// <summary>DPAPI in the current user's context. Never machine scope.</summary>
public sealed class WindowsSecretProtector : ISecretProtector
{
    public string Name => "DPAPI (CurrentUser)";

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext) =>
        ProtectedData.Unprotect(ciphertext, optionalEntropy: null, DataProtectionScope.CurrentUser);
}

/// <summary>
/// Windows Hello / PIN confirmation before the first password read of a
/// session. The WinRT type is resolved at runtime so the project still builds
/// on macOS; when the API is unavailable the app asks for a plain confirmation.
/// </summary>
public sealed class HelloGate
{
    private bool _passed;

    public async Task<bool> ConfirmAsync(CancellationToken ct = default)
    {
        if (_passed) return true;
        try
        {
            var type = Type.GetType(
                "Windows.Security.Credentials.UI.UserConsentVerifier, Windows, ContentType=WindowsRuntime");
            var statusType = Type.GetType(
                "Windows.Security.Credentials.UI.UserConsentVerificationResult, Windows, ContentType=WindowsRuntime");
            if (type is not null && statusType is not null)
            {
                var method = type.GetMethod("RequestVerificationAsync",
                    new[] { typeof(string) });
                if (method?.Invoke(null, new object[] { "DBooyvol: доступ к сохранённым паролям" })
                    is { } operation)
                {
                    var asTask = operation.GetType().GetMethod("AsTask", Type.EmptyTypes);
                    if (asTask?.Invoke(operation, null) is Task task)
                    {
                        await task.ConfigureAwait(false);
                        var result = task.GetType().GetProperty("Result")?.GetValue(task);
                        var text = result?.ToString() ?? "";
                        if (text.Contains("Verified", StringComparison.OrdinalIgnoreCase))
                        {
                            _passed = true;
                            return true;
                        }
                        return false;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the explicit confirmation.
        }

        var answer = System.Windows.MessageBox.Show(
            "Подтвердить доступ к сохранённым паролям?",
            "DBooyvol",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (answer == System.Windows.MessageBoxResult.Yes)
        {
            _passed = true;
            return true;
        }
        return false;
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? error = null) =>
        Write("ERROR", error is null ? message : $"{message}: {error}");

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            var path = Path.Combine(AppPaths.Logs,
                $"dbooyvol-{DateTime.Now:yyyy-MM-dd}.log");
            lock (Gate)
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {level} {message}\n", Encoding.UTF8);
        }
        catch (Exception)
        {
            // Logging must never take the app down.
        }
    }
}

public static class JavaLocator
{
    /// <summary>Settings → bundled jlink runtime → PATH.</summary>
    public static string Resolve(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var expanded = AppSettings.Expand(configured);
            if (File.Exists(expanded)) return expanded;
        }

        var bundled = AppPaths.RuntimeJava;
        if (File.Exists(bundled)) return bundled;

        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            var candidate = Path.Combine(home, "bin", "java.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return "java.exe";
    }
}

using System.Security.Cryptography;
using System.Text;

namespace DBooyvol.Core.Storage;

/// <summary>
/// Platform key protection. Windows uses DPAPI in the current user's context
/// (<c>CryptProtectData</c> without <c>CRYPTPROTECT_LOCAL_MACHINE</c>).
/// </summary>
public interface ISecretProtector
{
    string Name { get; }
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

public enum SecretErrorKind
{
    /// <summary>The requested account has no ciphertext.</summary>
    NotFound,
    /// <summary>DPAPI/Hello refused access. This is NOT "key missing".</summary>
    AccessDenied,
    /// <summary>Ciphertext exists but was encrypted with another master key.</summary>
    WrongKey,
    /// <summary>Ciphertext exists but the master key item is gone.</summary>
    KeyMissing,
    InvalidFormat
}

public sealed class SecretStoreException : Exception
{
    public SecretErrorKind Kind { get; }
    public string? Account { get; }

    public SecretStoreException(SecretErrorKind kind, string message, string? account = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Account = account;
    }
}

/// <summary>
/// <c>enc2:&lt;keyid&gt;:&lt;base64(nonce+ciphertext+tag)&gt;</c>.
/// The key id is the first 8 hex characters of SHA-256(master key), so a key
/// can be compared with a ciphertext without decrypting it.
/// </summary>
public static class SecretEnvelope
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static string KeyId(byte[] key) =>
        Convert.ToHexString(SHA256.HashData(key))[..8].ToLowerInvariant();

    public static string Encrypt(byte[] key, string plaintext)
    {
        if (key.Length != 32) throw new ArgumentException("Master key must be 32 bytes", nameof(key));
        var data = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, data, cipher, tag);

        var payload = new byte[NonceSize + cipher.Length + TagSize];
        nonce.CopyTo(payload, 0);
        cipher.CopyTo(payload, NonceSize);
        tag.CopyTo(payload, NonceSize + cipher.Length);
        return $"enc2:{KeyId(key)}:{Convert.ToBase64String(payload)}";
    }

    public static string Decrypt(byte[] key, string envelope)
    {
        var parts = envelope.Split(':');
        if (parts.Length != 3 || parts[0] != "enc2")
            throw new SecretStoreException(SecretErrorKind.InvalidFormat, "Неверный формат секрета");

        var keyId = KeyId(key);
        if (!string.Equals(parts[1], keyId, StringComparison.OrdinalIgnoreCase))
            throw new SecretStoreException(SecretErrorKind.WrongKey,
                "Секрет зашифрован другим мастер-ключом");

        byte[] payload;
        try { payload = Convert.FromBase64String(parts[2]); }
        catch (FormatException e)
        {
            throw new SecretStoreException(SecretErrorKind.InvalidFormat, "Повреждённый секрет", inner: e);
        }
        if (payload.Length < NonceSize + TagSize)
            throw new SecretStoreException(SecretErrorKind.InvalidFormat, "Повреждённый секрет");

        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(payload.Length - TagSize, TagSize);
        var cipher = payload.AsSpan(NonceSize, payload.Length - NonceSize - TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        try
        {
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException e)
        {
            throw new SecretStoreException(SecretErrorKind.WrongKey, "Не удалось расшифровать секрет", inner: e);
        }
        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>
/// Password storage with a master key protected by the OS. The rules from the
/// macOS build are preserved literally: an access error is never treated as
/// "no key", a new key is only created when the item is genuinely absent and
/// <c>secrets.json</c> is empty, and rotation is an explicit operation.
/// </summary>
public sealed class SecretStore
{
    private readonly string _secretsPath;
    private readonly string _keyPath;
    private readonly ISecretProtector _protector;
    private readonly Func<CancellationToken, Task<bool>>? _authorize;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _secrets;
    private byte[]? _key;
    private bool _authorized;

    public SecretStore(string secretsPath, string keyPath, ISecretProtector protector,
        Func<CancellationToken, Task<bool>>? authorize = null)
    {
        _secretsPath = secretsPath;
        _keyPath = keyPath;
        _protector = protector;
        _authorize = authorize;
    }

    public string ProtectorName => _protector.Name;

    public async Task AuthenticateOnceAsync(CancellationToken ct = default)
    {
        if (_authorized || _authorize is null) { _authorized = true; return; }
        var ok = await _authorize(ct).ConfigureAwait(false);
        if (!ok)
            throw new SecretStoreException(SecretErrorKind.AccessDenied,
                "Не удалось подтвердить доступ к сохранённым паролям");
        _authorized = true;
    }

    private Dictionary<string, string> Secrets
    {
        get
        {
            if (_secrets is not null) return _secrets;
            try
            {
                _secrets = Json.Read<Dictionary<string, string>>(_secretsPath)
                           ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch (Exception e)
            {
                throw new SecretStoreException(SecretErrorKind.InvalidFormat,
                    "Не удалось прочитать secrets.json", inner: e);
            }
            return _secrets;
        }
    }

    private void Persist()
    {
        Json.Write(_secretsPath, Secrets);
    }

    /// <summary>Loads the master key without ever creating or replacing it.</summary>
    private byte[] LoadKey()
    {
        if (_key is not null) return _key;
        if (!File.Exists(_keyPath))
        {
            if (Secrets.Count > 0)
                throw new SecretStoreException(SecretErrorKind.KeyMissing,
                    "Мастер-ключ не найден, но сохранённые пароли есть");
            _key = CreateKey();
            return _key;
        }

        byte[] protectedBytes;
        try { protectedBytes = File.ReadAllBytes(_keyPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SecretStoreException(SecretErrorKind.AccessDenied,
                "Нет доступа к файлу мастер-ключа", inner: e);
        }

        try
        {
            var key = _protector.Unprotect(protectedBytes);
            if (key.Length != 32)
                throw new SecretStoreException(SecretErrorKind.InvalidFormat, "Мастер-ключ повреждён");
            _key = key;
            return key;
        }
        catch (SecretStoreException) { throw; }
        catch (Exception e)
        {
            // DPAPI failure is an access problem, not an absent key.
            throw new SecretStoreException(SecretErrorKind.AccessDenied,
                "Не удалось получить мастер-ключ. Это не значит, что он отсутствует.", inner: e);
        }
    }

    private byte[] CreateKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var protectedBytes = _protector.Protect(key);
        Json.AtomicWrite(_keyPath, Convert.ToBase64String(protectedBytes));
        return key;
    }

    public string? Get(string account)
    {
        _gate.Wait();
        try
        {
            if (!Secrets.TryGetValue(account, out var envelope)) return null;
            var key = LoadKey();
            try { return SecretEnvelope.Decrypt(key, envelope); }
            catch (SecretStoreException e) when (e.Kind == SecretErrorKind.WrongKey)
            {
                throw new SecretStoreException(SecretErrorKind.WrongKey,
                    "Сохранённый пароль зашифрован другим ключом", account, e);
            }
        }
        finally { _gate.Release(); }
    }

    public void Save(string account, string password)
    {
        _gate.Wait();
        try
        {
            var key = LoadKey();
            Secrets[account] = SecretEnvelope.Encrypt(key, password);
            Persist();
        }
        finally { _gate.Release(); }
    }

    public void Delete(string account)
    {
        _gate.Wait();
        try
        {
            if (Secrets.Remove(account)) Persist();
        }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<string> StaleAccounts()
    {
        _gate.Wait();
        try
        {
            var result = new List<string>();
            var key = LoadKey();
            var keyId = SecretEnvelope.KeyId(key);
            foreach (var (account, envelope) in Secrets)
            {
                var parts = envelope.Split(':');
                if (parts.Length != 3 || !string.Equals(parts[1], keyId, StringComparison.OrdinalIgnoreCase))
                    result.Add(account);
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    public bool HasStaleSecrets() => StaleAccounts().Count > 0;

    /// <summary>Explicit "re-encrypt all passwords" operation: the only path
    /// that replaces the master key.</summary>
    public int Rotate(Func<string, string?> reenterPassword)
    {
        _gate.Wait();
        try
        {
            var oldKey = LoadKey();
            var plain = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (account, envelope) in Secrets)
            {
                try { plain[account] = SecretEnvelope.Decrypt(oldKey, envelope); }
                catch (SecretStoreException)
                {
                    var value = reenterPassword(account);
                    if (value is not null) plain[account] = value;
                }
            }

            var newKey = RandomNumberGenerator.GetBytes(32);
            Json.AtomicWrite(_keyPath, Convert.ToBase64String(_protector.Protect(newKey)));
            _key = newKey;
            _secrets = plain.ToDictionary(kv => kv.Key,
                kv => SecretEnvelope.Encrypt(newKey, kv.Value), StringComparer.Ordinal);
            Persist();
            return _secrets.Count;
        }
        finally { _gate.Release(); }
    }

    public string Diagnostics()
    {
        var key = LoadKey();
        return $"protector={_protector.Name} keyid={SecretEnvelope.KeyId(key)} " +
               $"accounts={Secrets.Count} stale={StaleAccounts().Count}";
    }
}

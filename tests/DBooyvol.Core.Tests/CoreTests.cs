using System.Security.Cryptography;
using System.Text;
using DBooyvol.Core.Models;
using DBooyvol.Core.Shortcuts;
using DBooyvol.Core.Storage;
using DBooyvol.Core.Text;
using Xunit;

namespace DBooyvol.Core.Tests;

public sealed class FakeProtector : ISecretProtector
{
    public bool Fail { get; set; }
    public string Name => "fake";

    public byte[] Protect(byte[] plaintext)
    {
        if (Fail) throw new CryptographicException("denied");
        return Encoding.UTF8.GetBytes("P:" + Convert.ToBase64String(plaintext));
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        if (Fail) throw new CryptographicException("denied");
        var text = Encoding.UTF8.GetString(ciphertext);
        if (!text.StartsWith("P:", StringComparison.Ordinal))
            throw new CryptographicException("bad blob");
        return Convert.FromBase64String(text[2..]);
    }
}

public sealed class SecretStoreTests : IDisposable
{
    private readonly string _dir;

    public SecretStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dbooyvol-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private SecretStore Store(FakeProtector protector) => new(
        Path.Combine(_dir, "secrets.json"),
        Path.Combine(_dir, "master.key"),
        protector);

    [Fact]
    public void RoundTripAndDelete()
    {
        var store = Store(new FakeProtector());
        store.Save("p1", "s3cret");
        Assert.Equal("s3cret", store.Get("p1"));
        store.Delete("p1");
        Assert.Null(store.Get("p1"));
    }

    [Fact]
    public void EnvelopeUsesKeyIdAndAesGcm()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var envelope = SecretEnvelope.Encrypt(key, "hello");
        Assert.StartsWith("enc2:", envelope);
        Assert.Equal(3, envelope.Split(':').Length);
        Assert.Equal("hello", SecretEnvelope.Decrypt(key, envelope));

        var other = RandomNumberGenerator.GetBytes(32);
        var error = Assert.Throws<SecretStoreException>(() => SecretEnvelope.Decrypt(other, envelope));
        Assert.Equal(SecretErrorKind.WrongKey, error.Kind);
    }

    [Fact]
    public void TamperedCiphertextIsRejected()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var envelope = SecretEnvelope.Encrypt(key, "hello");
        var parts = envelope.Split(':');
        var bytes = Convert.FromBase64String(parts[2]);
        bytes[^1] ^= 0xFF;
        var tampered = $"enc2:{parts[1]}:{Convert.ToBase64String(bytes)}";
        var error = Assert.Throws<SecretStoreException>(() => SecretEnvelope.Decrypt(key, tampered));
        Assert.Equal(SecretErrorKind.WrongKey, error.Kind);
    }

    [Fact]
    public void AccessErrorIsNotTreatedAsMissingKey()
    {
        var protector = new FakeProtector();
        Store(protector).Save("p1", "x");
        protector.Fail = true;

        // A fresh instance has no cached master key, so it must consult DPAPI.
        var error = Assert.Throws<SecretStoreException>(() => Store(protector).Get("p1"));
        Assert.Equal(SecretErrorKind.AccessDenied, error.Kind);
    }

    [Fact]
    public void ExistingSecretsWithoutKeyAreReportedAsKeyMissing()
    {
        File.WriteAllText(Path.Combine(_dir, "secrets.json"),
            "{\"p1\":\"enc2:deadbeef:AAAA\"}");
        var store = Store(new FakeProtector());
        var error = Assert.Throws<SecretStoreException>(() => store.Get("p1"));
        Assert.Equal(SecretErrorKind.KeyMissing, error.Kind);
    }

    [Fact]
    public void RotateReencryptsEverything()
    {
        var store = Store(new FakeProtector());
        store.Save("a", "1");
        store.Save("b", "2");
        Assert.Equal(2, store.Rotate(_ => null));
        Assert.Equal("1", store.Get("a"));
        Assert.Equal("2", store.Get("b"));
        Assert.False(store.HasStaleSecrets());
    }

    [Fact]
    public void MissingAccountReturnsNull()
    {
        Assert.Null(Store(new FakeProtector()).Get("nope"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }
}

public class ShortcutTests
{
    [Fact]
    public void DBeaverDefaultsArePresent()
    {
        Assert.Equal(new KeyGestureSpec(Vk.Enter, ShortcutModifiers.Control),
            ShortcutCatalog.Find("sql.executeStatement")!.Default);
        Assert.Equal(new KeyGestureSpec('X', ShortcutModifiers.Alt),
            ShortcutCatalog.Find("sql.executeScript")!.Default);
        Assert.Equal(new KeyGestureSpec(Vk.F4, ShortcutModifiers.Control),
            ShortcutCatalog.Find("tab.close")!.Default);
        Assert.Equal(new KeyGestureSpec(Vk.Oem2, ShortcutModifiers.Control),
            ShortcutCatalog.Find("edit.comment")!.Default);
        Assert.Equal(new KeyGestureSpec(Vk.Tab, ShortcutModifiers.None),
            ShortcutCatalog.Find("result.toggleRow")!.Default);
    }

    [Fact]
    public void SerializationRoundTrip()
    {
        var spec = new KeyGestureSpec(Vk.Enter, ShortcutModifiers.Control | ShortcutModifiers.Shift);
        Assert.Equal(spec, KeyGestureSpec.Parse(spec.Serialize()));
        Assert.Equal("Ctrl+Shift+Enter", spec.Display);
    }

    [Fact]
    public void ConflictsAreRejectedAcrossOverlappingScopes()
    {
        var registry = new ShortcutRegistry();
        var error = Assert.Throws<ShortcutConflictException>(() =>
            registry.Set("sql.format", new KeyGestureSpec(Vk.Enter, ShortcutModifiers.Control)));
        Assert.Equal("sql.executeStatement", error.OtherActionId);
    }

    [Fact]
    public void SameGestureInDifferentScopesIsAllowed()
    {
        var registry = new ShortcutRegistry();
        registry.Set("grid.copyAbove", new KeyGestureSpec(Vk.F7, ShortcutModifiers.None));
        registry.Set("edit.comment", new KeyGestureSpec(Vk.F7, ShortcutModifiers.None));
        Assert.Equal(new KeyGestureSpec(Vk.F7, ShortcutModifiers.None), registry.Get("edit.comment"));
    }

    [Fact]
    public void FixedActionCannotBeRebound()
    {
        var registry = new ShortcutRegistry();
        Assert.Throws<InvalidOperationException>(() =>
            registry.Set("result.toggleRow", new KeyGestureSpec('T', ShortcutModifiers.Control)));
    }

    [Fact]
    public void MatchRespectsScope()
    {
        var registry = new ShortcutRegistry();
        var grid = registry.Match(new KeyGestureSpec('D', ShortcutModifiers.Control), ShortcutScope.Grid);
        Assert.Contains(grid, a => a.Id == "grid.copyAbove");
        var editor = registry.Match(new KeyGestureSpec('D', ShortcutModifiers.Control), ShortcutScope.Editor);
        Assert.DoesNotContain(editor, a => a.Id == "grid.copyAbove");
    }
}

public class SettingsAndTextTests
{
    [Fact]
    public void SettingsAreClamped()
    {
        var settings = new AppSettings
        {
            EditorFontSize = 100,
            PageSize = 0,
            SidebarWidth = 10,
            EditorHeightRatio = 2,
            FormatterIndent = 1
        };
        settings.Normalize();
        Assert.Equal(32, settings.EditorFontSize);
        Assert.Equal(1, settings.PageSize);
        Assert.Equal(190, settings.SidebarWidth);
        Assert.Equal(0.8, settings.EditorHeightRatio);
        Assert.Equal(2, settings.FormatterIndent);
    }

    [Fact]
    public void TimestampIsConvertedForDisplayOnly()
    {
        var tz = TimeZoneInfo.CreateCustomTimeZone("test+3", TimeSpan.FromHours(3), "test+3", "test+3");
        Assert.Equal("2024-01-01 13:00:00",
            TimestampDisplay.Formatted("2024-01-01 10:00:00", "timestamp", tz));
        Assert.Equal("2024-01-01 13:00:00.250",
            TimestampDisplay.Formatted("2024-01-01T10:00:00.250Z", "timestamptz", tz));
        Assert.Equal("not a date",
            TimestampDisplay.Formatted("not a date", "timestamp", tz));
        Assert.Equal("2024-01-01",
            TimestampDisplay.Formatted("2024-01-01", "date", tz));
    }

    [Fact]
    public void NumericTextAcceptsBothSeparators()
    {
        Assert.True(NumericText.IsNumeric("1.5"));
        Assert.True(NumericText.IsNumeric("-1,5"));
        Assert.False(NumericText.IsNumeric("1.2.3"));
        Assert.False(NumericText.IsNumeric("abc"));
    }

    [Fact]
    public void ProfileCloneCanChangeId()
    {
        var profile = new Profile { Name = "p", Kind = "postgres", Host = "h" };
        var copy = profile.Clone(newId: true);
        Assert.NotEqual(profile.Id, copy.Id);
        Assert.Equal(profile.Name, copy.Name);
        Assert.Equal(5432, profile.EffectivePort);
    }
}

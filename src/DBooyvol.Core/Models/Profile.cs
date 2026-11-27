using System.Text.Json.Serialization;

namespace DBooyvol.Core.Models;

/// <summary>
/// A saved connection. Field names and defaults mirror the macOS
/// <c>profiles.json</c> so a profile file can be imported unchanged.
/// </summary>
public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString().ToUpperInvariant();
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "postgres";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Database { get; set; } = "";
    public string User { get; set; } = "";
    public string DriverPath { get; set; } = "";
    public string JdbcUrl { get; set; } = "";
    public bool OracleSid { get; set; }
    public SshProfile Ssh { get; set; } = new();
    public bool ReadOnly { get; set; } = true;
    public bool SavePassword { get; set; } = true;
    public string OracleMode { get; set; } = "easy";
    public string OracleAlias { get; set; } = "";
    public string TnsAdmin { get; set; } = "";
    public string LdapHost { get; set; } = "";
    public int LdapPort { get; set; } = 389;
    public string LdapContext { get; set; } = "";

    [JsonPropertyName("ldapSSL")]
    public bool LdapSsl { get; set; }

    public string OracleAuthServices { get; set; } = "";
    public string InstantClientDir { get; set; } = "";

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Database : Name;

    public int DefaultPort => Kind switch
    {
        "postgres" => 5432,
        "oracle" => 1521,
        "clickhouse" => 8123,
        _ => 0
    };

    public int EffectivePort => Port > 0 ? Port : DefaultPort;

    public string UrlDescription
    {
        get
        {
            if (Kind == "sqlite") return Database;
            if (!string.IsNullOrWhiteSpace(JdbcUrl)) return JdbcUrl;
            return $"{Kind}://{Host}:{EffectivePort}/{Database}";
        }
    }

    public Profile Clone(bool newId = false) => new()
    {
        Id = newId ? Guid.NewGuid().ToString().ToUpperInvariant() : Id,
        Name = Name,
        Kind = Kind,
        Host = Host,
        Port = Port,
        Database = Database,
        User = User,
        DriverPath = DriverPath,
        JdbcUrl = JdbcUrl,
        OracleSid = OracleSid,
        Ssh = Ssh.Clone(),
        ReadOnly = ReadOnly,
        SavePassword = SavePassword,
        OracleMode = OracleMode,
        OracleAlias = OracleAlias,
        TnsAdmin = TnsAdmin,
        LdapHost = LdapHost,
        LdapPort = LdapPort,
        LdapContext = LdapContext,
        LdapSsl = LdapSsl,
        OracleAuthServices = OracleAuthServices,
        InstantClientDir = InstantClientDir
    };
}

public sealed class SshProfile
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public string KeyPath { get; set; } = "";

    public SshProfile Clone() => new()
    {
        Enabled = Enabled, Host = Host, Port = Port, User = User, KeyPath = KeyPath
    };
}

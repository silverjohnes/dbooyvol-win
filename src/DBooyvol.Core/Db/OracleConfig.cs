namespace DBooyvol.Core.Db;

/// <summary>Reads aliases from tnsnames.ora and LDAP settings from ldap.ora.</summary>
public static class OracleConfig
{
    public sealed class TnsEntry
    {
        public string Alias { get; init; } = "";
        public string Description { get; init; } = "";
    }

    public sealed class LdapEntry
    {
        public string Host { get; init; } = "";
        public int Port { get; init; } = 389;
        public string Context { get; init; } = "";
        public bool Ssl { get; init; }
    }

    public static List<TnsEntry> ReadTnsNames(string tnsAdminDirectory)
    {
        var path = Path.Combine(tnsAdminDirectory, "tnsnames.ora");
        if (!File.Exists(path)) return new List<TnsEntry>();

        var entries = new List<TnsEntry>();
        var text = File.ReadAllText(path);
        var currentAlias = "";
        var depth = 0;
        var body = new System.Text.StringBuilder();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0) continue;

            if (depth == 0)
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                currentAlias = line[..eq].Trim().Trim('"');
                body.Clear();
                var rest = line[(eq + 1)..];
                depth += rest.Count(c => c == '(') - rest.Count(c => c == ')');
                body.Append(rest);
                if (depth <= 0) Finish();
            }
            else
            {
                depth += line.Count(c => c == '(') - line.Count(c => c == ')');
                body.Append(' ').Append(line);
                if (depth <= 0) Finish();
            }
        }
        return entries;

        void Finish()
        {
            if (currentAlias.Length > 0)
                entries.Add(new TnsEntry { Alias = currentAlias, Description = body.ToString().Trim() });
            currentAlias = "";
            body.Clear();
            depth = 0;
        }
    }

    public static LdapEntry? ReadLdap(string tnsAdminDirectory)
    {
        var path = Path.Combine(tnsAdminDirectory, "ldap.ora");
        if (!File.Exists(path)) return null;

        string? host = null, context = null;
        var port = 389;
        var ssl = false;
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = StripComment(rawLine).Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().ToUpperInvariant();
            var value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "DIRECTORY_SERVERS":
                {
                    var first = value.Split(',')[0].Trim();
                    var parts = first.Split(':');
                    host = parts[0];
                    if (parts.Length > 1 && int.TryParse(parts[1], out var p)) port = p;
                    break;
                }
                case "DEFAULT_ADMIN_CONTEXT":
                    context = value.Trim('"');
                    break;
                case "DIRECTORY_SERVER_TYPE":
                    ssl = value.Contains("ssl", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(host)) return null;
        return new LdapEntry { Host = host, Port = port, Context = context ?? "", Ssl = ssl };
    }

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        return hash >= 0 ? line[..hash] : line;
    }
}

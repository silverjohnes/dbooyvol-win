using System.Text.RegularExpressions;
using Xunit;

namespace DBooyvol.Security.Tests;

/// <summary>
/// SAST-гейт для DBooyvol. Проверяет не абстрактный «безопасный код», а
/// конкретные технологии продукта: WPF/.NET 9, Java-sidecar, JDBC,
/// DPAPI + AES-GCM, OpenSSH, Inno Setup и GitHub Actions.
/// </summary>
public class SastTests
{
    public static IEnumerable<object[]> RuleIds =>
        SastCatalog.Rules.Select(rule => new object[] { rule.Id });

    [Fact]
    public void Repository_has_no_security_findings()
    {
        var findings = SastScanner.Scan(Repository.SourceFiles());
        Assert.True(findings.Count == 0,
            "SAST нашёл проблемы:\n" + string.Join("\n", findings));
    }

    [Fact]
    public void Scanner_covers_every_technology_layer()
    {
        var files = Repository.SourceFiles();
        Assert.True(files.Count >= 30, $"Просканировано слишком мало файлов: {files.Count}");
        foreach (var extension in new[] { ".cs", ".java", ".ps1", ".sh", ".iss", ".yml" })
            Assert.Contains(files, f => f.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

        foreach (var required in new[]
                 {
                     "src/DBooyvol.Core/Protocol/SidecarClient.cs",
                     "src/DBooyvol.Core/Storage/SecretStore.cs",
                     "src/DBooyvol.App/Services/Platform.cs",
                     "sidecar/src/main/java/silver/dbooyvol/Engine.java",
                     "sidecar/build.ps1",
                     "sidecar/build.sh",
                     "installer/DBooyvol.iss",
                     ".github/workflows/build.yml"
                 })
        {
            Assert.Contains(files, f => f.Path == required);
        }

        Assert.DoesNotContain(files, f =>
            f.Path.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || f.Path.Contains("/obj/", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(RuleIds))]
    public void Every_rule_detects_its_positive_sample(string ruleId)
    {
        var rule = SastCatalog.ById(ruleId);
        var findings = SastScanner.ScanText(
            "sample" + rule.Extensions[0], rule.PositiveSample, new[] { rule });
        Assert.Contains(findings, f => f.RuleId == ruleId);
    }

    [Fact]
    public void Safe_patterns_are_not_flagged()
    {
        var safe = new (string Path, string Text)[]
        {
            ("safe.cs", "using var aes = new AesGcm(key, 16);"),
            ("safe.cs", "var nonce = RandomNumberGenerator.GetBytes(12);"),
            ("safe.cs", "psi.ArgumentList.Add(arg);"),
            ("safe.cs", "UseShellExecute = false,"),
            ("safe.cs", "ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);"),
            ("safe.cs", "var sql = \"SELECT * FROM t WHERE id = ?\";"),
            ("safe.cs", "settings.DtdProcessing = DtdProcessing.Prohibit;"),
            ("safe.java", "PreparedStatement ps = c.prepareStatement(\"UPDATE t SET a = ? WHERE id = ?\");"),
            ("safe.java", "try (Statement st = c.createStatement()) { st.execute(userSql); }"),
            ("safe.yml", "env:\n  TOKEN: ${{ secrets.TOKEN }}\nrun: echo \"$env:TOKEN\"")
        };

        var findings = SastScanner.Scan(safe);
        Assert.True(findings.Count == 0,
            "Ложные срабатывания:\n" + string.Join("\n", findings));
    }

    [Fact]
    public void Secret_storage_uses_aes_gcm_and_user_scoped_dpapi()
    {
        var store = Repository.Read("src/DBooyvol.Core/Storage/SecretStore.cs");
        Assert.Contains("new AesGcm", store, StringComparison.Ordinal);
        Assert.Contains("RandomNumberGenerator.GetBytes(32)", store, StringComparison.Ordinal);
        Assert.DoesNotContain("AesManaged", store, StringComparison.Ordinal);

        var platform = Repository.Read("src/DBooyvol.App/Services/Platform.cs");
        Assert.Contains("DataProtectionScope.CurrentUser", platform, StringComparison.Ordinal);
        Assert.DoesNotContain("DataProtectionScope.LocalMachine", platform, StringComparison.Ordinal);
    }

    [Fact]
    public void Sidecar_process_uses_argument_list_and_no_shell()
    {
        var client = Repository.Read("src/DBooyvol.Core/Protocol/SidecarClient.cs");
        Assert.Contains("ArgumentList.Add", client, StringComparison.Ordinal);
        Assert.DoesNotContain("psi.Arguments", client, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments = args", client, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = false", client, StringComparison.Ordinal);
    }

    [Fact]
    public void Ssh_tunnel_keeps_host_key_verification()
    {
        var tunnel = Repository.Read("src/DBooyvol.Core/Db/SshTunnel.cs");
        Assert.Contains("StrictHostKeyChecking=accept-new", tunnel, StringComparison.Ordinal);
        Assert.DoesNotContain("StrictHostKeyChecking=no", tunnel, StringComparison.Ordinal);
    }

    [Fact]
    public void Jdbc_writes_use_prepared_statements_and_quoted_identifiers()
    {
        var engine = Repository.Read("sidecar/src/main/java/silver/dbooyvol/Engine.java");
        Assert.Contains("prepareStatement", engine, StringComparison.Ordinal);
        Assert.Contains("quoteIdentifier", engine, StringComparison.Ordinal);
        Assert.Contains("bind(ps", engine, StringComparison.Ordinal);
        Assert.Contains("setAutoCommit(false)", engine, StringComparison.Ordinal);
        Assert.Contains("rollback()", engine, StringComparison.Ordinal);
    }

    [Fact]
    public void Gitignore_covers_secret_material_and_build_output()
    {
        var ignore = Repository.Read(".gitignore");
        foreach (var entry in new[]
                 {
                     "*.pfx", "*.p12", "*.pem", "*.key", ".env",
                     "runtime/jre/", "sidecar/build/", "publish/", "*.user"
                 })
        {
            Assert.Contains(entry, ignore, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void No_certificate_or_private_key_files_are_committed()
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pfx", ".p12", ".pem", ".key", ".jks", ".keystore", ".env"
        };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", "secrets.json"
        };

        var found = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Repository.Root, "*", SearchOption.AllDirectories))
        {
            var normalized = path.Replace('\\', '/');
            if (normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/runtime/jre/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/sidecar/build/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/publish/", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = Path.GetFileName(path);
            if (forbidden.Contains(Path.GetExtension(path)) || names.Contains(name))
                found.Add(normalized);
        }

        Assert.True(found.Count == 0,
            "В репозитории найдены секретные файлы:\n" + string.Join("\n", found));
    }

    [Fact]
    public void No_banned_dependencies()
    {
        var banned = new[] { "Newtonsoft.Json", "System.Data.SqlClient", "log4j-core", "commons-collections" };
        var packages = new List<(string File, string Id)>();
        var pattern = new Regex(@"PackageReference\s+Include=""([^""]+)""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        foreach (var (path, text) in Repository.SourceFiles()
                     .Where(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (Match match in pattern.Matches(text))
                packages.Add((path, match.Groups[1].Value));
        }

        var hits = packages
            .Where(p => banned.Any(b => p.Id.Equals(b, StringComparison.OrdinalIgnoreCase)))
            .Select(p => $"{p.File}: {p.Id}")
            .ToList();
        Assert.True(hits.Count == 0, "Запрещённые зависимости:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void Ci_runs_the_security_gate()
    {
        Assert.True(Repository.Exists(".github/workflows/build.yml"),
            "CI workflow отсутствует");
        var workflow = Repository.Read(".github/workflows/build.yml");
        Assert.Contains("DBooyvol.Security.Tests", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet test", workflow, StringComparison.Ordinal);
    }
}

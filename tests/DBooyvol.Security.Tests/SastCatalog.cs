using System.Text.RegularExpressions;

namespace DBooyvol.Security.Tests;

public static class SastCatalog
{
    public static readonly IReadOnlyList<SastRule> Rules = new List<SastRule>
    {
        new()
        {
            Id = "DBY001",
            Description = "Похоже на захардкоженный пароль, токен или секрет",
            Pattern = @"\b(password|passwd|pwd|secret|api[_-]?key|access[_-]?token)\b\s*=\s*""[^""\r\n]{6,}""",
            Extensions = new[] { ".cs", ".java" },
            PositiveSample = "var password = \"hunter2secret\";"
        },
        new()
        {
            Id = "DBY002",
            Description = "Слабая криптография (MD5/SHA-1/DES/RC4/ECB)",
            Pattern = @"\b(?:MD5|SHA-?1|DES|TripleDES|RC2)\.Create\s*\(|CipherMode\.ECB|MessageDigest\.getInstance\s*\(\s*""(?:MD5|SHA-?1)""|Cipher\.getInstance\s*\(\s*""[^""]*(?:DES|ECB|RC4)",
            Extensions = new[] { ".cs", ".java" },
            PositiveSample = "using var md5 = MD5.Create();"
        },
        new()
        {
            Id = "DBY003",
            Description = "Небезопасная десериализация",
            Pattern = @"BinaryFormatter|NetDataContractSerializer|LosFormatter|TypeNameHandling\s*\.\s*(?:All|Auto|Objects)|new\s+ObjectInputStream",
            Extensions = new[] { ".cs", ".java" },
            PositiveSample = "var formatter = new BinaryFormatter();"
        },
        new()
        {
            Id = "DBY004",
            Description = "XXE: разбор DTD или внешний XML-резолвер",
            Pattern = @"DtdProcessing\s*=\s*DtdProcessing\.Parse|XmlResolver\s*=\s*new\s+XmlUrlResolver",
            Extensions = new[] { ".cs" },
            PositiveSample = "settings.DtdProcessing = DtdProcessing.Parse;"
        },
        new()
        {
            Id = "DBY005",
            Description = "Запуск процесса через shell или shell-строку",
            Pattern = @"UseShellExecute\s*=\s*true|Runtime\.getRuntime\s*\(\s*\)\.exec|new\s+ProcessBuilder\s*\(\s*[^)]*\+",
            Extensions = new[] { ".cs", ".java" },
            PositiveSample = "psi.UseShellExecute = true;"
        },
        new()
        {
            Id = "DBY006",
            Description = "Аргументы процесса собираются строкой вместо ArgumentList",
            Pattern = @"\bArguments\s*=\s*(?:\$""|[^;\r\n]*\+)|Process\.Start\s*\(\s*""[^""]*""",
            Extensions = new[] { ".cs", ".java" },
            PositiveSample = "psi.Arguments = \"-jar \" + jarPath;"
        },
        new()
        {
            Id = "DBY007",
            Description = "Отключена проверка TLS-сертификата или имени хоста",
            Pattern = @"ServerCertificateCustomValidationCallback\s*=[^;\r\n]*=>\s*true|RemoteCertificateValidationCallback\s*=[^;\r\n]*=>\s*true|DangerousAcceptAnyServerCertificateValidator|setHostnameVerifier|setDefaultHostnameVerifier|HostnameVerifier[^;\r\n]*return\s+true",
            Extensions = new[] { ".cs", ".java" },
            PositiveSample = "handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;"
        },
        new()
        {
            Id = "DBY008",
            Description = "Криптографический nonce/ключ из Random",
            Pattern = @"new\s+(?:java\.util\.)?Random\s*\(",
            Extensions = new[] { ".cs", ".java" },
            PositiveSample = "var nonce = new Random().Next();"
        },
        new()
        {
            Id = "DBY009",
            Description = "SQL собирается конкатенацией/интерполяцией без привязки параметров",
            Pattern = @"\$""[^""\r\n]*\b(?:SELECT|INSERT\s+INTO|UPDATE|DELETE\s+FROM|FROM|WHERE|SET)\b[^""\r\n]*\{[^}\r\n]+\}|(?:execute|executeQuery|executeUpdate)\s*\([^;\r\n]*\+",
            Extensions = new[] { ".cs", ".java" },
            PositiveSample = "var sql = $\"SELECT * FROM {table}\";"
        },
        new()
        {
            Id = "DBY010",
            Description = "Секрет попадает в лог или консоль",
            Pattern = @"(?:Log\.(?:Info|Error)|Console\.(?:Write|WriteLine)|System\.(?:out|err)\.print(?:ln)?)\s*\([^)\r\n]*\b(?:password|passwd|secret|token)\b",
            Extensions = new[] { ".cs", ".java" },
            PositiveSample = "Log.Info(\"password=\" + value);"
        },
        new()
        {
            Id = "DBY011",
            Description = "DPAPI в контексте машины вместо пользователя",
            Pattern = @"DataProtectionScope\.LocalMachine",
            Extensions = new[] { ".cs" },
            PositiveSample = "ProtectedData.Protect(data, null, DataProtectionScope.LocalMachine);"
        },
        new()
        {
            Id = "DBY012",
            Description = "Отключена проверка host key в OpenSSH",
            Pattern = @"StrictHostKeyChecking[=\s]+no|UserKnownHostsFile[=\s]+/dev/null|HostKeyAlgorithms[=\s]+\+ssh-rsa",
            Extensions = new[] { ".cs", ".ps1", ".sh" },
            PositiveSample = "\"-o\", \"StrictHostKeyChecking=no\""
        },
        new()
        {
            Id = "DBY013",
            Description = "Java TrustManager/HostnameVerifier без проверки",
            Pattern = @"checkServerTrusted\s*\([^)]*\)\s*\{\s*\}|checkClientTrusted\s*\([^)]*\)\s*\{\s*\}|X509TrustManager\s*\(\s*\)\s*\{",
            Extensions = new[] { ".java" },
            PositiveSample = "public void checkServerTrusted(X509Certificate[] chain, String authType) { }"
        },
        new()
        {
            Id = "DBY014",
            Description = "В репозитории лежит приватный ключ",
            Pattern = @"BEGIN\s+(?:RSA\s+|EC\s+|OPENSSH\s+)?PRIVATE KEY",
            Extensions = new[] { ".cs", ".java", ".ps1", ".sh", ".iss", ".yml", ".yaml", ".json", ".config", ".props", ".csproj", ".md" },
            PositiveSample = "-----BEGIN PRIVATE KEY-----"
        },
        new()
        {
            Id = "DBY015",
            Description = "GitHub Actions подставляет secrets прямо в run-строку",
            Pattern = @"run:\s*[^\r\n]*\$\{\{\s*secrets\.",
            Extensions = new[] { ".yml", ".yaml" },
            PositiveSample = "run: echo ${{ secrets.TOKEN }}"
        },
        new()
        {
            Id = "DBY016",
            Description = "Опасный триггер pull_request_target",
            Pattern = @"pull_request_target",
            Extensions = new[] { ".yml", ".yaml" },
            PositiveSample = "on: pull_request_target"
        },
        new()
        {
            Id = "DBY017",
            Description = "Скачивание и немедленное исполнение скрипта",
            Pattern = @"(?:curl|wget)[^|\r\n]*\|\s*(?:ba)?sh",
            Extensions = new[] { ".sh", ".ps1", ".yml", ".yaml" },
            PositiveSample = "curl -fsSL https://example.com/x.sh | bash"
        }
    };

    public static SastRule ById(string id) =>
        Rules.Single(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

public static class SastScanner
{
    private static readonly Regex Suppression = new(
        @"sast:allow\s+(DBY\d{3})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<SastFinding> Scan(IEnumerable<(string Path, string Text)> files,
        IReadOnlyList<SastRule>? rules = null)
    {
        rules ??= SastCatalog.Rules;
        var findings = new List<SastFinding>();
        foreach (var (path, text) in files)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (var rule in rules.Where(r => r.AppliesTo(path)))
            {
                var regex = new Regex(rule.Pattern, rule.Options);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (!regex.IsMatch(line)) continue;
                    if (IsSuppressed(rule.Id, lines, i)) continue;
                    findings.Add(new SastFinding(rule.Id, rule.Description,
                        path, i + 1, line));
                }
            }
        }
        return findings;
    }

    public static IReadOnlyList<SastFinding> ScanText(string path, string text,
        IReadOnlyList<SastRule>? rules = null) =>
        Scan(new[] { (path, text) }, rules);

    private static bool IsSuppressed(string ruleId, string[] lines, int index)
    {
        if (HasSuppression(lines[index], ruleId)) return true;
        // An explanatory comment may sit between the marker and the code.
        for (var back = 1; back <= 2 && index - back >= 0; back++)
            if (HasSuppression(lines[index - back], ruleId)) return true;
        return false;
    }

    private static bool HasSuppression(string line, string ruleId)
    {
        foreach (Match match in Suppression.Matches(line))
        {
            if (match.Groups[1].Value.Equals(ruleId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

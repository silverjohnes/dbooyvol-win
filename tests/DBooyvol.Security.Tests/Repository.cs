namespace DBooyvol.Security.Tests;

/// <summary>
/// Locates the checkout and returns the files that make up the product:
/// WPF/.NET sources, the Java sidecar, packaging scripts and CI.
/// Build output, dependencies, locale packs and tests are intentionally excluded.
/// </summary>
public static class Repository
{
    public static string Root { get; } = FindRoot();

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".java", ".ps1", ".sh", ".iss", ".yml", ".yaml",
        ".json", ".config", ".props", ".csproj", ".md"
    };

    private static readonly string[] Roots =
    {
        "src", "sidecar", ".github", "installer", "runtime"
    };

    public static IReadOnlyList<(string Path, string Text)> SourceFiles()
    {
        var files = new List<(string, string)>();
        foreach (var relative in Roots)
        {
            var directory = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (!IsTextSource(path)) continue;
                files.Add((Relative(path), File.ReadAllText(path)));
            }
        }

        foreach (var name in new[] { "README.md", ".gitignore", "Directory.Build.props" })
        {
            var path = Path.Combine(Root, name);
            if (File.Exists(path)) files.Add((name, File.ReadAllText(path)));
        }

        return files.OrderBy(f => f.Item1, StringComparer.Ordinal).ToList();
    }

    public static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public static bool Exists(string relativePath) =>
        File.Exists(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static bool IsTextSource(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Extensions.Contains(Path.GetExtension(path))) return false;

        var normalized = path.Replace('\\', '/');
        return !normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
               && !normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
               && !normalized.Contains("/publish/", StringComparison.OrdinalIgnoreCase)
               && !normalized.Contains("/sidecar/build/", StringComparison.OrdinalIgnoreCase)
               && !normalized.Contains("/runtime/jre/", StringComparison.OrdinalIgnoreCase)
               && !normalized.Contains("/resources/locales/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(Root, path).Replace('\\', '/');

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DBooyvol.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Не найден корень репозитория (DBooyvol.sln)");
    }
}

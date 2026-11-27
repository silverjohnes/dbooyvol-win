using System.Text.RegularExpressions;

namespace DBooyvol.Security.Tests;

/// <summary>
/// A single line-oriented SAST rule. Rules are intentionally small and
/// technology-specific: WPF/.NET, the Java sidecar, JDBC, DPAPI/AES-GCM,
/// OpenSSH and the packaging scripts.
/// </summary>
public sealed class SastRule
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public required string Pattern { get; init; }
    public required string[] Extensions { get; init; }
    /// <summary>A line that must trigger this rule; proves the rule is not dead.</summary>
    public required string PositiveSample { get; init; }
    public RegexOptions Options { get; init; } =
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    public bool AppliesTo(string path) =>
        Extensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
}

public sealed record SastFinding(string RuleId, string Rule, string File, int Line, string Code)
{
    public override string ToString() => $"{RuleId} {File}:{Line}: {Rule} | {Code.Trim()}";
}

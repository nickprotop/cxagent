namespace CxAgent.UI;

/// <summary>
/// Orders two plugin versions as an author is likely to have meant them: <c>1.10.0</c> after <c>1.9.0</c>, a leading
/// <c>v</c> ignored, and a release after its own pre-release (<c>1.0.0</c> after <c>1.0.0-rc.1</c>).
///
/// <para>NULL WHEN IT CANNOT TELL. A sidecar version is free text; one that does not read as dotted numbers is not
/// guessed at, and the caller decides what an unordered difference means.</para>
/// </summary>
public static class PluginVersion
{
    /// <summary>Whether <paramref name="candidate"/> is newer than <paramref name="than"/>; null when either is not a
    /// version.</summary>
    public static bool? IsNewer(string candidate, string than)
    {
        if (Parse(candidate) is not { } a || Parse(than) is not { } b) return null;
        var core = a.Core.CompareTo(b.Core);
        if (core != 0) return core > 0;

        // THE SAME NUMBERS: a release is newer than a pre-release of it, and two pre-releases are told apart as
        // text — semver's own rule for identifiers is finer, and nothing here needs it.
        return (a.Pre, b.Pre) switch
        {
            (null, null) => false,
            (null, _) => true,
            (_, null) => false,
            _ => string.CompareOrdinal(a.Pre, b.Pre) > 0,
        };
    }

    private sealed record Parsed(Version Core, string? Pre);

    private static Parsed? Parse(string text)
    {
        var t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        var plus = t.IndexOf('+');
        if (plus >= 0) t = t[..plus];
        var dash = t.IndexOf('-');
        var core = dash >= 0 ? t[..dash] : t;
        var pre = dash >= 0 ? t[(dash + 1)..] : null;
        // ONE PART IS A NUMBER, NOT A VERSION — "2024" beside "2025-nightly" is a date, not a release line.
        if (!core.Contains('.') || !Version.TryParse(core, out var v)) return null;
        // PADDED TO THREE PARTS: .NET orders 1.0 before 1.0.0, and an author who wrote either meant the same release.
        var padded = new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
        return new Parsed(padded, string.IsNullOrEmpty(pre) ? null : pre);
    }
}

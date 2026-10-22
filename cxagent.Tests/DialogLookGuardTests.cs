using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// ONE LOOK, ENFORCED. A dialog that builds its own chrome drifts back to its own colours, padding and
/// border; this fails the build the moment one does.
/// </summary>
public class DialogLookGuardTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "plugins", "plugins.json"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void EveryModalWindow_IsStyledByDialogLook()
    {
        var ui = Path.Combine(RepoRoot(), "cxagent", "UI");
        var offenders = Directory.EnumerateFiles(ui, "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains(".AsModal()", StringComparison.Ordinal))
            .Where(f => !File.ReadAllText(f).Contains(".Styled(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(ui, f))
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void NoDialogTitle_IsPaddedWithSpaces()
    {
        var ui = Path.Combine(RepoRoot(), "cxagent", "UI");
        var padded = Directory.EnumerateFiles(ui, "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadLines(f).Select(l => (f, l)))
            .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.l, "WithTitle\\(\\$?\" "))
            .Select(x => $"{Path.GetRelativePath(ui, x.f)}: {x.l.Trim()}")
            .ToList();
        Assert.Empty(padded);
    }
}

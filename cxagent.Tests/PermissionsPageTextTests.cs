using System.Linq;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Parsing;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Task 5: the Permissions page is READ-ONLY (spec Decision 7) — it renders trust state, this
/// scope's rules, an honest count of other scopes, the file path, a revoke note, and the
/// LoadError warning. It never calls Add/SetTrust. <see cref="PermissionsPageText.Build"/> is the
/// pure half, split out so these assertions don't need a live window.
/// </summary>
public class PermissionsPageTextTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-permpage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void PermissionsPage_SurfacesALoadFailure_TheOnePersistentPlaceAUserCanLearnOfIt()
    {
        // The I3 chat echo fires once at first wire and scrolls away (AppBootstrap.cs:128-136).
        // This page is where a user can DISCOVER that every grant silently vanished.
        var paths = new AppPaths(MakeTempDir());
        Directory.CreateDirectory(paths.ConfigDir);
        File.WriteAllText(Path.Combine(paths.ConfigDir, "permissions.json"), "{ not json");
        var store = new PermissionRulesStore(paths);

        var lines = PermissionsPageText.Build(store, "/proj/a");
        Assert.Contains(lines, l => l.Contains("could not be read"));
    }

    [Fact]
    public void PermissionsPage_ShowsTrustState_AndEscapesRulePatterns()
    {
        var store = new PermissionRulesStore(new AppPaths(MakeTempDir()));
        store.SetTrust("/proj/a", TrustState.Trusted);
        // "[red]" is a REAL recognised tag name — an unescaped "we[red]ird" renders as "weird"
        // (verified against the live parser; SettingsDialogTests.cs:149-151 pins the same fact).
        // A fixture like "ls [a]*" would pass this assertion even with escaping deleted, since
        // "[a]" is not a real tag and the parser leaves it untouched either way — the exact trap
        // Task 5's brief calls out. Using a real tag name is what makes this test load-bearing.
        store.Add("/proj/a", PermissionKind.Shell, "ls [red]*");

        var lines = PermissionsPageText.Build(store, "/proj/a");
        Assert.Contains(lines, l => l.Contains("Trusted"));
        var ruleLine = lines.First(l => l.Contains("ls "));
        var visible = string.Concat(MarkupParser.Parse(ruleLine, Color.White, Color.Black)
            .Select(c => c.Character));
        Assert.Contains("ls [red]*", visible);                   // P7: bracket survives rendering
    }

    [Fact]
    public void PermissionsPage_NoLoadError_DoesNotShowTheWarning()
    {
        var store = new PermissionRulesStore(new AppPaths(MakeTempDir()));
        var lines = PermissionsPageText.Build(store, "/proj/a");
        Assert.DoesNotContain(lines, l => l.Contains("could not be read"));
    }

    [Fact]
    public void PermissionsPage_ShowsOtherScopeCount_WithoutListingThem()
    {
        var store = new PermissionRulesStore(new AppPaths(MakeTempDir()));
        store.Add("/proj/a", PermissionKind.Shell, "git status");
        store.Add("/proj/b", PermissionKind.Shell, "ls");
        store.Add("/proj/c", PermissionKind.Shell, "pwd");

        var lines = PermissionsPageText.Build(store, "/proj/a");
        Assert.Contains(lines, l => l.Contains("2") && (l.Contains("other") || l.Contains("scope")));
        Assert.DoesNotContain(lines, l => l.Contains("/proj/b"));
        Assert.DoesNotContain(lines, l => l.Contains("/proj/c"));
    }
}

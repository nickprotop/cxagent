using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What <c>@</c> offers. Nothing is filtered out: the file tree hides generated output because
/// nobody browses it, and this is not browsing.
/// </summary>
public class PathCompletionsTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "atpaths-" + Guid.NewGuid().ToString("N"));

    public PathCompletionsTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src", "UI"));
        Directory.CreateDirectory(Path.Combine(_dir, "obj", "Debug"));
        File.WriteAllText(Path.Combine(_dir, "src", "UI", "ShellWindow.cs"), "");
        File.WriteAllText(Path.Combine(_dir, "src", "Program.cs"), "");
        File.WriteAllText(Path.Combine(_dir, "obj", "Debug", "generated.cs"), "");
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "obj/\n");
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void ItMatchesAFilenameAnywhereUnderTheRoot()
    {
        var hits = PathCompletions.Find(_dir, "Shell");

        Assert.Contains(hits, h => h.Path.EndsWith("ShellWindow.cs", StringComparison.Ordinal));
    }

    /// <summary>
    /// GENERATED OUTPUT IS OFFERED. `obj/` is gitignored and excluded from the file tree; a user who
    /// types its name has said something specific, and a completion that hid it would be one they
    /// stop trusting.
    /// </summary>
    [Fact]
    public void NothingIsExcluded()
    {
        Assert.NotEmpty(PathCompletions.Find(_dir, "generated"));
        Assert.NotEmpty(PathCompletions.Find(_dir, "obj"));
    }

    /// <summary>A separator in the prefix anchors the search rather than matching a bare name.</summary>
    [Fact]
    public void APrefixWithASeparator_SearchesInsideThatDirectory()
    {
        var hits = PathCompletions.Find(_dir, "src/UI/");

        Assert.Contains(hits, h => h.Path.EndsWith("ShellWindow.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(hits, h => h.Path.EndsWith("Program.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void ADirectoryIsAMatch_AndIsMarkedAsOne()
    {
        var hit = Assert.Single(PathCompletions.Find(_dir, "src"), h => h.IsDirectory);

        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), hit.Display);
    }

    [Fact]
    public void ItStopsAtTheLimit()
    {
        Assert.True(PathCompletions.Find(_dir, "", limit: 2).Count <= 2);
    }

    [Fact]
    public void AnUnreadableRoot_IsEmptyRatherThanThrowing()
    {
        Assert.Empty(PathCompletions.Find(Path.Combine(_dir, "nope"), "x"));
    }

    /// <summary>
    /// AN ABSOLUTE PATH LEAVES THE ROOT, and that costs nothing: completion is not the permission
    /// boundary. A read outside the session's folder still prompts (PermissionPolicy:368), so
    /// refusing to COMPLETE one would restrict the composer where the permission model does not.
    /// </summary>
    [Fact]
    public void AnAbsolutePrefix_CompletesOutsideTheRoot()
    {
        var elsewhere = Path.Combine(_dir, "src", "UI");
        var hits = PathCompletions.Find(Path.Combine(_dir, "obj"), elsewhere + Path.DirectorySeparatorChar);

        Assert.Contains(hits, h => h.Path.EndsWith("ShellWindow.cs", StringComparison.Ordinal));
    }
}

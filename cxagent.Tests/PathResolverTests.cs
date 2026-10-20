using CxAgent.Core.Permissions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The three kinds of path resolution, named in one type: the file tool's (collapse <c>..</c> as text, then
/// follow links), a shell argument's (the kernel's walk), and a shell cd's (logical where that folder exists).
/// Each test pins a place two of them disagree, because a call site that picked the wrong one is a hole.
/// </summary>
public sealed class PathResolverTests : IDisposable
{
    private readonly string _top = Path.Combine(Path.GetTempPath(), "cx-resolver-" + Guid.NewGuid().ToString("N"));
    private readonly string _proj;
    private readonly string _away;

    public PathResolverTests()
    {
        _proj = Path.Combine(_top, "proj");
        _away = Path.Combine(_top, "away");
        Directory.CreateDirectory(Path.Combine(_proj, "sub"));
        Directory.CreateDirectory(_away);
        File.WriteAllText(Path.Combine(_top, "beside.txt"), "beside");
        File.WriteAllText(Path.Combine(_proj, "in.txt"), "in");
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateSymbolicLink(Path.Combine(_proj, "out"), _away);
        File.CreateSymbolicLink(Path.Combine(_proj, "dangling"), Path.Combine(_away, "missing.txt"));
    }

    public void Dispose() { try { Directory.Delete(_top, recursive: true); } catch (IOException) { } }

    private PathResolver At(string folder) => new(folder);

    [Fact]
    public void ALinkComponentIsFollowed_ByEveryKind()
    {
        if (OperatingSystem.IsWindows()) return;
        var real = Path.Combine(PathResolver.RealFolder(_away), "x.txt");
        Assert.Equal(real, At(_proj).AsFileTool("out/x.txt"));
        Assert.Equal(real, At(_proj).AsShellArgument("out/x.txt"));
    }

    [Fact]
    public void DotDotAfterALink_IsBesideTheLinkForTheFileTool_AndBesideItsTargetForTheShell()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(Path.Combine(PathResolver.RealFolder(_proj), "beside.txt"), At(_proj).AsFileTool("out/../beside.txt"));
        Assert.Equal(Path.Combine(PathResolver.RealFolder(_top), "beside.txt"), At(_proj).AsShellArgument("out/../beside.txt"));
    }

    [Fact]
    public void ADanglingLinkNamesSomething_AndResolvesWhereItPoints()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.True(At(_proj).Names("dangling"));
        Assert.False(At(_proj).Names("nothing-here"));
        Assert.Equal(Path.Combine(PathResolver.RealFolder(_away), "missing.txt"), At(_proj).AsShellArgument("dangling"));
    }

    [Fact]
    public void ALoopOfLinks_ResolvesToNothing()
    {
        if (OperatingSystem.IsWindows()) return;
        File.CreateSymbolicLink(Path.Combine(_proj, "a"), Path.Combine(_proj, "b"));
        File.CreateSymbolicLink(Path.Combine(_proj, "b"), Path.Combine(_proj, "a"));

        Assert.Null(At(_proj).AsShellArgument("a"));
    }

    [Theory]
    [InlineData("-")]
    [InlineData("~")]
    [InlineData("~/x")]
    [InlineData("$HOME")]
    [InlineData("${OLDPWD}/x")]
    public void ACdTheShellDecides_IsSaidSo(string cd)
    {
        var after = At(_proj).AsShellCd(cd);
        Assert.True(after.ShellDecides);
        Assert.Null(after.Folder);
    }

    [Fact]
    public void ACd_IsLogicalWhereThatFolderExists_AndPhysicalWhereItDoesNot()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Path.Combine(_top, "onlyphys"));

        // `out/..` collapses to the project itself, which exists: dash and bash both enter it.
        Assert.Equal(Path.GetFullPath(_proj), At(_proj).AsShellCd("out/..").Folder);
        // `out/../onlyphys` collapses to proj/onlyphys, which does not exist: bash enters the physical one.
        Assert.Equal(Path.Combine(PathResolver.RealFolder(_top), "onlyphys"), At(_proj).AsShellCd("out/../onlyphys").Folder);
        Assert.Equal(_proj, At(_proj).AsShellCd(null).Folder);
    }

    [Fact]
    public void AFolderContainsItselfAndWhatIsUnderIt_NotASiblingSharingItsPrefix()
    {
        Assert.True(PathResolver.Contains("/a", "/a"));
        Assert.True(PathResolver.Contains("/a", "/a/b"));
        Assert.False(PathResolver.Contains("/a", "/ab"));
    }
}

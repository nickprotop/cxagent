using CxAgent.Core.Permissions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A COMMAND'S TOKENS ARE JUDGED WHERE THE COMMAND READS THEM — the folder it runs in, past a leading cd — and
/// come back absolute, so no caller joins them onto a folder of its own choosing. The process directory has no
/// say: a file that exists only there names nothing the command touches.
///
/// <para>THE MERGE GUARDS below pin the population two collectors gathered before they were one — a verb after
/// a pipe, a quoted path, a relative path that does not exist, an absolute path through a link and back. A
/// narrower merge drops one of them, and a dropped token is a path nobody judges.</para>
/// </summary>
[Collection("working-directory")]
public sealed class CommandSubjectsFolderTests : IDisposable
{
    private readonly string _top = Path.Combine(Path.GetTempPath(), "cx-subjects-" + Guid.NewGuid().ToString("N"));
    private readonly string _run;
    private readonly string _elsewhere;
    private readonly string _previous = Directory.GetCurrentDirectory();

    public CommandSubjectsFolderTests()
    {
        _run = Path.Combine(_top, "run");
        _elsewhere = Path.Combine(_top, "elsewhere");
        Directory.CreateDirectory(Path.Combine(_run, "sub"));
        Directory.CreateDirectory(_elsewhere);
        File.WriteAllText(Path.Combine(_elsewhere, "only-here.txt"), "x");
        File.WriteAllText(Path.Combine(_run, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(_run, "sub", "notes.txt"), "x");
        File.WriteAllText(Path.Combine(_run, "my notes.txt"), "x");
        Directory.SetCurrentDirectory(_elsewhere);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_previous);
        Directory.Delete(_top, recursive: true);
    }

    private CommandSubjects Of(string command) => CommandSubjects.Of(command, new PathResolver(_run));

    [Fact]
    public void ATokenThatExistsOnlyInTheProcessDirectory_NamesNothing()
    {
        Assert.DoesNotContain(Of("cat only-here.txt").Paths, p => p.EndsWith("only-here.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void PathsAreAbsolute_UnderTheFolderTheyAreReadIn()
    {
        Assert.Contains(Path.Combine(_run, "notes.txt"), Of("cat notes.txt").Paths);
        Assert.Contains(Path.Combine(Path.GetFullPath(Path.Combine(_run, "sub")), "notes.txt"),
            Of("cd sub && cat notes.txt").Paths.Select(Path.GetFullPath));
        Assert.Equal(["notes.txt"], Of("cat notes.txt").Tokens);
    }

    [Fact]
    public void ACdTheShellDecides_IsSaidSo()
    {
        Assert.True(Of("cd - && ls").CdShellDecides);
        Assert.False(Of("cd sub && ls").CdShellDecides);
    }

    [Theory]
    [InlineData("cat notes.txt | grep x ../missing", "../missing")]
    [InlineData("cat \"my notes.txt\"", "my notes.txt")]
    [InlineData("cat ../missing", "../missing")]
    [InlineData("rm -rf ../outside", "../outside")]
    public void TheMergedPopulation_KeepsEveryToken(string command, string expected)
    {
        Assert.Contains(Path.GetFullPath(Path.Combine(_run, expected)), Of(command).Paths.Select(Path.GetFullPath));
    }

    [Fact]
    public void AnAbsolutePathThroughALinkAndBack_IsKept()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateSymbolicLink(Path.Combine(_run, "link"), _elsewhere);
        var spelled = $"{_run}/link/../y";

        Assert.Contains(spelled, Of($"cat {spelled}").Paths);
    }
}

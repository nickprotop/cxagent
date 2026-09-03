using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What a typed `/open` means, before any window exists. The handler needs a window system and so is
/// driven by hand; this is the decision it makes first.
/// </summary>
public class OpenTargetTests
{
    // BARE MEANS THE PICKER, not an error and not a usage line — the spec's first rule.
    [Fact]
    public void NoArgument_AsksForThePicker()
    {
        Assert.True(OpenTarget.For("", "/work").ShowPicker);
        Assert.True(OpenTarget.For("   ", "/work").ShowPicker);
    }

    [Fact]
    public void ARelativePath_ResolvesAgainstTheWorkingDirectory()
    {
        var target = OpenTarget.For(Path.Combine("UI", "ShellTab.cs"), "/work");

        Assert.False(target.ShowPicker);
        Assert.Equal(Path.GetFullPath(Path.Combine("/work", "UI", "ShellTab.cs")), target.Path);
    }

    // An absolute path is taken as given — the same latitude `@` has, for the same reason.
    [Fact]
    public void AnAbsolutePath_IsUsedAsGiven()
    {
        var absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hosts"));

        Assert.Equal(absolute, OpenTarget.For(absolute, "/work").Path);
    }

    // The composer hands the argument through verbatim, so a quoted path arrives with its quotes.
    [Fact]
    public void AQuotedPath_LosesItsQuotes()
    {
        var target = OpenTarget.For("\"my file.cs\"", "/work");

        Assert.Equal(Path.GetFullPath(Path.Combine("/work", "my file.cs")), target.Path);
    }
}

/// <summary>The registration itself: present, and deliberately not advertised to the model.</summary>
public class OpenCommandRegistrationTests
{
    // TellTheModel defaults to false; /open relies on that default rather than restating it. This
    // pins the default, because a later edit that "tidied" it to true would cost tokens in every
    // request with nothing on screen to show for it.
    [Fact]
    public void OpenIsNotToldToTheModel()
    {
        var command = new CxAgent.Core.Commands.SessionCommand("/open", "open a file in a tab",
            [new CxAgent.Core.Commands.CommandArgument("<path>", "the file to open")]);

        Assert.False(command.TellTheModel);
    }

    // A path is exactly what the @ machinery completes, unlike /shell's <command>.
    [Fact]
    public void ThePathArgumentCompletes()
    {
        var arg = new CxAgent.Core.Commands.CommandArgument("<path>", "the file to open");

        Assert.True(arg.Completes);
    }
}

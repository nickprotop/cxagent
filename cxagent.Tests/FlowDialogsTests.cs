using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A Flow question's text, split between its title and its body: the library shows the title in the
/// window border and again as the step's heading, so the body carries only what the title does not.
/// </summary>
public class FlowDialogsTests
{
    [Fact]
    public void AOneLineQuestion_HasNoBody() =>
        Assert.Equal("", FlowDialogs.DetailOf("Install 'repo-map' 0.9.46 where?"));

    [Fact]
    public void TheBody_IsTheLinesAfterTheQuestion()
    {
        var prompt = "Uninstall 'calc'?\n\nThese files go:\n  calc.dll";
        Assert.Equal("Uninstall 'calc'?", FlowDialogs.TitleOf(prompt));
        Assert.Equal("These files go:\n  calc.dll", FlowDialogs.DetailOf(prompt));
    }
}

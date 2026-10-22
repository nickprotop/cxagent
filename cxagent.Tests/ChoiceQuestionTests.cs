using CxAgent.UI;
using CxAgent.UI.Config;
using SharpConsoleUI;
using SharpConsoleUI.Drivers;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The plugin manager's questions — install where, uninstall what — asked in the app's own dialog: the
/// question once, as the title; the detail under it; the answers as a list with Cancel last.
/// </summary>
public class ChoiceQuestionTests : IDisposable
{
    // THE DIALOG IS STATIC, so one left open would answer the next test's question at once.
    public void Dispose() => ConfirmDialog.CloseIfOpen();

    private static ConsoleWindowSystem System()
    {
        var system = new ConsoleWindowSystem(new HeadlessConsoleDriver(100, 30));
        CxAgentTheme.Install(system, null);
        return system;
    }

    [Fact]
    public void AOneLineQuestion_HasNoDetail() =>
        Assert.Empty(ConfirmDialog.DetailOf("Install 'repo-map' 0.9.46 where?"));

    [Fact]
    public void TheQuestionIsTheTitle_AndTheRestIsTheDetail()
    {
        var prompt = "Uninstall 'calc'?\n\nThese files go:\n  calc.dll";
        Assert.Equal("Uninstall 'calc'?", ConfirmDialog.TitleOf(prompt));
        Assert.Equal(["These files go:", "  calc.dll"], ConfirmDialog.DetailOf(prompt));
    }

    [Fact]
    public void TheChoicesAreListed_WithCancelLast_AndTheFirstReadyForEnter()
    {
        var asked = ConfirmDialog.Choose(System(), "Install 'x' where?", ["global — ~/.config", "project — ./.cxagent"], appendCancel: true);

        Assert.True(ConfirmDialog.IsOpen);
        Assert.Equal(["global — ~/.config", "project — ./.cxagent", "Cancel"], ConfirmDialog.AnswersForTest);
        Assert.Equal("global — ~/.config", ConfirmDialog.Highlighted);
        ConfirmDialog.CloseIfOpen();
        Assert.Null(asked.Result);
    }

    [Fact]
    public void ACallerWithItsOwnLastChoice_GetsNoCancel()
    {
        _ = ConfirmDialog.Choose(System(), "'x' installed", ["load now", "add to config", "done"], appendCancel: false);
        Assert.Equal(["load now", "add to config", "done"], ConfirmDialog.AnswersForTest);
    }
}

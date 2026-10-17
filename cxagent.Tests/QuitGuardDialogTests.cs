using CxAgent.UI;
using CxAgent.UI.Config;
using SharpConsoleUI;
using SharpConsoleUI.Drivers;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The quit/close question as it opens: Enter, the reflex, must keep the agents running.
///
/// <para>ON THE HEADLESS DRIVER, THROUGH THE REAL DIALOG: the answer a reflex reaches is decided by which
/// item the dialog selects first, and only the dialog itself can say which that is.</para>
/// </summary>
[Collection(ConfirmDialogCollection.Name)]
public class QuitGuardDialogTests
{
    [Fact]
    public void TheQuestion_OpensOnCancel_WithTheAnswersFocused()
    {
        var system = new ConsoleWindowSystem(new HeadlessConsoleDriver(100, 30));
        CxAgentTheme.Install(system, null);
        var asked = QuitGuard.ConfirmAsync(system, running: 2);
        try
        {
            Assert.True(ConfirmDialog.IsOpen);
            Assert.Equal("Cancel", ConfirmDialog.Highlighted);
            Assert.True(ConfirmDialog.AnswersFocused);
        }
        finally
        {
            ConfirmDialog.CloseIfOpen();
        }
        Assert.False(asked.GetAwaiter().GetResult());   // Esc, like closing, keeps them running
    }

    /// <summary>CLOSING A TAB OPENS ON CANCEL TOO, and Esc keeps the tab and everything it is running.</summary>
    [Fact]
    public void TheTabQuestion_OpensOnCancel()
    {
        var system = new ConsoleWindowSystem(new HeadlessConsoleDriver(100, 30));
        CxAgentTheme.Install(system, null);
        var asked = QuitGuard.ConfirmCloseTabAsync(system, new TabWork(Agents: 1, Commands: 2));
        try
        {
            Assert.True(ConfirmDialog.IsOpen);
            Assert.Equal("Cancel", ConfirmDialog.Highlighted);
        }
        finally
        {
            ConfirmDialog.CloseIfOpen();
        }
        Assert.Equal(TabClose.Cancel, asked.GetAwaiter().GetResult());
    }

    /// <summary>
    /// THE CHOSEN ANSWER IS THE MOST VIVID THING IN THE LIST, in the theme the user runs as well as in
    /// cxagent's own. A theme's selection bar is often a dark shade under rows drawn in its bright accent —
    /// Crimson's is — so the answers NOT chosen outshone the chosen one, and "which one will Enter pick"
    /// read the wrong way.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Crimson")]
    [InlineData("ModernGray")]
    public void TheHighlightedAnswer_IsAFullStrengthBar(string? themeName)
    {
        var system = new ConsoleWindowSystem(new HeadlessConsoleDriver(100, 30));
        CxAgentTheme.Install(system, themeName);
        var theme = system.ThemeStateService.CurrentTheme;
        static int Brightness(SharpConsoleUI.Color c) => c.R + c.G + c.B;

        _ = QuitGuard.ConfirmAsync(system, running: 1);
        try
        {
            var fill = ConfirmDialog.HighlightFill!.Value;
            Assert.True(Math.Abs(Brightness(fill) - Brightness(theme.WindowBackgroundColor)) >= 250,
                $"{theme.Name}: bar {fill} against background {theme.WindowBackgroundColor}");
        }
        finally
        {
            ConfirmDialog.CloseIfOpen();
        }
    }
}

/// <summary>ConfirmDialog holds one question at a time in static state, so its tests do not run in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConfirmDialogCollection
{
    public const string Name = "ConfirmDialog";
}

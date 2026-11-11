using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Drivers;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The right-hand panel's keys (user, 2026-11-10): F3 and F7 only choose its view — Session or Tasks — and do nothing
/// while it is hidden; F5 shows and hides it, on the main status bar. F3 and F7 sit on the panel's own bar, and the main
/// bar names the running tasks only while the panel that shows them is hidden.
/// </summary>
public sealed class SidePanelKeysTests
{
    private static MainWindow Window(int width)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-panel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var paths = new AppPaths(dir);
        paths.EnsureCreated();
        var mw = new MainWindow(new ConsoleWindowSystem(new HeadlessConsoleDriver(width, 30),
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: true)),
            ResolvedConfig.ForTesting(new MockLlmProvider(), "Mock"), new LogFileManager(paths));
        mw.Build();
        mw.RefreshSessionPanel();
        return mw;
    }

    private static bool Hidden(MainWindow mw) => !mw.SessionPanel.Control.Visible && !mw.JobsPanel.Control.Visible;

    [Fact]
    public void F3_and_F7_do_nothing_while_the_panel_is_hidden()
    {
        var mw = Window(80);   // narrow: hidden by default

        mw.ShowTasksView();
        Assert.True(Hidden(mw));
        mw.ShowSessionView();
        Assert.True(Hidden(mw));
    }

    [Fact]
    public void F3_and_F7_choose_the_view_and_never_hide_it()
    {
        var mw = Window(160);   // wide: shown by default

        mw.ShowTasksView();
        Assert.True(mw.JobsPanel.Control.Visible);
        mw.ShowTasksView();
        Assert.True(mw.JobsPanel.Control.Visible);
        mw.ShowSessionView();
        Assert.True(mw.SessionPanel.Control.Visible);
        mw.ShowSessionView();
        Assert.True(mw.SessionPanel.Control.Visible);
    }

    [Fact]
    public void F5_shows_and_hides_it_on_the_view_it_last_showed()
    {
        var mw = Window(160);
        mw.ShowTasksView();

        mw.TogglePanel();
        Assert.True(Hidden(mw));
        mw.TogglePanel();
        Assert.True(mw.JobsPanel.Control.Visible);
    }

    [Fact]
    public void Before_any_F5_it_follows_the_width_and_after_one_the_choice_holds()
    {
        Assert.True(Hidden(Window(80)));
        Assert.True(Window(160).SessionPanel.Control.Visible);

        var mw = Window(80);
        mw.TogglePanel();                 // shown on a narrow terminal
        mw.RefreshSessionPanel();         // a resize's refresh
        Assert.True(mw.SessionPanel.Control.Visible);
        mw.TogglePanel();
        mw.RefreshSessionPanel();
        Assert.True(Hidden(mw));
    }

    [Fact]
    public void F3_and_F7_are_on_the_panels_bar_and_F5_on_the_main_bar()
    {
        var mw = Window(160);
        mw.ShowPanelItem();

        Assert.Equal(["F3", "F7"], mw.PanelBar.LeftItems.Select(i => i.Shortcut));
        var main = mw.StatusBar.LeftItems.Select(i => i.Shortcut).ToList();
        Assert.Contains("F5", main);
        Assert.DoesNotContain("F3", main);
        Assert.DoesNotContain("F7", main);
    }

    [Theory]
    [InlineData(true, 2, 0, "Panel")]
    [InlineData(false, 0, 0, "Panel")]
    [InlineData(false, 2, 0, "Panel · Tasks 2")]
    [InlineData(false, 0, 1, "Panel · Tasks 0+1")]
    public void The_main_bar_names_running_tasks_only_while_the_panel_is_hidden(bool shown, int mine, int elsewhere, string label) =>
        Assert.Equal(label, MainWindow.PanelItemLabel(shown, mine, elsewhere));

    /// <summary>
    /// THE STATUS BAR'S DISTILLING WORD FOLLOWS THE DISTILLING, panel or no panel (user, 2026-11-11: with the side panel
    /// hidden or on Tasks, "distilling Charting…" stayed after the colleague had finished learning). The bar is always
    /// on screen; the panel's refresh is not.
    /// </summary>
    [Fact]
    public void The_distilling_word_comes_and_goes_with_the_panel_hidden()
    {
        var mw = Window(80);   // narrow: the panel is hidden
        Assert.True(Hidden(mw));
        string Shown() => string.Join(" ", mw.StatusBar.RightItems.Where(i => i.IsVisible).Select(i => i.Label));

        mw.DistillingForTest = () => ["Charting"];
        mw.RefreshSessionPanel();
        Assert.Contains("distilling Charting…", Shown());

        mw.DistillingForTest = () => [];
        mw.RefreshSessionPanel();
        Assert.DoesNotContain("distilling", Shown());
    }
}

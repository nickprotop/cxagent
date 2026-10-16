using System.Drawing;
using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Events;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A click on a tab puts the keyboard in that tab's main control; arrowing to a tab does not.
/// Before this, clicking back to Chat from another tab left focus on the strip, and the next
/// thing typed went nowhere with no sign it was lost.
/// </summary>
public class TabFocusTests
{
    private static MainWindow Built()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-tabfocus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var paths = new AppPaths(dir);
        paths.EnsureCreated();

        var system = new ConsoleWindowSystem(new HeadlessConsoleDriver(120, 30),
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: true));
        var mw = new MainWindow(system, ResolvedConfig.ForTesting(new MockLlmProvider(), "Mock"),
            new LogFileManager(paths));
        mw.Build();
        return mw;
    }

    /// <summary>A tab whose main control is a button, standing in for a terminal or an editor: all
    /// that matters here is which control the keyboard lands on.</summary>
    private static ButtonControl AddTabWithMain(MainWindow mw, string title)
    {
        var main = new ButtonControl { Text = title + " main" };
        var panel = new ScrollablePanelControl();
        panel.AddControl(main);
        mw.AddTab(title, panel);
        mw.SetTabMainControl(panel, main);
        return main;
    }

    /// <summary>
    /// Clicks along the header row until the strip reports choosing <paramref name="index"/>. The
    /// header's cell layout is the library's business, so this finds the tab by clicking rather than
    /// restating how wide a title is drawn.
    /// </summary>
    private static void ClickTab(MainWindow mw, int index)
    {
        var chosen = -1;
        void Note(int i) => chosen = i;
        mw.Tabs.SelectedByMouse += Note;
        try
        {
            for (var x = 0; x < 60 && chosen != index; x++)
            {
                var pos = new Point(x, mw.Tabs.Margin.Top);
                mw.Tabs.ProcessMouseEvent(new MouseEventArgs(
                    new List<MouseFlags> { MouseFlags.Button1Clicked }, pos, pos, pos));
            }
        }
        finally { mw.Tabs.SelectedByMouse -= Note; }

        Assert.True(chosen == index, $"no click on the header row chose tab {index}");
    }

    [Fact]
    public void ClickingChatAfterAnotherTab_FocusesTheComposer()
    {
        var mw = Built();
        AddTabWithMain(mw, "Notes");
        Assert.Equal(1, mw.Tabs.ActiveTabIndex);

        ClickTab(mw, 0);

        Assert.Equal(0, mw.Tabs.ActiveTabIndex);
        Assert.Same(mw.Input, mw.Window!.FocusManager.FocusedControl);
    }

    [Fact]
    public void ClickingATab_FocusesItsMainControl()
    {
        var mw = Built();
        var main = AddTabWithMain(mw, "Notes");
        ClickTab(mw, 0);
        Assert.Same(mw.Input, mw.Window!.FocusManager.FocusedControl);

        ClickTab(mw, 1);

        Assert.Same(main, mw.Window!.FocusManager.FocusedControl);
    }

    [Fact]
    public void ClickingATabWithNoRegisteredControl_FocusesItsFirstFocusableOne()
    {
        var mw = Built();
        var button = new ButtonControl { Text = "only" };
        var panel = new ScrollablePanelControl();
        panel.AddControl(button);
        mw.AddTab("Plain", panel);
        ClickTab(mw, 0);

        ClickTab(mw, 1);

        Assert.Same(button, mw.Window!.FocusManager.FocusedControl);
    }

    /// <summary>Arrowing through the strip is browsing: focus must stay where the strip left it, so
    /// the next arrow still reaches the strip.</summary>
    [Fact]
    public void SwitchingByKeyboard_DoesNotMoveFocusIntoTheTab()
    {
        var mw = Built();
        var main = AddTabWithMain(mw, "Notes");
        var raised = false;
        mw.Tabs.SelectedByMouse += _ => raised = true;
        ClickTab(mw, 0);
        raised = false;

        mw.Window!.FocusManager.SetFocus(mw.Tabs, FocusReason.Keyboard);
        mw.Tabs.ProcessKey(new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false));

        Assert.Equal(1, mw.Tabs.ActiveTabIndex);
        Assert.False(raised, "a key press must not be reported as a mouse selection");
        Assert.NotSame(main, mw.Window!.FocusManager.FocusedControl);
    }

    [Fact]
    public void SelectingProgrammatically_IsNotAMouseSelection()
    {
        var mw = Built();
        AddTabWithMain(mw, "Notes");
        var raised = false;
        mw.Tabs.SelectedByMouse += _ => raised = true;

        mw.Tabs.ActiveTabIndex = 0;

        Assert.False(raised);
    }
}

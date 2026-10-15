using SharpConsoleUI.Controls;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// That a confirmation's buttons can be reached and pressed from the keyboard.
///
/// <para>NOTHING IS MOUSE-ONLY. A block asking "delete this history" or "replace this conversation"
/// renders real focusable buttons, and nothing put the keyboard on them — the transcript is a long
/// scrolling surface that focus traversal does not stop at, so a keyboard-driven user could not
/// answer at all and the thing they asked for silently did not happen.</para>
///
/// <para><b>THE ROW IS ENTERED WITH TAB, WALKED WITH ARROWS, PRESSED WITH ENTER.</b> That split is
/// the framework's, not a choice made here: `ToolbarControl` is a focus SCOPE, and SharpConsoleUI
/// has tests named `Tab_ExitsToolbar_Forward` asserting Tab leaves it. Making Tab cycle inside the
/// row broke exactly those. What was missing was only the way IN, which is this app's to provide
/// because only it knows which row is the one being asked about.</para>
/// </summary>
public class ActionRowKeyboardTests
{
    private static ConsoleKeyInfo Tab(bool shift = false) =>
        new('\t', ConsoleKey.Tab, shift, alt: false, control: false);

    private static (ToolbarControl Bar, ButtonControl First, ButtonControl Second) Row()
    {
        var bar = new ToolbarControl { Wrap = true };
        var first = new ButtonControl { Text = "Delete history" };
        var second = new ButtonControl { Text = "Keep" };

        bar.AddItem(first);
        bar.AddItem(second);
        return (bar, first, second);
    }

    /// <summary>THE BUTTONS ARE REAL FOCUSABLE CONTROLS — the row can take the keyboard at all.</summary>
    [Fact]
    public void AnActionRowCanReceiveFocus()
    {
        var (bar, _, _) = Row();

        Assert.True(bar.CanReceiveFocus);
    }

    /// <summary>
    /// AND DECLINES KEYS WHEN IT IS NOT FOCUSED: `ProcessKey` returns false unless the control is in
    /// its window's focus path, and focus is the window's focus manager's to give. The walking itself
    /// is tested below, with the row in a headless window — see <see cref="HeadlessKeys"/>.
    /// </summary>
    [Fact]
    public void TheRowDeclinesKeysWhenItIsNotFocused()
    {
        var (bar, _, _) = Row();

        Assert.False(bar.ProcessKey(Tab()));
    }

    /// <summary>
    /// ARROWS WALK, ENTER PRESSES, TAB LEAVES — the row as a keyboard user meets it, with real keys.
    /// </summary>
    [Fact]
    public void ArrowsWalkTheRow_EnterPresses_TabLeaves()
    {
        var (bar, first, second) = Row();
        var pressed = "";
        first.Click += (_, _) => pressed = "first";
        second.Click += (_, _) => pressed = "second";
        var keys = HeadlessKeys.Focus(bar, bar);

        Assert.True(keys.Right());
        Assert.True(second.HasFocus);

        Assert.True(keys.Enter());
        Assert.Equal("second", pressed);

        Assert.False(keys.Tab());
    }
}

using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Events;
using SharpConsoleUI.Layout;

namespace CxAgent.UI;

/// <summary>
/// The tab strip, told apart by HOW a tab was chosen.
///
/// <para>WHY A SUBCLASS. <see cref="TabControl.TabChanged"/> fires for every cause — arrow keys, F6
/// browsing, the app moving to a new tab — and carries no hint which. A mouse click on a tab is a
/// different statement from the rest: it is a choice of that tab, so the keyboard should follow it
/// there, where arrowing through the strip is browsing and must keep the keyboard for the next
/// arrow. The library's mouse handler is not virtual, so the only seam is to wrap it: this
/// re-implements the interface member, calls the library's, and reports a click that selected a tab
/// afterwards — AFTER, because selecting a tab hands focus back to the strip, and anything that
/// moves focus into the tab has to be the last thing to run.</para>
///
/// <para>A CLICK ON THE ACTIVE TAB COUNTS TOO. Nothing changed index, but the user clicked a tab and
/// expects to type into it, and focus may well be elsewhere.</para>
///
/// <para>NOT A CLOSE CLICK. The close button shares the header row; closing a tab is not choosing
/// one, and focus should not jump into whichever tab happens to be left active.</para>
/// </summary>
public sealed class MouseAwareTabs : TabControl, IMouseAwareControl
{
    private bool _closeRequested;

    public MouseAwareTabs()
    {
        TabCloseRequested += (_, _) => _closeRequested = true;
    }

    /// <summary>Raised with the strip index of a tab the user chose with the mouse.</summary>
    public event Action<int>? SelectedByMouse;

    /// <summary>Hides the library's handler so a caller holding this type reaches the wrapper; the
    /// window dispatches through <see cref="IMouseAwareControl"/>, which resolves here as well.</summary>
    public new bool ProcessMouseEvent(MouseEventArgs args)
    {
        _closeRequested = false;
        var before = ActiveTabIndex;
        var handled = base.ProcessMouseEvent(args);

        var onHeader = args.Position.Y == Margin.Top;
        var chose = onHeader && !_closeRequested && handled
            && (args.HasFlag(MouseFlags.Button1Clicked)
                || (args.HasFlag(MouseFlags.Button3Clicked) && ActiveTabIndex != before));
        if (chose) SelectedByMouse?.Invoke(ActiveTabIndex);

        return handled;
    }
}

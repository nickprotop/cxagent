using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;

namespace CxAgent.Tests;

/// <summary>
/// Real keystrokes for a control that only takes them while focused.
///
/// <para>A list or a toolbar answers a key only when its window's focus manager has given it focus,
/// so one built alone in a test refuses every key. This puts the content in a window on the headless
/// driver — a whole window system, no terminal — gives the control focus there, and presses keys
/// through its own <c>ProcessKey</c>: the path a user's keystroke takes, not a test hook beside it.</para>
/// </summary>
internal sealed class HeadlessKeys
{
    private readonly IWindowControl _target;

    private HeadlessKeys(IWindowControl target) => _target = target;

    /// <summary>Hosts <paramref name="content"/> in a window and focuses <paramref name="target"/>.</summary>
    public static HeadlessKeys Focus(IWindowControl content, IWindowControl target)
    {
        var system = new ConsoleWindowSystem(new HeadlessConsoleDriver(80, 24));
        var window = new WindowBuilder(system).AddControl(content).Build();
        system.AddWindow(window);
        window.FocusManager.SetFocus((IFocusableControl)target, FocusReason.Keyboard);
        return new HeadlessKeys(target);
    }

    public bool Down() => Press(ConsoleKey.DownArrow, '\0');
    public bool Space() => Press(ConsoleKey.Spacebar, ' ');
    public bool Enter() => Press(ConsoleKey.Enter, '\r');
    public bool Tab() => Press(ConsoleKey.Tab, '\t');
    public bool Right() => Press(ConsoleKey.RightArrow, '\0');

    private bool Press(ConsoleKey key, char ch) =>
        ((IInteractiveControl)_target).ProcessKey(new ConsoleKeyInfo(ch, key, false, false, false));
}

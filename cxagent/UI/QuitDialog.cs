using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;
using SharpConsoleUI.Themes;

namespace CxAgent.UI;

/// <summary>What the quit question was answered with.</summary>
public enum QuitChoice
{
    Cancel,
    Quit,

    /// <summary>Quit, and do not ask again — "Don't ask again" was ticked when Quit was chosen.</summary>
    QuitAndStopAsking,
}

/// <summary>
/// "Quit cxagent?" — what Ctrl+Q asks when nothing is running. The question when agents ARE running is
/// <see cref="QuitGuard"/>'s, and this one never stands in for it.
///
/// <para>A DIALOG OF ITS OWN rather than <see cref="Config.ConfirmDialog"/> with a box bolted on: that one
/// guards edits and opens on its safe answer, while this one opens on Quit — nothing is running, so the
/// Enter that follows Ctrl+Q by reflex should do what the user just asked for.</para>
///
/// <para>THE ANSWERS HOLD THE FOCUS, the box is a Tab away. Focused first, the box would take that same
/// reflexive Enter as "tick me", and the user who meant only to quit would be asked again, now opted
/// out.</para>
/// </summary>
public static class QuitDialog
{
    private const string Quit = "Quit";
    private const string Cancel = "Cancel";

    private static Window? _window;
    private static ListControl? _list;
    private static CheckboxControl? _box;
    private static TaskCompletionSource<QuitChoice>? _answer;

    /// <summary>Whether the question is up — Escape answers it before anything under it.</summary>
    public static bool IsOpen => _window is not null;

    /// <summary>The answer Enter would choose right now, or null when the question is not up.</summary>
    public static string? Highlighted => _list?.SelectedItem?.Tag as string;

    /// <summary>Whether the answers hold the keyboard focus, so a reflexive Enter answers rather than ticks.</summary>
    public static bool AnswersFocused => _list?.HasFocus ?? false;

    /// <summary>Whether "Don't ask again" is ticked right now.</summary>
    public static bool StopAskingTicked => _box?.Checked ?? false;

    /// <summary>Answers Cancel and closes. Returns whether it was open.</summary>
    public static bool CloseIfOpen()
    {
        if (_window is not { } window) return false;
        _answer?.TrySetResult(QuitChoice.Cancel);
        window.Close();
        return true;
    }

    /// <summary>
    /// Hands a key to the dialog for a test with no input loop: Tab moves the focus, anything else goes to
    /// the focused control — the two routes a key takes inside a window.
    /// </summary>
    public static bool PressForTest(ConsoleKeyInfo key)
    {
        if (_window is not { } window) return false;
        if (key.Key == ConsoleKey.Tab)
        {
            window.FocusManager.MoveFocus(backward: (key.Modifiers & ConsoleModifiers.Shift) != 0);
            return true;
        }
        return (window.FocusManager.FocusedControl as IInteractiveControl)?.ProcessKey(key) ?? false;
    }

    /// <summary>
    /// Asks. A second ask while the question is up answers Cancel at once and leaves the first one
    /// standing, so a repeated Ctrl+Q cannot stack questions.
    /// </summary>
    public static Task<QuitChoice> AskAsync(ConsoleWindowSystem system)
    {
        if (_window is not null) return Task.FromResult(QuitChoice.Cancel);
        var answer = new TaskCompletionSource<QuitChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        _answer = answer;

        var window = new WindowBuilder(system)
            .WithTitle("Quit cxagent?")
            .WithSize(56, 9)
            .Centered()
            .AsModal()
            .WithBorderStyle(BorderStyle.Single)
            .Movable(true)
            .Minimizable(false)
            .Build();

        // THE PRIMARY ROLE, as ConfirmDialog has it: a full-strength bar, so the answer Enter will choose
        // is the most vivid thing in the list in every theme.
        var list = new ListControl { HorizontalAlignment = HorizontalAlignment.Stretch, ColorRole = ColorRole.Primary };
        foreach (var a in new[] { Quit, Cancel }) list.AddItem(new ListItem($"[bold]{MarkupParser.Escape(a)}[/]") { Tag = a });
        var box = new CheckboxControl("Don't ask again", isChecked: false);

        list.ItemActivated += (_, item) =>
        {
            answer.TrySetResult(item?.Tag as string != Quit ? QuitChoice.Cancel
                : box.Checked ? QuitChoice.QuitAndStopAsking
                : QuitChoice.Quit);
            window.Close();
        };
        window.AddControl(list);
        window.AddControl(new MarkupControl([""]));
        window.AddControl(box);
        window.AddControl(new MarkupControl([$"[{ColorScheme.MutedMarkup}]Enter chooses · Tab reaches the box · Esc cancels[/]"])
            { StickyPosition = StickyPosition.Bottom });

        window.OnClosed += (_, _) =>
        {
            _answer?.TrySetResult(QuitChoice.Cancel);
            _answer = null;
            _window = null;
            _list = null;
            _box = null;
        };

        _window = window;
        _list = list;
        _box = box;
        system.AddWindow(window);
        system.SetActiveWindow(window);
        window.FocusManager.SetFocus(list, FocusReason.Programmatic);
        list.SelectedIndex = 0;
        return answer.Task;
    }
}

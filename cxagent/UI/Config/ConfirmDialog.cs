using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;
using SharpConsoleUI.Themes;

namespace CxAgent.UI.Config;

/// <summary>
/// A small question over the configuration dialog: what is about to happen, said once, and the answers
/// as a list. Hand-composed, in the configuration dialog's own look — the same frame as "Apply to open
/// tabs?" — rather than a wizard step, whose frame repeats the title and crowds the reasons.
///
/// <para>ESC IS "NO": every question here guards an edit, and leaving things as they were is the answer
/// a reflex should reach.</para>
/// </summary>
public static class ConfirmDialog
{
    private static Window? _window;
    private static ListControl? _list;
    private static TaskCompletionSource<string?>? _answer;

    /// <summary>
    /// The answer Enter would choose right now, or null when no question is up — what a test reads to learn
    /// which answer a reflex reaches.
    /// </summary>
    public static string? Highlighted => _list?.SelectedItem?.Tag as string;

    /// <summary>The fill the highlighted answer is drawn on, or null when no question is up.</summary>
    public static SharpConsoleUI.Color? HighlightFill => _list?.HighlightBackgroundColor;

    /// <summary>Whether the answers hold the keyboard focus, so the highlight is drawn at full strength.</summary>
    public static bool AnswersFocused => _list?.HasFocus ?? false;

    /// <summary>Whether a question is up — Escape answers it before anything under it.</summary>
    public static bool IsOpen => _window is not null;

    /// <summary>Answers "no" and closes. Returns whether it was open.</summary>
    public static bool CloseIfOpen()
    {
        if (_window is not { } window) return false;
        _answer?.TrySetResult(null);
        window.Close();
        return true;
    }

    /// <summary>Asks, and returns the chosen answer, or null for Esc.</summary>
    /// <param name="system">The window system.</param>
    /// <param name="title">The question, short — the window's title.</param>
    /// <param name="lines">What the answer will do, one fact per line.</param>
    /// <param name="answers">The answers, the safe one first.</param>
    public static Task<string?> AskAsync(ConsoleWindowSystem system, string title, IReadOnlyList<string> lines, IReadOnlyList<string> answers)
    {
        if (_window is not null) return Task.FromResult<string?>(null);
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _answer = answer;

        var width = Math.Min(Math.Max(48, lines.Concat([title]).Max(l => l.Length) + 6), Console.WindowWidth - 4);
        var window = new WindowBuilder(system)
            .WithTitle(title)
            .WithSize(width, Math.Min(lines.Count + answers.Count + 7, Console.WindowHeight - 2))
            .Centered()
            .AsModal()
            .WithBorderStyle(BorderStyle.Single)
            .Movable(true)
            .Minimizable(false)
            .Build();

        window.AddControl(new MarkupControl([.. lines.Select(MarkupParser.Escape), ""]) { Wrap = true });
        // THE PRIMARY ROLE, so the highlighted answer is a full-strength bar in the theme's accent with text
        // chosen to read on it. A theme's own selection bar is often a dark shade under rows drawn in that
        // same bright accent, and in a two-answer question the answer NOT chosen then looks chosen — which,
        // over a stop that cannot be undone, is the misread that matters.
        var list = new ListControl { HorizontalAlignment = HorizontalAlignment.Stretch, ColorRole = ColorRole.Primary };
        foreach (var a in answers) list.AddItem(new ListItem($"[bold]{MarkupParser.Escape(a)}[/]") { Tag = a });
        list.ItemActivated += (_, item) =>
        {
            answer.TrySetResult(item?.Tag as string);
            window.Close();
        };
        window.AddControl(list);
        window.AddControl(new MarkupControl([$"[{ColorScheme.MutedMarkup}]Enter chooses · Esc cancels[/]"]) { StickyPosition = StickyPosition.Bottom });

        window.OnClosed += (_, _) =>
        {
            _answer?.TrySetResult(null);
            _answer = null;
            _window = null;
            _list = null;
        };

        _window = window;
        _list = list;
        system.AddWindow(window);
        system.SetActiveWindow(window);
        window.FocusManager.SetFocus(list, FocusReason.Programmatic);
        list.SelectedIndex = 0;
        return answer.Task;
    }
}

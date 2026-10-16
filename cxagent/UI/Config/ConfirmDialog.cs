using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

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
    private static TaskCompletionSource<string?>? _answer;

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
        var list = new ListControl { HorizontalAlignment = HorizontalAlignment.Stretch };
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
        };

        _window = window;
        system.AddWindow(window);
        system.SetActiveWindow(window);
        window.FocusManager.SetFocus(list, FocusReason.Programmatic);
        list.SelectedIndex = 0;
        return answer.Task;
    }
}

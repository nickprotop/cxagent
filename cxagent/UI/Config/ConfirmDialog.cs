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

    /// <summary>The answers as listed, in order, or null when no question is up — for a test.</summary>
    internal static IReadOnlyList<string>? AnswersForTest =>
        _list?.Items.Select(i => i.Tag as string ?? "").ToList();

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

    private const string CancelChoice = "Cancel";

    /// <summary>
    /// Asks a multi-line <paramref name="prompt"/> — its first line the question, the rest its detail —
    /// with <paramref name="choices"/> as the answers, and returns the one chosen, or null for Cancel or
    /// Esc. Safe from any thread: the dialog is opened on the UI thread.
    /// </summary>
    /// <param name="appendCancel">
    /// When true, Cancel is the last answer. False for a caller whose own last answer ends the question
    /// differently — "done" after an install keeps what was installed, which Cancel would not say.
    /// Esc still answers null either way.
    /// </param>
    public static async Task<string?> ChooseAsync(ConsoleWindowSystem system, string prompt,
        IReadOnlyList<string> choices, bool appendCancel = true)
    {
        var asked = await system.InvokeAsync(() => Choose(system, prompt, choices, appendCancel), "confirmDialog.Choose");
        return await asked;
    }

    /// <summary><see cref="ChooseAsync"/>'s work, on the UI thread already.</summary>
    internal static async Task<string?> Choose(ConsoleWindowSystem system, string prompt,
        IReadOnlyList<string> choices, bool appendCancel)
    {
        var answer = await AskAsync(system, TitleOf(prompt), DetailOf(prompt),
            appendCancel ? [.. choices, CancelChoice] : choices);
        return appendCancel && answer == CancelChoice ? null : answer;
    }

    /// <summary>A prompt's question: its first line, which is the dialog's title.</summary>
    internal static string TitleOf(string prompt) => prompt.Replace("\r\n", "\n").Split('\n')[0];

    /// <summary>A prompt's detail: the lines after its question, without the blank line that separates them.</summary>
    internal static IReadOnlyList<string> DetailOf(string prompt) =>
        [.. prompt.Replace("\r\n", "\n").Split('\n').Skip(1).SkipWhile(string.IsNullOrWhiteSpace)];

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

        // S, or wider when a line needs it: a fact cut by the border is a fact the answer is chosen without.
        var width = Math.Min(Math.Max(DialogLook.Width(DialogSize.S, Console.WindowWidth), lines.Concat([title]).Concat(answers).Max(l => l.Length) + 6), Console.WindowWidth - 4);
        var window = new WindowBuilder(system)
            .WithTitle(title)
            .WithSize(width, Math.Min(lines.Count + answers.Count + 9, Console.WindowHeight - 2))
            .Centered()
            .AsModal()
            .Styled()
            .Movable(true)
            .Minimizable(false)
            .Build();
        DialogLook.Titled(window);

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
        DialogLook.AddFooter(window, "Enter chooses · Esc cancels");

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

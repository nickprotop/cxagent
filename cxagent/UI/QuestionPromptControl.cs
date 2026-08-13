using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Ctl = SharpConsoleUI.Builders.Controls;

namespace CxAgent.UI;

/// <summary>
/// The model's question, in the composer — the same place the permission gate asks.
///
/// <para>ONE PLACE THE SESSION ASKS FOR INPUT. A question that appeared somewhere else would be a
/// second thing to watch, and the composer is already where "cxagent needs you" is answered. A user
/// who has approved one shell command knows exactly where to look.</para>
///
/// <para>OPTIONS ARE A REAL LIST, NOT BUTTONS. An option is a phrase — "use the existing parser and
/// add a case" — and buttons lay them ACROSS the screen, so three either wrap into an unreadable row
/// or get clipped to a width that loses the distinction being asked about. A list runs DOWN, where
/// length costs a line instead of legibility, and <c>ListControl</c> already answers on Enter with
/// arrow-key navigation, which is what a TUI user reaches for.</para>
///
/// <para>THE FIELD STAYS EITHER WAY. A list of options is the model's guess at the answers, and a
/// user with a fourth answer should not have to pick the closest wrong one.</para>
///
/// <para>AND CANCELLING IS AN ANSWER. Escape leaves the question unanswered and lets the model
/// continue on its own judgement — a question the user does not want to answer must not be a wall,
/// and the alternative is killing the turn to escape a dialog.</para>
/// </summary>
public sealed class QuestionPromptControl
{
    /// <summary>
    /// Past this the question is elided. A model can write a paragraph, and a paragraph in the
    /// composer pushes the transcript off screen to ask one thing.
    /// </summary>
    private const int MaxQuestionChars = 600;

    /// <summary>
    /// Per option. Generous — an option is a phrase, and the whole reason this is a list rather than
    /// a row of buttons is that phrases need room — but not unbounded, because a model that returns
    /// a paragraph per option has written a document, not a question.
    /// </summary>
    private const int MaxOptionChars = 160;

    private readonly TaskCompletionSource<string> _tcs = new();
    private readonly string _question;
    private readonly IReadOnlyList<string> _options;

    public QuestionPromptControl(string question, IReadOnlyList<string> options)
    {
        _question = question;
        _options = options;
    }

    /// <summary>Completes with the user's answer, or "" if they dismissed it.</summary>
    public Task<string> Completion => _tcs.Task;

    /// <summary>
    /// Resolves the prompt from outside — cancellation, or the turn ending under it.
    ///
    /// <para>THE CONTROL ITSELF MUST RESOLVE, not just the caller's await. A prompt left holding a
    /// live TaskCompletionSource keeps the composer swapped out, and the user is looking at a
    /// question nobody is waiting for the answer to. Same hazard the permission gate documents.</para>
    /// </summary>
    public void Resolve(string answer) => _tcs.TrySetResult(answer);

    public IWindowControl BuildContent()
    {
        var panel = Ctl.ScrollablePanel()
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();
        panel.BackgroundColor = ColorScheme.PromptSurface;

        var markup = Ctl.Markup();
        markup.AddLine($"[bold]{Escape(Clip(_question, MaxQuestionChars))}[/]");
        markup.AddLine($"[{ColorScheme.MutedMarkup}]"
                     + (_options.Count > 0
                        ? "↑↓ then Enter, or type your own answer. Esc to skip."
                        : "Type your answer. Esc to skip.")
                     + "[/]");
        panel.AddControl(markup.Build());

        // THE FRAMEWORK'S LIST, not a hand-drawn one. It navigates with the arrow keys and activates
        // on Enter — the interaction a TUI user already has in their fingers — where a markup block
        // would have made them read a number and type it.
        if (_options.Count > 0)
        {
            var list = Ctl.List()
                .AddItems(_options.Select(o => Escape(Clip(o, MaxOptionChars))).ToArray())
                .Build();

            // The model gets the OPTION TEXT, not an index: it wrote these, and handing back "2"
            // makes it re-derive which one that was — a model that miscounts then acts on the wrong
            // choice while believing the user picked it.
            list.ItemActivated += (_, item) =>
            {
                var index = list.Items.IndexOf(item);
                if (index >= 0 && index < _options.Count) _tcs.TrySetResult(_options[index]);
            };

            panel.AddControl(list);
        }

        panel.AddControl(Ctl.RuleBuilder()
            .WithColorRole(ColorScheme.Structure)
            .WithMargin(1, 0, 1, 0)
            .Build());

        // TYPE INSTEAD. The options are the model's guess at the answers; a user with a fourth
        // answer should not have to pick the closest wrong one.
        var input = Ctl.Prompt()
            .WithMargin(1, 0, 1, 0)
            .Build();
        input.InputChanged += (_, text) =>
        {
            if (!string.IsNullOrWhiteSpace(text)) _tcs.TrySetResult(text.Trim());
        };
        panel.AddControl(input);

        return panel;
    }

    /// <summary>
    /// The user declined to answer.
    ///
    /// <para>AN EMPTY STRING, WHICH THE TOOL READS AS "PROCEED ON YOUR OWN JUDGEMENT". Not a
    /// cancellation of the turn: a question the user does not want to answer must not be a wall, and
    /// making Escape kill the run would mean the only way out of a dialog is to lose the work.</para>
    /// </summary>
    public void Skip() => _tcs.TrySetResult(string.Empty);

    private static string Escape(string text) => SharpConsoleUI.Parsing.MarkupParser.Escape(text);

    private static string Clip(string text, int max) =>
        text.Length > max ? text[..max].TrimEnd() + "…" : text;
}

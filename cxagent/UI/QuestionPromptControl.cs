using CxAgent.Core.Agent;
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
/// <para>ONE QUESTION ON SCREEN AT A TIME, even when several were asked. The composer is a few rows
/// tall: three questions with described options stacked into it would push the transcript off screen
/// and clip the last of them. Stepping through is also what makes BACK possible — a user who realises
/// their first answer was wrong while reading the second question can go and change it, which a
/// single submit-everything panel cannot offer.</para>
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
    /// Per option label. Generous — an option is a phrase, and the whole reason this is a list rather
    /// than a row of buttons is that phrases need room — but not unbounded, because a model that
    /// returns a paragraph per option has written a document, not a question.
    /// </summary>
    private const int MaxOptionChars = 160;

    /// <summary>Per option description. One wrapped line's worth; the label carries the choice.</summary>
    private const int MaxDescriptionChars = 200;

    private readonly TaskCompletionSource<QuestionAnswers> _tcs = new();
    private readonly IReadOnlyList<UserQuestion> _questions;
    private readonly string[] _answers;

    private int _step;
    private ListControl? _list;
    private PromptControl? _input;
    private bool _summarising;

    public QuestionPromptControl(IReadOnlyList<UserQuestion> questions)
    {
        _questions = questions;
        _answers = new string[questions.Count];
        Array.Fill(_answers, "");
    }

    /// <summary>Completes when every question has been answered, skipped, or the run dismissed.</summary>
    public Task<QuestionAnswers> Completion => _tcs.Task;

    /// <summary>Raised when the step changes, so the host can swap in the new content.</summary>
    public event Action<IWindowControl>? StepChanged;

    /// <summary>
    /// What should hold focus on this step — the option list when there is one, else the text field.
    ///
    /// <para>THE LIST, NOT THE FIELD, AND THE DRIVE SHOWED WHY. Focus started on the panel, so the
    /// first Enter did nothing: the user had to press Down before the list would answer. A question
    /// whose obvious keystroke does nothing reads as a hung app, and the fix belongs here because
    /// only this type knows whether this step has a list at all.</para>
    /// </summary>
    public IFocusableControl? FocusTarget => (IFocusableControl?)_list ?? _input;

    /// <summary>
    /// Resolves the prompt from outside — cancellation, or the turn ending under it.
    ///
    /// <para>THE CONTROL ITSELF MUST RESOLVE, not just the caller's await. A prompt left holding a
    /// live TaskCompletionSource keeps the composer swapped out, and the user is looking at a
    /// question nobody is waiting for the answer to. Same hazard the permission gate documents.</para>
    /// </summary>
    public void Resolve(QuestionAnswers answers) => _tcs.TrySetResult(answers);

    /// <summary>
    /// The user declined to go on.
    ///
    /// <para>WHAT IS ALREADY ANSWERED IS KEPT. Escaping on question three does not throw away the
    /// two decisions already made — they were real answers, and making the user repeat them is a
    /// punishment for changing their mind about the third.</para>
    /// </summary>
    public void Skip()
    {
        // NOTHING ANSWERED AT ALL IS A CANCEL, not a set of blank answers: "I do not want to be asked"
        // and "decide the first one for me" are different messages to send the model.
        if (_answers.All(string.IsNullOrWhiteSpace) && _step == 0 && !_summarising)
        {
            _tcs.TrySetResult(QuestionAnswers.Cancel);
            return;
        }

        _tcs.TrySetResult(new QuestionAnswers(_answers));
    }

    public IWindowControl BuildContent() => BuildStep();

    private IWindowControl BuildStep() => _summarising ? BuildSummary() : BuildQuestion();

    /// <summary>
    /// Everything that was answered, before any of it is sent.
    ///
    /// <para>THE LAST CHANCE TO CHANGE A DECISION, and the only place the set is visible as a set.
    /// Stepping shows one question at a time, which is what makes it readable — and also means that
    /// by question three nobody remembers exactly what they said to question one. The answers are
    /// about to become instructions the model acts on; reviewing them costs one keypress and
    /// catching a wrong one afterwards costs an edit to the wrong file.</para>
    ///
    /// <para>ONLY FOR SEVERAL QUESTIONS. Confirming a single answer the user just typed is ceremony
    /// — they are looking at it.</para>
    /// </summary>
    private IWindowControl BuildSummary()
    {
        var panel = Ctl.ScrollablePanel()
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();
        panel.BackgroundColor = ColorScheme.PromptSurface;

        var markup = Ctl.Markup();
        markup.AddLine($"[bold]Your answers[/]");
        markup.AddLine($"[{ColorScheme.MutedMarkup}]Enter to send, Alt+← to change the last one. "
                     + "Esc to send as-is.[/]");
        markup.AddLine("");

        for (var i = 0; i < _questions.Count; i++)
        {
            var header = string.IsNullOrWhiteSpace(_questions[i].Header)
                ? Clip(_questions[i].Question, 60)
                : _questions[i].Header!;

            var answer = string.IsNullOrWhiteSpace(_answers[i])
                ? $"[{ColorScheme.MutedMarkup}](skipped — you decide)[/]"
                : Escape(_answers[i]);

            markup.AddLine($"  [{ColorScheme.MutedMarkup}]{Escape(header)}[/]  {answer}");
        }

        panel.AddControl(markup.Build());
        panel.AddControl(Ctl.RuleBuilder()
            .WithColorRole(ColorScheme.Structure)
            .WithMargin(1, 0, 1, 0)
            .Build());

        var input = Ctl.Prompt()
            .WithMargin(1, 0, 1, 0)
            .Build();
        _input = input;
        _list = null;

        // ENTER SENDS. Typing here is not an answer to anything — there is no question on screen —
        // so anything typed is ignored and the keypress just confirms.
        input.Entered += (_, _) => _tcs.TrySetResult(new QuestionAnswers(_answers));
        panel.AddControl(input);

        return panel;
    }

    private IWindowControl BuildQuestion()
    {
        var q = _questions[_step];

        var panel = Ctl.ScrollablePanel()
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();
        panel.BackgroundColor = ColorScheme.PromptSurface;

        var markup = Ctl.Markup();

        // THE STEP INDICATOR, only when there is more than one. "Question 1 of 1" is ceremony, and
        // the header names what is being decided where a bare count would not.
        if (_questions.Count > 1)
        {
            var header = string.IsNullOrWhiteSpace(q.Header) ? "" : $" · {Escape(q.Header!)}";
            markup.AddLine($"[{ColorScheme.MutedMarkup}]Question {_step + 1} of "
                         + $"{_questions.Count}{header}[/]");
        }
        else if (!string.IsNullOrWhiteSpace(q.Header))
        {
            markup.AddLine($"[{ColorScheme.MutedMarkup}]{Escape(q.Header!)}[/]");
        }

        markup.AddLine($"[bold]{Escape(Clip(q.Question, MaxQuestionChars))}[/]");
        markup.AddLine($"[{ColorScheme.MutedMarkup}]{Hint(q)}[/]");
        panel.AddControl(markup.Build());

        if (q.HasOptions)
        {
            _list = BuildList(q);
            panel.AddControl(_list);
        }
        else
        {
            _list = null;
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
        _input = input;

        // ON ENTER, NOT ON EVERY KEYSTROKE. This was InputChanged, which fires per character — so
        // the answer resolved on the FIRST letter typed: the model got "c" for "config-prod.yaml",
        // the composer was restored under the user mid-word, and the rest spilled into the
        // transcript as stray text.
        input.Entered += (_, text) =>
        {
            if (!string.IsNullOrWhiteSpace(text)) Advance(text.Trim());

            // EMPTY ENTER IS NOT AN ANSWER. It would read as a skip — Skip() also completes with "" —
            // but a user who pressed Enter by accident has not decided anything, so the question
            // stays up. With a list present, an empty Enter takes the highlighted option instead.
            else if (_list is { } list && q.HasOptions) TakeFromList(list, q);
        };
        panel.AddControl(input);

        return panel;
    }

    private ListControl BuildList(UserQuestion q)
    {
        var list = Ctl.List()
            .WithCheckboxMode(q.Multiple)
            .Build();

        foreach (var option in q.Choices)
        {
            // LABEL AND DESCRIPTION IN ONE ITEM, as two lines. A described option is the difference
            // between a choice a user can make and a pair of words they have to guess between —
            // "rewrite the parser" versus "patch the tokenizer" says nothing about which they want.
            var text = Escape(Clip(option.Label, MaxOptionChars));
            if (!string.IsNullOrWhiteSpace(option.Description))
                text += $"\n  {Escape(Clip(option.Description!, MaxDescriptionChars))}";

            list.Items.Add(new ListItem(text));
        }

        // The model gets the OPTION TEXT, not an index: it wrote these, and handing back "2" makes it
        // re-derive which one that was — a model that miscounts then acts on the wrong choice while
        // believing the user picked it.
        list.ItemActivated += (_, _) => TakeFromList(list, q);

        // THE FIRST OPTION IS HIGHLIGHTED FROM THE START. A list opens with SelectedIndex = -1, so
        // Enter had nothing to activate and did nothing at all until the user pressed Down — twice
        // on a live drive, which reads as a hung app rather than as a list waiting to be navigated.
        // It also makes "make your recommendation first" mean something: the recommended option is
        // the one already under the cursor.
        if (!q.Multiple && list.Items.Count > 0) list.SelectedIndex = 0;

        return list;
    }

    /// <summary>
    /// Takes whatever the list currently has chosen, as pressing Enter on it would.
    ///
    /// <para>A TEST SEAM, and the only one here. <c>ListControl.ProcessKey</c> refuses every key
    /// unless it <c>HasFocus</c>, which is computed from a live window's focus manager — so the
    /// multi-select path (check some items, submit) cannot be exercised by simulating keystrokes
    /// outside a running app. This is the same operation the Enter handler performs.</para>
    /// </summary>
    public void SubmitFromList()
    {
        if (_list is { } list) TakeFromList(list, _questions[_step]);
    }

    /// <summary>Reads what is chosen in the list and moves on, if anything is.</summary>
    private void TakeFromList(ListControl list, UserQuestion q)
    {
        if (q.Multiple)
        {
            // SPACE CHECKS, ENTER SUBMITS. Nothing checked means nothing chosen — advancing on an
            // empty selection would record "none of these" as though it had been decided.
            var checkedItems = list.GetCheckedItems();
            if (checkedItems.Count == 0) return;

            var labels = checkedItems
                .Select(i => q.Choices.ElementAtOrDefault(list.Items.IndexOf(i)).Label)
                .Where(l => !string.IsNullOrEmpty(l));

            Advance(string.Join(", ", labels));
            return;
        }

        var index = list.SelectedIndex;
        if (index >= 0 && index < q.Choices.Count) Advance(q.Choices[index].Label);
    }

    /// <summary>Records an answer and moves to the next question, or finishes.</summary>
    private void Advance(string answer)
    {
        _answers[_step] = answer;

        if (_step + 1 >= _questions.Count)
        {
            // ONE QUESTION NEEDS NO REVIEW: the user is looking at the answer they just gave.
            if (_questions.Count == 1)
            {
                _tcs.TrySetResult(new QuestionAnswers(_answers));
                return;
            }

            _summarising = true;
            StepChanged?.Invoke(BuildStep());
            return;
        }

        _step++;
        StepChanged?.Invoke(BuildStep());
    }

    /// <summary>Back to the previous question, so an answer can be reconsidered.</summary>
    public bool Back()
    {
        // FROM THE SUMMARY, back to the last question — which is the answer someone is most likely
        // to want to change, having just read it.
        if (_summarising)
        {
            _summarising = false;
            StepChanged?.Invoke(BuildStep());
            return true;
        }

        if (_step == 0) return false;

        _step--;
        StepChanged?.Invoke(BuildStep());
        return true;
    }

    private string Hint(UserQuestion q)
    {
        // ALT+LEFT, NOT BACKSPACE OR PLAIN LEFT. Both of those are text editing in the field right
        // below, and a global shortcut that swallows Backspace would make the free-text answer
        // uncorrectable.
        var back = _step > 0 ? ", Alt+← to go back" : "";

        if (!q.HasOptions) return $"Type your answer{back}. Esc to skip.";

        return q.Multiple
            ? $"Space to check, Enter when done, or type your own{back}. Esc to skip."
            : $"↑↓ then Enter, or type your own answer{back}. Esc to skip.";
    }

    private static string Escape(string text) => SharpConsoleUI.Parsing.MarkupParser.Escape(text);

    private static string Clip(string text, int max) =>
        text.Length > max ? text[..max].TrimEnd() + "…" : text;
}

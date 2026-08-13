namespace CxAgent.Core.Agent;

/// <summary>
/// One answer the model is offering, and what choosing it means.
/// </summary>
/// <param name="Label">The choice itself, short enough to read in a list row.</param>
/// <param name="Description">
/// What this option means or what happens if it is chosen — the trade-off, in the model's own words.
///
/// <para>WITHOUT IT, OPTIONS ARE UNANSWERABLE. "Rewrite the parser" and "Patch the tokenizer" tell a
/// user nothing about which they want; the reason one is being offered lives in the model's head and
/// nowhere on screen. A list of bare labels asks the user to guess what the model was thinking.</para>
/// </param>
public readonly record struct QuestionOption(string Label, string? Description = null);

/// <summary>
/// One question, with its answers.
/// </summary>
/// <param name="Header">
/// A very short label — two or three words — naming what is being decided.
///
/// <para>It is the step's title in a multi-question run, where "Question 2 of 3" alone tells the
/// user nothing about what they are about to be asked.</para>
/// </param>
/// <param name="Question">The question itself, in full.</param>
/// <param name="Options">
/// The answers the model can act on, or empty when the answer is free text.
/// </param>
/// <param name="Multiple">
/// May more than one option be chosen? For questions where the choices are not exclusive — which
/// checks to enable, which files to include.
/// </param>
public sealed record UserQuestion(
    string Question,
    string? Header = null,
    IReadOnlyList<QuestionOption>? Options = null,
    bool Multiple = false)
{
    /// <summary>Never null, so every consumer can enumerate without a guard.</summary>
    public IReadOnlyList<QuestionOption> Choices => Options ?? [];

    /// <summary>Is this a list to pick from, or a box to type in?</summary>
    public bool HasOptions => Choices.Count > 0;
}

/// <summary>
/// What the user said, per question.
/// </summary>
/// <param name="Answers">
/// One entry per question asked, in the order asked. An empty string means that question was
/// skipped — which is an answer of a kind, and is reported to the model as one.
/// </param>
/// <param name="Cancelled">
/// The user dismissed the whole run rather than answering. Distinguished from skipping every
/// question: a skip says "decide for me", a cancel says "stop".
/// </param>
public readonly record struct QuestionAnswers(IReadOnlyList<string> Answers, bool Cancelled = false)
{
    public static QuestionAnswers Cancel => new([], Cancelled: true);
}

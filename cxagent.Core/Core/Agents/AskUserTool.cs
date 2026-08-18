using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Agents;

/// <summary>
/// <c>question</c> — the model asks the user questions and waits for the answers.
///
/// <para>WHAT IT IS FOR, and what it is not. The permission gate already asks "may I"; nothing asked
/// "which one did you mean". Without it an ambiguous request gets a guess — and a guess on "update
/// the config" when there are three config files is a wrong edit the user then has to find — or a
/// turn that ends with a question in its final message, which reads as an answer and is not one.</para>
///
/// <para>HIDDEN FROM SUB-AGENTS ENTIRELY, not refused at call time. A child has no user: its output
/// goes to its parent, and a child blocking on a question nobody can see is a hang that ends only
/// when the parent's turn is cancelled. Withholding the definition is the same mechanism that makes
/// "no sub-agents of sub-agents" structural rather than a rule — it is not a tool the child is asked
/// not to use, it is one it was never given.</para>
/// </summary>
public sealed class AskUserTool(Func<IReadOnlyList<UserQuestion>, CancellationToken, Task<QuestionAnswers>> ask)
{
    public string ToolName => "question";

    /// <summary>
    /// A NAME THIS TOOL ALSO ANSWERS TO — the name this tool carried before it was renamed to match opencode.
    ///
    /// <para>ACCEPTED, NOT ADVERTISED. Only <see cref="ToolName"/> is sent to the model, so nothing
    /// pulls it toward the old spelling. But a rename is invisible to a model working from habit or
    /// from a resumed conversation whose earlier turns used the old name, and an unknown tool is a
    /// hard failure that costs a turn to recover from — for no reason, since the call is
    /// unambiguous. Accepting it costs one comparison.</para>
    /// </summary>
    private const string LegacyName = "ask_user";

    /// <summary>Is this call for this tool, under either name?</summary>
    private bool Claims(string name) =>
        string.Equals(name, ToolName, StringComparison.Ordinal)
        || string.Equals(name, LegacyName, StringComparison.Ordinal);

    /// <summary>
    /// How many questions one call may carry.
    ///
    /// <para>A wizard the user has to page through four times is an interrogation, and a model that
    /// wants five answers has not thought hard enough about which two matter. Past the cap the extras
    /// are dropped and the model is told, rather than silently losing questions it believes it
    /// asked.</para>
    /// </summary>
    public const int MaxQuestions = 4;

    /// <summary>Per question. More than this is a menu, not a decision.</summary>
    public const int MaxOptions = 6;

    /// <summary>
    /// The description says what the tool is FOR.
    ///
    /// <para>IT USED TO ARGUE AGAINST ITSELF — "use this ONLY when you cannot proceed", followed by a
    /// paragraph on what not to do and the line "a question the user has to answer costs them more
    /// than a tool call costs you". The intent was to prevent a model that asks before every
    /// decision. The effect, across three live drives, was a model that never called it at all: on
    /// the last one it wanted to consult the user, and asked in PROSE instead — the failure this tool
    /// exists to prevent, caused by the tool's own description.</para>
    ///
    /// <para>One line of restraint stays. The rest describes the mechanism, which is what a tool
    /// description is for.</para>
    /// </summary>
    public ToolDefinition Definition => new(
        ToolName,
        "Ask the user questions and wait for their answers. Use this to:\n"
        + "1. Gather user preferences or requirements\n"
        + "2. Clarify an ambiguous instruction — which of several files or approaches they meant\n"
        + "3. Get a decision on an implementation choice as you work\n"
        + "4. Offer a choice about what direction to take\n\n"
        + "Usage notes:\n"
        + $"- Ask up to {MaxQuestions} questions in ONE call; they are presented as steps and answered "
        + "together. Do not call the tool repeatedly for related decisions.\n"
        + "- Give each option a short label and a description saying what it means or what happens if "
        + "it is chosen. A list of bare labels asks the user to guess what you were thinking.\n"
        + "- If you recommend an option, make it first and add \"(Recommended)\" to its label.\n"
        + "- The user can always type an answer of their own, so do not add an \"Other\" option.\n"
        + "- Set \"multiple\": true when more than one option can be chosen at once.\n"
        + "- Omit \"options\" entirely when the answer is free text.\n"
        + "- Prefer investigating first: if the answer is in the code, read the code.",
        JsonDocument.Parse($$"""
            {
              "type": "object",
              "properties": {
                "questions": {
                  "type": "array",
                  "maxItems": {{MaxQuestions}},
                  "description": "The questions to ask, in order. Usually one.",
                  "items": {
                    "type": "object",
                    "properties": {
                      "question": {
                        "type": "string",
                        "description": "The question in full. State what you will do with each answer."
                      },
                      "header": {
                        "type": "string",
                        "description": "Very short label naming what is being decided, e.g. 'Config file'. Max 30 chars."
                      },
                      "options": {
                        "type": "array",
                        "maxItems": {{MaxOptions}},
                        "description": "The answers you can act on. Omit for a free-text answer.",
                        "items": {
                          "type": "object",
                          "properties": {
                            "label": {
                              "type": "string",
                              "description": "The choice itself, short enough to read in a list row."
                            },
                            "description": {
                              "type": "string",
                              "description": "What this option means, or what happens if it is chosen."
                            }
                          },
                          "required": ["label"]
                        }
                      },
                      "multiple": {
                        "type": "boolean",
                        "description": "Allow more than one option to be chosen. Default false."
                      }
                    },
                    "required": ["question"]
                  }
                }
              },
              "required": ["questions"]
            }
            """).RootElement.Clone());

    /// <summary>Runs the call, or returns null if it is not this tool's.</summary>
    public async Task<string?> TryInvokeAsync(ToolCall call, CancellationToken ct)
    {
        if (!Claims(call.Name)) return null;

        List<UserQuestion> questions;
        var dropped = 0;

        try
        {
            questions = Parse(call.Arguments, out dropped);
        }
        catch (Exception)
        {
            return "question could not read its arguments. Send "
                 + "{\"questions\": [{\"question\": \"…\"}]}.";
        }

        if (questions.Count == 0)
            return "question needs at least one question: "
                 + "{\"questions\": [{\"question\": \"…\"}]}.";

        // CANCELLATION IS THE USER'S OTHER ANSWER. Escape while a question is up means "stop", and
        // the tool must still return a RESULT — an unanswered tool call is the orphan that 400s a
        // session permanently, and it would be a bitter way to lose one.
        try
        {
            var answers = await ask(questions, ct);

            if (answers.Cancelled)
                return "The user dismissed the questions without answering. Proceed on your own "
                     + "judgement, or say what you need.";

            return Format(questions, answers, dropped);
        }
        catch (OperationCanceledException)
        {
            return "cancelled: the user stopped this turn before answering.";
        }
    }

    /// <summary>
    /// Reads the arguments into questions.
    ///
    /// <para>THREE SHAPES ARE ACCEPTED, because a model that gets the envelope slightly wrong should
    /// still reach the user rather than receive a schema lecture: the documented
    /// <c>{questions: [...]}</c>, a single <c>{question, options}</c> at the top level, and a bare
    /// string. The last two were this tool's whole schema until recently, and a model trained on
    /// either will produce them.</para>
    ///
    /// <para>Options are read as objects OR as plain strings, for the same reason.</para>
    /// </summary>
    private static List<UserQuestion> Parse(JsonElement args, out int dropped)
    {
        dropped = 0;
        var result = new List<UserQuestion>();

        if (args.ValueKind == JsonValueKind.String)
        {
            // A bare string is the question. Nothing else could be meant.
            if (args.GetString() is { Length: > 0 } only) result.Add(new UserQuestion(only.Trim()));
            return result;
        }

        if (args.ValueKind != JsonValueKind.Object) return result;

        if (args.TryGetProperty("questions", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (ReadQuestion(item) is not { } q) continue;

                if (result.Count == MaxQuestions) { dropped++; continue; }
                result.Add(q);
            }

            return result;
        }

        // The older single-question shape, still the most likely thing a model sends.
        if (ReadQuestion(args) is { } single) result.Add(single);

        return result;
    }

    private static UserQuestion? ReadQuestion(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString() is { Length: > 0 } s ? new UserQuestion(s.Trim()) : null;

        if (element.ValueKind != JsonValueKind.Object) return null;

        if (!element.TryGetProperty("question", out var q) || q.ValueKind != JsonValueKind.String)
            return null;

        if (q.GetString() is not { Length: > 0 } text || string.IsNullOrWhiteSpace(text)) return null;

        string? header = element.TryGetProperty("header", out var h) && h.ValueKind == JsonValueKind.String
            ? h.GetString()
            : null;

        var multiple = element.TryGetProperty("multiple", out var m)
                    && m.ValueKind is JsonValueKind.True;

        var options = new List<QuestionOption>();
        if (element.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in o.EnumerateArray())
            {
                if (options.Count == MaxOptions) break;

                // A PLAIN STRING IS A LABEL. The previous schema said options were strings, and a
                // model producing them should get its question asked rather than an error.
                if (option.ValueKind == JsonValueKind.String)
                {
                    if (option.GetString() is { Length: > 0 } label)
                        options.Add(new QuestionOption(label.Trim()));
                    continue;
                }

                if (option.ValueKind != JsonValueKind.Object) continue;
                if (!option.TryGetProperty("label", out var l) || l.ValueKind != JsonValueKind.String)
                    continue;
                if (l.GetString() is not { Length: > 0 } text2) continue;

                var description = option.TryGetProperty("description", out var d)
                               && d.ValueKind == JsonValueKind.String
                    ? d.GetString()
                    : null;

                options.Add(new QuestionOption(text2.Trim(), description?.Trim()));
            }
        }

        return new UserQuestion(text.Trim(), header?.Trim(), options, multiple);
    }

    /// <summary>
    /// The tool result: each question paired with what was said.
    ///
    /// <para>PAIRED, NOT A BARE LIST OF ANSWERS. With three questions asked, three loose strings
    /// leave the model matching them back by position — and a model that miscounts acts on the wrong
    /// decision while believing the user chose it.</para>
    /// </summary>
    private static string Format(
        IReadOnlyList<UserQuestion> questions, QuestionAnswers answers, int dropped)
    {
        var parts = new List<string>();

        for (var i = 0; i < questions.Count; i++)
        {
            var answer = i < answers.Answers.Count ? answers.Answers[i] : "";

            parts.Add(string.IsNullOrWhiteSpace(answer)
                ? $"\"{questions[i].Question}\" = (skipped)"
                : $"\"{questions[i].Question}\" = \"{answer}\"");
        }

        var text = $"The user answered: {string.Join("; ", parts)}.";

        if (parts.Any(p => p.EndsWith("(skipped)", StringComparison.Ordinal)))
            text += " A skipped question means they would rather you decided; use your own judgement "
                  + "there.";

        // SAID OUT LOUD. A model that believes it asked five questions and hears back about four
        // will act on an answer nobody gave.
        if (dropped > 0)
            text += $" ({dropped} further question{(dropped == 1 ? " was" : "s were")} not asked — "
                  + $"at most {MaxQuestions} per call. Ask again if you still need them.)";

        return text;
    }
}

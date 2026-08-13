using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Agent;

/// <summary>
/// <c>ask_user</c> — the model asks a question and waits for the answer.
///
/// <para>WHAT IT IS FOR, and what it is not. The permission gate already asks "may I"; nothing asked
/// "which one did you mean". Today an ambiguous request gets a guess — and a guess on "update the
/// config" when there are three config files is a wrong edit the user then has to find — or a turn
/// that ends with a question in its final message, which reads as an answer and is not one.</para>
///
/// <para>HIDDEN FROM SUB-AGENTS ENTIRELY, not refused at call time. A child has no user: its output
/// goes to its parent, and a child blocking on a question nobody can see is a hang that ends only
/// when the parent's turn is cancelled. Withholding the definition is the same mechanism that makes
/// "no sub-agents of sub-agents" structural rather than a rule — it is not a tool the child is asked
/// not to use, it is one it was never given.</para>
/// </summary>
public sealed class AskUserTool(Func<string, IReadOnlyList<string>, CancellationToken, Task<string>> ask)
{
    public string ToolName => "ask_user";

    /// <summary>
    /// The description argues AGAINST use as much as for it. A model that asks before every decision
    /// is worse than one that guesses: it turns a delegated task back into a conversation, and the
    /// user delegated precisely to avoid that.
    /// </summary>
    public ToolDefinition Definition => new(
        ToolName,
        "Ask the user a question and wait for their answer. Use this ONLY when you cannot proceed "
        + "without it — the request is genuinely ambiguous, or a choice is theirs to make (which of "
        + "several files they meant, whether to take an approach with a real trade-off).\n\n"
        + "Do NOT use it to confirm something you can check, to ask permission (tools already ask "
        + "when they need to), or to report progress. Prefer investigating: if the answer is in the "
        + "code, read the code. A question the user has to answer costs them more than a tool call "
        + "costs you.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "question": {
                  "type": "string",
                  "description": "One clear question. State what you will do with each answer."
                },
                "options": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Optional. The answers you can act on, if they are enumerable."
                }
              },
              "required": ["question"]
            }
            """).RootElement.Clone());

    /// <summary>Runs the call, or returns null if it is not this tool's.</summary>
    public async Task<string?> TryInvokeAsync(ToolCall call, CancellationToken ct)
    {
        if (!string.Equals(call.Name, ToolName, StringComparison.Ordinal)) return null;

        string? question = null;
        var options = new List<string>();

        try
        {
            if (call.Arguments.ValueKind == JsonValueKind.String)
            {
                // A bare string is the question. Nothing else could be meant.
                question = call.Arguments.GetString();
            }
            else if (call.Arguments.ValueKind == JsonValueKind.Object)
            {
                if (call.Arguments.TryGetProperty("question", out var q) && q.ValueKind == JsonValueKind.String)
                    question = q.GetString();

                if (call.Arguments.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array)
                    foreach (var option in o.EnumerateArray())
                        if (option.ValueKind == JsonValueKind.String && option.GetString() is { Length: > 0 } s)
                            options.Add(s);
            }
        }
        catch (Exception)
        {
            return "ask_user could not read its arguments. Send {\"question\": \"…\"}.";
        }

        if (string.IsNullOrWhiteSpace(question))
            return "ask_user needs a 'question'.";

        // CANCELLATION IS THE USER'S OTHER ANSWER. Escape while a question is up means "stop", and
        // the tool must still return a RESULT — an unanswered tool call is the orphan that 400s a
        // session permanently, and it would be a bitter way to lose one.
        try
        {
            var answer = await ask(question!.Trim(), options, ct);
            return string.IsNullOrWhiteSpace(answer)
                ? "The user dismissed the question without answering. Proceed on your own judgement, "
                + "or say what you need."
                : answer;
        }
        catch (OperationCanceledException)
        {
            return "cancelled: the user stopped this turn before answering.";
        }
    }
}

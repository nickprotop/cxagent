using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Agents;

/// <summary>
/// <c>todowrite</c> — the model writes and rewrites its own plan.
///
/// <para>AGENT-OWNED, like the skill loader and for the same reason: the list is per-agent state,
/// and a plugin has no agent identity. It sits in the dispatch chain beside the spawner rather than
/// in <see cref="Plugins.PluginRegistry"/>.</para>
///
/// <para>NO PERMISSION GATE. Every other tool touches the world — a file, a shell, a socket — and
/// the gate exists for that. This writes to a list the model already controls and that only the
/// model reads back. A prompt here would train the user to approve things reflexively, which is the
/// one habit a permission system cannot survive.</para>
/// </summary>
public sealed class TodoTool(TodoList list)
{
    public string ToolName => "todowrite";

    /// <summary>
    /// A NAME THIS TOOL ALSO ANSWERS TO — the name this tool carried before it was renamed to match opencode and Claude Code.
    ///
    /// <para>ACCEPTED, NOT ADVERTISED. Only <see cref="ToolName"/> is sent to the model, so nothing
    /// pulls it toward the old spelling. But a rename is invisible to a model working from habit or
    /// from a resumed conversation whose earlier turns used the old name, and an unknown tool is a
    /// hard failure that costs a turn to recover from — for no reason, since the call is
    /// unambiguous. Accepting it costs one comparison.</para>
    /// </summary>
    private const string LegacyName = "update_todos";

    /// <summary>Is this call for this tool, under either name?</summary>
    private bool Claims(string name) =>
        string.Equals(name, ToolName, StringComparison.Ordinal)
        || string.Equals(name, LegacyName, StringComparison.Ordinal);

    /// <summary>
    /// The description is doing most of the work here, and it is worth being long.
    ///
    /// <para>A todo tool that exists but is never called is worse than no todo tool: it costs prompt
    /// weight and buys nothing. Both reference implementations ship a description several times this
    /// size, listing when to use it and — as importantly — when not to, because the failure mode
    /// beyond "never calls it" is "opens a three-item list to read one file".</para>
    /// </summary>
    public ToolDefinition Definition => new(
        ToolName,
        "Record and update your plan for multi-step work. Send the WHOLE list every time — it "
        + "replaces the previous one.\n\n"
        + "Use it when a task needs three or more real steps, when the user gives you several "
        + "things at once, or when you learn something mid-task that changes the plan. Do not use "
        + "it for a single action or for work you will finish this turn.\n\n"
        + "Mark exactly one item in_progress before you start it, and completed only once it is "
        + "genuinely done — not when you intend to do it. If you get blocked, leave the item "
        + "in_progress and add one describing the blocker. The list is shown back to you at the "
        + "start of every turn, so it is how you remember what you were doing.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "todos": {
                  "type": "array",
                  "description": "The complete list, in order. Replaces the previous one.",
                  "items": {
                    "type": "object",
                    "properties": {
                      "text": { "type": "string", "description": "What to do, specific and actionable." },
                      "status": {
                        "type": "string",
                        "enum": ["pending", "in_progress", "completed", "cancelled"],
                        "description": "Defaults to pending."
                      }
                    },
                    "required": ["text"]
                  }
                }
              },
              "required": ["todos"]
            }
            """).RootElement.Clone());

    /// <summary>Runs the call, or returns null if it is not this tool's.</summary>
    public string? TryInvoke(ToolCall call)
    {
        if (!Claims(call.Name)) return null;

        JsonElement todos;
        try
        {
            if (call.Arguments.ValueKind == JsonValueKind.Array)
            {
                // A BARE ARRAY IS THE LIST. Models send [{...}] instead of {"todos":[{...}]} often
                // enough that refusing it would be pedantry — nothing else could be meant.
                todos = call.Arguments;
            }
            else if (call.Arguments.ValueKind != JsonValueKind.Object
                     || !call.Arguments.TryGetProperty("todos", out todos))
            {
                return "todowrite needs a 'todos' array. Send the whole list each time.";
            }
        }
        catch (Exception)
        {
            return "todowrite could not read its arguments. Send {\"todos\": [ … ]}.";
        }

        var items = TodoList.Parse(todos);

        // AN EMPTY LIST CLEARS IT, deliberately: finishing the work and saying so is a legitimate
        // thing to want, and the alternative is a stale plan sitting in the prompt for the rest of
        // the session.
        list.Replace(items);
        return list.Describe();
    }
}

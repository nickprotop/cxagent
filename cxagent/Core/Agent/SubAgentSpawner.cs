using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Agent;

/// <summary>
/// The <c>spawn_agent</c> tool: builds a child, runs it to completion, and returns its answer.
///
/// <para>FOREGROUND AND BLOCKING in step 1 (D10). The parent waits, exactly as it waits for
/// <c>run_shell</c>. Background is a different tool — it needs a registry, a notification route and a
/// lifetime rule — and pretending this one is asynchronous would mean building all three now.</para>
/// </summary>
public sealed class SubAgentSpawner : ISubAgentSpawner
{
    private readonly SubAgentFactory _factory;

    public SubAgentSpawner(SubAgentFactory factory) => _factory = factory;

    public string ToolName => "spawn_agent";

    /// <summary>
    /// WHERE ALL SPAWN GUIDANCE LIVES (D25) — not in the system prompt.
    ///
    /// <para>A description is read at the moment of choosing. Putting this in the system prompt would
    /// spend prefix on every turn of every session, including the ones that never spawn, to describe
    /// a capability the model can already see in its tool list.</para>
    ///
    /// <para>MOST OF IT IS WHEN NOT TO, following opencode. That is the part that stops a model
    /// delegating work it should simply do: a child begins with no knowledge of this conversation, so
    /// a task the parent could finish in two calls costs a full briefing and a full run instead.</para>
    /// </summary>
    private const string Description =
        """
        Run a prompt in a separate agent that has its own context, and get back what it found.

        Use it when finding the answer would fill this conversation with material you do not need to
        keep — searching a large codebase for where something is done, reading through many files to
        answer one question, or any open-ended hunt whose intermediate steps are noise once it is
        over.

        Do NOT use it when you already know what to read. A known file, a known symbol, or anything
        two or three tool calls away is faster and more reliable done yourself — a sub-agent starts
        with no knowledge of this conversation, so a task you could finish now costs a full briefing
        and a full run.

        It cannot ask you anything. It runs once, with only what you write in the prompt, and returns
        one message. Say in the prompt exactly what you want back, and what "done" means.

        Its work is NOT shown to the user — they see only a status row. Anything from its answer that
        they need must appear in your reply.

        It cannot spawn sub-agents of its own.
        """;

    /// <summary>
    /// HAND-BUILT, AND THAT IS NORMAL HERE. <c>McpToolset.Definitions()</c> constructs
    /// <see cref="ToolDefinition"/>s directly too. <c>WorkerToolset.BuildDefinition</c> exists to stop
    /// a tool's advertised params drifting from a plugin's <c>JobSchema</c>; a spawn tool has neither,
    /// so the doctrine does not apply and looking for a generator would be looking for something that
    /// cannot exist.
    /// </summary>
    public ToolDefinition Definition => new(ToolName, Description,
        JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "description": {
                  "type": "string",
                  "description": "3-5 words naming the task, for the status row the user sees."
                },
                "prompt": {
                  "type": "string",
                  "description": "What the agent should do, and exactly what it should return. It has none of this conversation's context, so include everything it needs."
                }
              },
              "required": ["description", "prompt"]
            }
            """).RootElement);

    public async Task<string?> TryInvokeAsync(ToolCall call, Action<SubAgent>? onChild, CancellationToken ct)
    {
        if (!string.Equals(call.Name, ToolName, StringComparison.Ordinal)) return null;

        var prompt = Read(call, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return "error: 'prompt' is required — say what the agent should do and what to return.";

        // THE BRIEFING IS THE PARENT'S DESCRIPTION of the task (D9). Step 1 has one child type, so
        // there is no configured briefing to outrank it; step 2 introduces types and the precedence
        // that goes with them.
        var child = _factory.Create(briefing: Read(call, "description"));
        onChild?.Invoke(child);

        var result = await child.Agent.SendAsync(prompt, ct);
        return SubAgentEnvelope.Render(child.Agent.Id, result.Outcome, result.Text);
    }

    private static string? Read(ToolCall call, string name) =>
        call.Arguments.ValueKind == JsonValueKind.Object
        && call.Arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

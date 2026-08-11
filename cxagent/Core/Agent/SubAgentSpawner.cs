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
    private readonly AgentTypeCatalog _types;

    /// <param name="types">
    /// The catalog a `type` argument resolves against. Never empty — it always holds at least
    /// `general` — so an error can always name something valid.
    /// </param>
    public SubAgentSpawner(SubAgentFactory factory, AgentTypeCatalog? types = null)
    {
        _factory = factory;
        _types = types ?? new AgentTypeCatalog(new Dictionary<string, Llm.AgentTypeConfig>(), null);
    }

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

        Reach for it when answering would mean reading across several files — delegate it and you keep
        the conclusion, not the file dumps. Searching a large codebase for where something is done,
        reading through many files to answer one question, any open-ended hunt whose intermediate
        steps are noise once it is over.

        For a single-fact lookup where you already know the file, symbol or value, search directly.
        A sub-agent starts with none of this conversation, so a task you could finish yourself costs
        a full briefing and a full run to arrive at what you already had.

        Once you have delegated something, do not also do it yourself — wait for the result. Doing
        both pays twice and leaves you two answers to reconcile.

        It cannot ask you anything. It runs once, with only what you write in the prompt, and returns
        one message. Say in the prompt exactly what you want back, and what "done" means.

        Put the TASK in prompt, and what you already KNOW in context. Anything that would otherwise be
        rediscovered, or got wrong — a file that is currently broken, an approach already tried and
        failed, where the thing actually lives, a convention this repo follows — belongs in context.
        It stays with the agent for its whole run; the prompt does not survive a long one.

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
                  "description": "3-5 words naming the task, for the status row the user sees. Not sent to the agent."
                },
                "prompt": {
                  "type": "string",
                  "description": "What the agent should DO, and exactly what it should return. This is its task."
                },
                "context": {
                  "type": "string",
                  "description": "Optional. What you already know that it would otherwise have to rediscover, or would get wrong: a file that is currently broken, an approach already tried and failed, a convention this repo follows. Facts, not instructions."
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

        // THREE CHANNELS, AND description IS NOT ONE OF THEM (D9).
        //
        // `description` is a UI LABEL — 3-5 words for the status row and the permission prompt. It
        // used to be passed as the briefing, which put "Analyze TextWrapping failures" into the
        // highest-authority position in the child's system message under the heading "this is what
        // you were created to do; where it disagrees with anything above, follow this". Harmless
        // because it is contentless, and structurally the wrong thing in the wrong slot.
        //
        // `briefing` STAYS EMPTY until step 2 supplies config types. It is the one channel that
        // outranks everything else in the prompt, and the only legitimate author of it is a human
        // writing config — letting the parent model fill it would rank generated text above the
        // config that does not exist yet.
        //
        // `context` is the parent's channel: facts the child cannot discover, in the system message
        // so they survive compaction, below the briefing so they carry no authority.
        // AN UNKNOWN TYPE IS REFUSED, NOT SILENTLY DEFAULTED. The model will invent "researcher".
        // Substituting `general` means the user's briefing did not apply and nobody was told — the
        // same class of silent-wrong as a mode that quietly stays single. A blank or absent name IS
        // `general`, which is what makes a bare spawn ordinary rather than special.
        var requested = Read(call, "type");
        var type = _types.Resolve(requested);
        if (type is null)
            return $"error: unknown agent type '{requested?.Trim()}'. Valid: {_types.Names}.";

        var child = _factory.Create(
            // THE BRIEFING COMES FROM THE TYPE, never from the parent (D9). Config is the only
            // legitimate author of the highest-authority text in a child's prompt.
            briefing: null,
            callerContext: Read(call, "context"),
            // The label the USER sees — status row and permission prompts. Never sent to the model.
            label: Read(call, "description"),
            type: type);
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

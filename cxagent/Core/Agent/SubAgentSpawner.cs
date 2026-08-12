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

        Once you have delegated something, do not also do it yourself — wait for the results. Doing
        both pays twice and leaves you two answers to reconcile.

        LAUNCH SEVERAL AT ONCE when the work is independent: put every call in ONE message and they
        run concurrently, finishing in the time the slowest one takes. Two searches of different
        parts of the repository are independent. A search and then a fix that depends on what the
        search found are not — those are two turns, not two agents.

        Give them non-overlapping work. Two agents told to edit the same file will both edit it, and
        neither will know the other did.

        An agent cannot ask you anything. It runs with only what you write in its prompt and returns
        one message when it is done. Say in that prompt exactly what you want back, and what "done"
        means.

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
    /// <summary>
    /// The description the model reads, with the type catalog appended.
    ///
    /// <para>GENERATED, because a model cannot pick from a catalog it has never seen (D5) and the
    /// catalog is per-config. The prose above it is a constant and is NOT rewritten here: it was
    /// tuned across three live drives and the wording is load-bearing — the when-not-to, the benefit,
    /// and "do not also do it yourself". Types are an addition to that text.</para>
    ///
    /// <para>THE PROMPT-CACHE PREFIX NOW DEPENDS ON CONFIG. The description is part of the tool
    /// schema, which is part of every request. Config is stable within a session, so the prefix is
    /// stable within a session — the same guarantee everything else here has.</para>
    /// </summary>
    private string DescriptionWithTypes()
    {
        var sb = new System.Text.StringBuilder(Description);

        sb.AppendLine();
        sb.AppendLine();
        // SAY IT IS OPTIONAL AND WHAT OMITTING IT MEANS. A model that suddenly sees a catalog may
        // infer it MUST choose, and choose badly on the tasks where `general` was right — turning a
        // helpful list into a forced decision. This is the `context` failure from the other side: a
        // parameter whose purpose is unstated is either ignored or misused, never used well.
        sb.AppendLine("Agent types. Omit `type` for a general-purpose agent; name one when it fits "
                    + "what you need done.");

        foreach (var type in _types.All)
        {
            // ONE LINE EACH. A catalog that dwarfs the guidance above it buries the guidance, and
            // that guidance is already hard enough for a model to act on.
            var what = string.IsNullOrWhiteSpace(type.Briefing)
                ? "same model as you, no special instructions"
                : Summarise(type.Briefing);
            sb.AppendLine($"- {type.Name}: {what}");
        }

        return sb.ToString();
    }

    /// <summary>First sentence of a briefing, bounded. A briefing is written for the CHILD and can run
    /// long; what the parent needs is enough to choose by.</summary>
    private static string Summarise(string briefing)
    {
        var text = briefing.ReplaceLineEndings(" ").Trim();
        var stop = text.IndexOf(". ", StringComparison.Ordinal);
        if (stop > 0) text = text[..(stop + 1)];
        return text.Length <= 140 ? text : text[..140].TrimEnd() + "…";
    }

    public ToolDefinition Definition => new(ToolName, DescriptionWithTypes(),
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
                },
                "type": {
                  "type": "string",
                  "description": "Optional. Which agent type to use — see the list in this tool's description. Omit for a general-purpose agent."
                }
              },
              "required": ["description", "prompt"]
            }
            """).RootElement);

    public async Task<string?> TryInvokeAsync(ToolCall call, Action<SubAgent>? onChild,
        CancellationToken ct, string? parentAgentId = null)
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
            type: type,
            // So the child's logs land UNDER this agent's directory rather than beside it.
            parentAgentId: parentAgentId);
        onChild?.Invoke(child);

        // A BUDGET ALREADY BREACHED REFUSES THE CHILD RATHER THAN RUNNING IT.
        //
        // `Breached` is a warning that fires exactly once and stops nothing. With one child at a
        // time that was tolerable — the user sees it and can press Escape. With several, N children
        // can each burn a window past a limit that announced itself one time, and the announcement
        // is long gone by the third.
        //
        // ESTIMATE ZERO, deliberately: refuse only what is ALREADY over, never what might go over.
        // We cannot know what a child will cost, and guessing would refuse work the user's budget
        // could afford. This is a floor, not a forecast.
        //
        // Refused as an ENVELOPE, not an exception: the parent reads it, knows why, and can say so —
        // the same contract every other spawn failure has.
        if (_factory.Ledger.WouldBreach(0))
            return SubAgentEnvelope.Render(child.Agent.Id, SendOutcome.Capped,
                "not started: the session's token budget is already spent. Raise "
              + "orchestrator.goalTokenBudget or start a new session.");

        // THE CAP, WAITED HERE — inside the started task, never on the parent's walk. Waiting on the
        // walk would stall the turn's INLINE tools behind a queued child, turning a limit on
        // concurrency into a serialiser for work that was never capped.
        //
        // Null when unconfigured, which is the default: whatever the model emits, runs.
        var slot = _factory.ConcurrencySlot;
        if (slot is not null) await slot.WaitAsync(ct);
        try
        {
            var result = await child.Agent.SendAsync(prompt, ct);
            return SubAgentEnvelope.Render(child.Agent.Id, result.Outcome, result.Text);
        }
        finally
        {
            slot?.Release();
        }
    }

    private static string? Read(ToolCall call, string name) =>
        call.Arguments.ValueKind == JsonValueKind.Object
        && call.Arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

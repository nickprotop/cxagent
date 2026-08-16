using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Agent;

/// <summary>
/// The <c>task</c> tool: builds a child, runs it to completion, and returns its answer.
///
/// <para>FOREGROUND AND BLOCKING in step 1 (D10). The parent waits, exactly as it waits for
/// <c>run_shell</c>. Background is a different tool — it needs a registry, a notification route and a
/// lifetime rule — and pretending this one is asynchronous would mean building all three now.</para>
/// </summary>
public sealed class SubAgentSpawner : ISubAgentSpawner
{
    private readonly SubAgentFactory _factory;
    private readonly AgentTypeCatalog _types;

    /// <summary>Forwarded to the factory, which holds the default every child inherits.</summary>
    public void SwapDefaultProvider(ILlmProvider provider, int? contextWindow, string? instanceName) =>
        _factory.SwapDefaultProvider(provider, contextWindow, instanceName);

    /// <param name="types">
    /// The catalog a `type` argument resolves against. Never empty — it always holds at least
    /// `general` — so an error can always name something valid.
    /// </param>
    public SubAgentSpawner(SubAgentFactory factory, AgentTypeCatalog? types = null)
    {
        _factory = factory;
        _types = types ?? new AgentTypeCatalog(new Dictionary<string, Llm.AgentTypeConfig>(), null);
    }

    public string ToolName => "task";

    /// <summary>
    /// A NAME THIS TOOL ALSO ANSWERS TO — the name this tool carried before it was renamed to match opencode and Claude Code.
    ///
    /// <para>ACCEPTED, NOT ADVERTISED. Only <see cref="ToolName"/> is sent to the model, so nothing
    /// pulls it toward the old spelling. But a rename is invisible to a model working from habit or
    /// from a resumed conversation whose earlier turns used the old name, and an unknown tool is a
    /// hard failure that costs a turn to recover from — for no reason, since the call is
    /// unambiguous. Accepting it costs one comparison.</para>
    /// </summary>
    private const string LegacyName = "spawn_agent";

    /// <summary>Is this call for this tool, under either name?</summary>
    private bool Claims(string name) =>
        string.Equals(name, ToolName, StringComparison.Ordinal)
        || string.Equals(name, LegacyName, StringComparison.Ordinal);

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
            // WHAT THE PARENT NEEDS TO CHOOSE BY, WRITTEN FOR IT. This was Summarise(briefing) —
            // the first sentence of text addressed to the CHILD — which produced lines like "You
            // search and report.": grammatically about the agent, and useless to anyone deciding
            // whether to reach for it.
            //
            // NO FALLBACK TO THE BRIEFING. A type with nothing configured already had an honest
            // line, and it is the better answer: it says nothing was said, which is true, rather
            // than saying something misleading with confidence. Keeping the derivation as a fallback
            // would have preserved exactly the output this change removes, on a path the shipped
            // config never takes and nobody would test.
            //
            // THE ONE-LINE RULE WAS NEVER "BE TERSE" — it was "do not drown the guidance above".
            // Measured: the five shipped descriptions total ~1,620 characters against this tool's
            // ~2,150 of delegation advice (0.75x). Full briefings would be 4,558, or 2.3x, which is
            // what the rule existed to prevent.
            var what = type.Description is { Length: > 0 } described
                ? described
                : "runs where you do, no special instructions";

            // WHERE IT RUNS, ONLY WHEN THAT IS ELSEWHERE. A type bound to another instance is often
            // bound to it FOR A REASON — a bigger window, a stronger model, a cheaper one — and that
            // is a fact worth choosing by. Naming the instance rather than the model is the point:
            // two instances can serve one model, so the model alone would report the routing as no
            // routing at all. Silent for the common type, which runs where the parent does and has
            // nothing to say.
            var where = type.InstanceName is { Length: > 0 } instance ? $" [runs on {instance}]" : "";

            sb.AppendLine($"- {type.Name}: {what}{where}");
        }

        return sb.ToString();
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
        if (!Claims(call.Name)) return null;

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

        string? planPath = null;
        var child = _factory.Create(
            // THE BRIEFING COMES FROM THE TYPE, never from the parent (D9). Config is the only
            // legitimate author of the highest-authority text in a child's prompt.
            briefing: null,
            callerContext: WithPlanPath(Read(call, "context"), type, Read(call, "description"),
                out planPath),
            // The label the USER sees — status row and permission prompts. Never sent to the model.
            label: Read(call, "description"),
            type: type,
            // So the child's logs land UNDER this agent's directory rather than beside it.
            parentAgentId: parentAgentId);
        onChild?.Invoke(child);

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
            return SubAgentEnvelope.Render(child.Agent.Id, result.Outcome,
                WithPlanOutcome(result.Text, planPath));
        }
        finally
        {
            slot?.Release();
        }
    }

    /// <summary>
    /// Names the file a plan-writing type must write, and tells the child where it is.
    ///
    /// <para>THE PARENT NO LONGER HAS TO TRUST A CLAIM. The planner briefing asks for a
    /// <c>PLAN WRITTEN: &lt;path&gt;</c> line, and a claim is all that was — measured twice on live
    /// drives, a planner that never called write_file ended with the marker anyway, once after
    /// announcing it would "proceed by making some assumptions". The parent believed it both times.
    /// Naming the path HERE means the result can report what is actually on disk instead.</para>
    ///
    /// <para>DERIVED FROM THE SPAWN'S OWN LABEL so the file says what it is, and stable within a
    /// call so the check below looks where the child was told to write. Only for types whose
    /// briefing asks for a plan — every other type's context is untouched.</para>
    /// </summary>
    /// <summary>
    /// Which types write a plan file — declared by the type, not inferred from its prose.
    ///
    /// <para>This read <c>Briefing.Contains("PLAN WRITTEN:")</c>, which coupled a mechanism to a
    /// sentence: the builder's briefing MENTIONED that marker (to say what it refuses), so it was
    /// one edit away from being handed a plan path it never writes, and retiring the marker from the
    /// planner's text would have silently switched the whole thing off.</para>
    /// </summary>
    public static bool WritesAPlan(AgentType type) => type.WritesAPlanFile;

    /// <summary>
    /// A path per spawn, derived from the label the parent already wrote.
    ///
    /// <para>NOT A FIXED NAME: two planners in one session would overwrite each other, and the
    /// second one's parent would read the first one's plan without either noticing. The label is
    /// slugged rather than hashed so the file is still recognisable in ./plans/ afterwards.</para>
    /// </summary>
    private static string PlanPathFor(string? label)
    {
        var slug = new string((label ?? "plan")
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray())
            .Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        if (slug.Length == 0) slug = "plan";
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        return $"plans/{slug}.md";
    }

    private static string? WithPlanPath(string? context, AgentType type, string? label,
        out string? planPath)
    {
        planPath = null;
        if (!WritesAPlan(type)) return context;
        planPath = PlanPathFor(label);

        var line = $"Write your plan file to `{planPath}` — that exact path, relative to the working "
                 + "directory. The parent reads that file; it does not read this answer for the plan.";
        return string.IsNullOrWhiteSpace(context) ? line : context + "\n\n" + line;
    }

    /// <summary>
    /// Appends what is actually on disk at the path the child was given.
    ///
    /// <para>REPLACES A MARKER THE CHILD CONTROLS with a fact the spawner checked. Says so either
    /// way: a parent told nothing about a missing plan writes one itself from the chat text — which
    /// is exactly what happened, and the builder it fed then went looking for Rust sources in a C#
    /// repository.</para>
    /// </summary>
    private string WithPlanOutcome(string text, string? planPath)
    {
        if (planPath is null) return text;

        var full = _factory.WorkingDir is { Length: > 0 } root
            ? Path.Combine(root, planPath) : planPath;

        bool written;
        try { written = File.Exists(full) && new FileInfo(full).Length > 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException) { written = false; }

        return written
            ? text + $"\n\n[cxagent] plan file: {planPath}"
            : text + $"\n\n[cxagent] NO PLAN FILE was written at {planPath}. There is no plan. Do "
                   + "not build from the text above, and do not write a plan yourself from it — send "
                   + "the planner again, telling it the previous run wrote nothing.";
    }

    private static string? Read(ToolCall call, string name) =>
        call.Arguments.ValueKind == JsonValueKind.Object
        && call.Arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

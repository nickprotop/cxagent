using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Agents;

/// <summary>
/// The <c>agent</c> tool: builds a child, and either starts it in the background and returns a receipt,
/// or runs it to completion and returns its answer.
///
/// <para>BACKGROUND WHERE IT CAN WORK, which needs a store to keep the child reachable, a delivery port and
/// a parent to report to, and a front end that allows it — see the branch in
/// <see cref="TryInvokeAsync"/>. Everywhere else, and when the model passes <c>background: false</c>, the
/// parent waits, exactly as it waits for <c>run_shell</c>.</para>
/// </summary>
internal sealed class SubAgentSpawner : ISubAgentSpawner
{
    private readonly SubAgentFactory _factory;
    private volatile AgentTypeCatalog _types;
    private ToolDefinition? _definition;

    /// <summary>Forwarded to the factory, which holds the default every child inherits.</summary>
    public void SwapDefaultProvider(ILlmProvider provider, int? contextWindow, string? instanceName) =>
        _factory.SwapDefaultProvider(provider, contextWindow, instanceName);

    /// <param name="types">
    /// The catalog a `type` argument resolves against. Never empty — it always holds at least
    /// `general` — so an error can always name something valid.
    /// </param>
    /// <param name="factory">Builds each child, carrying what every child shares.</param>
    /// <param name="store">
    /// Where finished children are kept so the model can ask them more. Null keeps none, which is
    /// what every caller with no interest in reaching a child again gets by default.
    /// </param>
    public SubAgentSpawner(SubAgentFactory factory, AgentTypeCatalog? types = null,
        SubAgentStore? store = null)
    {
        _factory = factory;
        _types = types ?? new AgentTypeCatalog(new Dictionary<string, Llm.AgentTypeConfig>(), null);
        // OPTIONAL, so every existing construction site keeps working and a caller with no interest
        // in reaching children again — a test, a headless run — spawns exactly as it did.
        _store = store;
    }

    /// <summary>Where finished children are kept, so the model can ask them more. Null means none are.</summary>
    private readonly SubAgentStore? _store;

    /// <inheritdoc />
    public SubAgentStore? Store => _store;

    /// <inheritdoc />
    public SemaphoreSlim? ConcurrencySlot => _factory.ConcurrencySlot;

    public string ToolName => "agent";

    /// <summary>
    /// Whether this tool answers to the name a call used.
    ///
    /// <para>NO LEGACY NAME. This accepted its pre-rename spelling for six days after 2026-08-13, so
    /// a conversation resumed across the change would not fail on a name it had seen in its own
    /// history. That window is closed. Only cheap because nothing is published — a rename after
    /// release needs the acceptance window again.</para>
    /// </summary>

    /// <summary>Is this call for this tool? One name, no aliases since 2026-08-19.</summary>
    private bool Claims(string name) =>
        string.Equals(name, ToolName, StringComparison.Ordinal);

    /// <summary>
    /// WHERE ALL SPAWN GUIDANCE LIVES (D25) — not in the system prompt.
    ///
    /// <para>A description is read at the moment of choosing. Putting this in the system prompt would
    /// spend prefix on every turn of every session, including the ones that never spawn, to describe
    /// a capability the model can already see in its tool list.</para>
    ///
    /// <para>MOST OF IT IS WHEN NOT TO. That is the part that stops a model delegating work it
    /// should simply do: a child begins with no knowledge of this conversation, so a task the parent
    /// could finish in two calls costs a full briefing and a full run instead.</para>
    /// </summary>
    private const string Description =
        """
        Run a prompt in a separate agent that has its own context, and get back what it found.

        This STARTS A NEW ONE, which knows nothing. If an agent you already spawned has done work the
        request builds on — redo it differently, go deeper, check what it told you — send to THAT one
        with agent_send instead. "Use the agent again", "have it also do X", "tell it to keep going"
        are all sends, not spawns.

        Reach for it when answering would mean reading across several files — delegate it and you keep
        the conclusion, not the file dumps. Searching a large codebase for where something is done,
        reading through many files to answer one question, any open-ended hunt whose intermediate
        steps are noise once it is over.

        For a single-fact lookup where you already know the file, symbol or value, search directly.
        A sub-agent starts with none of this conversation, so a task you could finish yourself costs
        a full briefing and a full run to arrive at what you already had.

        UNLESS THE USER ASKED FOR ONE. If they named a type or said to spawn an agent, that settles
        it — send it, however small the task looks to you. Say it is more than the work needs if you
        think so; do not quietly do the work yourself instead.

        Once you have delegated something, do not also do it yourself — wait for the results. Doing
        both pays twice and leaves you two answers to reconcile.

        LAUNCH SEVERAL AT ONCE when the work is independent: put every call in ONE message and they
        run concurrently, finishing in the time the slowest one takes. Two searches of different
        parts of the repository are independent. A search and then a fix that depends on what the
        search found are not — those are two turns, not two agents.

        A PLANNER AND THE BUILDER THAT CARRIES OUT ITS PLAN ARE TWO STEPS. The builder needs a plan that
        does not exist while the planner is still running. Spawn the planner with background: false — or
        spawn it in the background and wait for its report — then send the builder to the file it names.
        Give it the PATH, not your own account of the plan: a specification you write into the builder's
        prompt is the planner's run spent and discarded, and it is your transcription the builder then
        follows rather than the plan — measured, a parent that wrote its own got three types wrong and the
        builder implemented all three.

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

        {{WAITING}}

        A sub-agent you spawned is still there, by name, with everything it read and did. When more is
        wanted from work an agent has ALREADY done — a correction, a deeper pass, a check on what it
        told you — use agent_send on that agent. Spawning a second one over the same ground starts
        from nothing: it has not read what the first read and cannot see what the first concluded.
        task_list shows which ones there are; you will not remember them once the spawn falls out of
        context.
        """;

    /// <summary>
    /// How a spawn waits, for a spawner that can run one in the background.
    ///
    /// <para>ONE OF TWO PARAGRAPHS, CHOSEN BY WHAT THIS SPAWNER CAN DO: a headless run, or a caller with
    /// no delivery port, spawns in the foreground whatever the model asks, and telling it a call returns
    /// at once would have it end its turn before the answer it is about to be handed.</para>
    /// </summary>
    private const string WaitingInTheBackground =
        $"""
        IT RUNS IN THE BACKGROUND by default: the call returns at once, you carry on, and its report comes
        to you as a message when it is done — in this turn if you are still working, as a new turn if you
        are not. Do not poll for it and do not do its work yourself. {AskedForBackground} Otherwise, pass
        background: false only when this turn can do nothing else until its answer arrives; then the call
        waits and its report IS the result. If you backgrounded it and then find you need it now,
        task_output with wait: true waits for it.
        """;

    /// <summary>
    /// The rule that the user's ask for a background agent settles the flag — one sentence, read in the
    /// system prompt, this tool's description and the flag's own.
    ///
    /// <para>STATED WHERE THE FLAG IS CHOSEN, because the rule the model already had ("false when your next
    /// step needs the answer") is a judgement about the task, and a model that judges an explore request to
    /// be such a step blocks the parent's turn — measured live, on "explore … with an background agent",
    /// with the user's next message left waiting behind it. The user's words are the one input that judgement
    /// must not override.</para>
    ///
    /// <para>THE WAIT IS NAMED TOO, because it is the same block by another route: a model told only "never
    /// pass false" spawns with true and then calls task_output wait: true at once, and the user's turn
    /// waits exactly as it would have. Saying what to do instead — end the turn or carry on — matters as
    /// much as the ban: a model that knows only what not to do reaches for the wait the receipt offers.</para>
    ///
    /// <para>ONE CONSTANT for the three places, so they cannot come to say different things.</para>
    /// </summary>
    internal const string AskedForBackground =
        "When the user asks for a background agent, or to keep working or talking while it runs, omit "
        + "background (never pass background: false), and do not follow the spawn with task_output wait: "
        + "true — that blocks the user's turn exactly as false does. End your turn or carry on with other "
        + "work; its report comes to you as a message.";

    /// <summary>How a spawn waits, for a spawner that runs every spawn in the foreground.</summary>
    private const string WaitingInTheForeground =
        """
        Spawning waits for the answer: the call returns when the agent is done, and its report IS the
        result. Several spawned in one turn run at the same time and report together.
        """;

    /// <summary>Whether a spawn may run in the background here — the same test the spawn itself makes.</summary>
    public bool CanBackground => _factory.AllowBackground && _factory.Delivery is not null && _store is not null;

    /// <summary>
    /// HAND-BUILT, AND THAT IS NORMAL HERE. <c>McpToolset.Definitions()</c> constructs
    /// <see cref="ToolDefinition"/>s directly too. <c>ToolBindings.BuildDefinition</c> exists to stop
    /// a tool's advertised params drifting from an executor's <c>JobSchema</c>; a spawn tool has neither,
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
        var sb = new System.Text.StringBuilder(Description.Replace("{{WAITING}}",
            (CanBackground ? WaitingInTheBackground : WaitingInTheForeground).TrimEnd(), StringComparison.Ordinal));

        sb.AppendLine();
        sb.AppendLine();
        // "WHICH SETTLES IT" IS THE OTHER HALF OF THE SAME PROBLEM. Everything else here — and every
        // spawn rule in the system prompt — keys on the MODEL's judgement of the task. Measured on a
        // live drive: asked to "spawn a planner, then spawn a builder to carry out that plan", the
        // parent spawned the planner, read its report ("just one switch case and one help-text
        // line"), and did the building itself. One `agent` call in the whole session, no refusal, the
        // tool still offered — it simply judged the task too small, exactly as the paragraph above
        // tells it to.
        //
        // THE MODEL OBEYED THE DESCRIPTION OVER THE USER, which is a trust failure rather than an
        // efficiency one. It is also the ask_user shape a second time: restraint in the description,
        // restraint in the prompt, nothing anywhere saying a request outranks the sizing judgement.
        //
        // SAY IT IS OPTIONAL AND WHAT OMITTING IT MEANS. A model that suddenly sees a catalog may
        // infer it MUST choose, and choose badly on the tasks where `general` was right — turning a
        // helpful list into a forced decision. This is the `context` failure from the other side: a
        // parameter whose purpose is unstated is either ignored or misused, never used well.
        sb.AppendLine("Agent types. Omit `type` for a general-purpose agent; name one when it fits "
                    + "what you need done — or when the user named one, which settles it.");

        foreach (var type in _types.All)
        {
            // WHAT THE PARENT NEEDS TO CHOOSE BY, WRITTEN FOR IT — never derived from the briefing.
            // Summarising the briefing takes the first sentence of text addressed to the CHILD and
            // yields lines like "You search and report.": grammatically about the agent, and useless
            // to anyone deciding whether to reach for it.
            //
            // NO FALLBACK TO THE BRIEFING EITHER. The honest line below is the better answer for a
            // type with nothing configured: it says nothing was said, which is true, rather than
            // saying something misleading with confidence. A derivation kept as a fallback would
            // produce exactly that misleading output on a path the shipped config never takes and
            // nobody would test.
            //
            // THE ONE-LINE RULE IS NOT "BE TERSE" — it is "do not drown the guidance above".
            // Measured: the five shipped descriptions total ~1,620 characters against this tool's
            // ~2,150 of delegation advice (0.75x). Full briefings would be 4,558, or 2.3x, which is
            // what the rule exists to prevent.
            // "RUNS WHERE YOU DO" ONLY WHEN IT DOES: a type with providers, or a disabled one, does not.
            var what = type.Description is { Length: > 0 } described ? described
                : type.Offers.Count > 0 || type.Disabled is not null ? "no special instructions"
                : "runs where you do, no special instructions";

            // WHERE IT MAY RUN, ONLY WHEN THAT IS SAID. A type's providers are a choice worth making by —
            // a stronger model, a cheaper one — so each comes with its description, and is named as
            // name:model because two entries can serve one model. Silent for a type with no list, which
            // runs where the parent does. A DISABLED type is listed with its reason rather than hidden,
            // so a user who names it gets an answer the model can relay.
            var sep = what.EndsWith('.') ? " " : ". ";
            var where = type.Disabled is { } why ? $" (disabled: {why})"
                : type.Offers.Count == 0 ? ""
                : sep + RunsOn(type.Offers);

            sb.AppendLine($"- {type.Name}: {what}{where}");
        }

        return sb.ToString();
    }


    private static string RunsOn(IReadOnlyList<TypeRouting> offers)
    {
        static string One(TypeRouting o) => o.Description is { Length: > 0 } d ? $"{o.Label} ({d})" : o.Label;
        return offers.Count == 1
            ? $"Runs on {One(offers[0])}."
            : $"Runs on {One(offers[0])} by default; or {string.Join("; or ", offers.Skip(1).Select(One))}.";
    }

    /// <inheritdoc/>
    public void Refresh(int? maxConcurrentAgents, AgentTypeCatalog types)
    {
        _factory.SwapLimit(maxConcurrentAgents);
        _types = types;
    }

    /// <summary>
    /// The spawn tool as the model sees it — BUILT ONCE, at first use. It lists the agent types, and it
    /// is part of the cached prompt prefix: rebuilt from a refreshed catalog it would change that prefix
    /// and re-send the whole conversation. A type added since is announced in the conversation instead,
    /// and resolves by name all the same.
    /// </summary>
    public ToolDefinition Definition => _definition ??= Build();

    /// <summary>
    /// The `background` argument, offered only where it can do something — see
    /// <see cref="CanBackground"/>. A flag the spawn ignores is a promise the schema cannot keep.
    ///
    /// <para>IT OPENS WITH "OMIT IT", because the default is the right answer nearly always, and a flag
    /// whose description opens on what the value means invites the model to choose one — and choosing is
    /// where it picks false.</para>
    /// </summary>
    private const string BackgroundProperty =
        $$"""
        ,
                        "background": {
                          "type": "boolean",
                          "description": "Omit it: by default the agent runs while you carry on, and its report comes to you when it is done. {{AskedForBackground}} Otherwise, pass false only when this turn can do nothing else until its answer arrives."
                        }
        """;

    /// <summary>
    /// The `provider` argument, offered as soon as any type lists a provider — even one, so a config
    /// edit adding a second mid-session rarely meets a schema without it (the schema is built once, for
    /// the prompt cache). With no lists at all there is nothing to choose.
    /// </summary>
    private const string ProviderProperty =
        """
        ,
                        "provider": {
                          "type": "string",
                          "description": "Optional. Where the agent runs: one of its type's providers from this tool's description, as name:model. Omit for the type's default."
                        }
        """;

    private ToolDefinition Build() => new(ToolName, DescriptionWithTypes(),
        JsonDocument.Parse(
            SchemaTemplate.Replace("{{BACKGROUND}}", CanBackground ? BackgroundProperty.Trim() : "",
                    StringComparison.Ordinal)
                .Replace("{{PROVIDER}}", _types.All.Any(t => t.Offers.Count > 0) ? ProviderProperty.Trim() : "",
                    StringComparison.Ordinal)).RootElement);

    /// <summary>The spawn tool's argument schema, with a slot for the background flag.</summary>
    private const string SchemaTemplate =
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
                }{{BACKGROUND}}{{PROVIDER}}
              },
              "required": ["description", "prompt"]
            }
            """;

    /// <summary>A foreground child's call when the user stopped it alone — the turn goes on; the work does not.</summary>
    internal const string StoppedByUser =
        "stopped by the user before it finished. Do not redo its work, and do not start it again: the user ended it.";

    public async Task<string?> TryInvokeAsync(ToolCall call, Action<SubAgent>? onChild,
        CancellationToken ct, string? parentAgentId = null, Jobs.ToolSelection? turnTools = null)
    {
        if (!Claims(call.Name)) return null;

        var prompt = Read(call, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return "error: 'prompt' is required — say what the agent should do and what to return.";

        // THREE CHANNELS, AND description IS NOT ONE OF THEM (D9).
        //
        // `description` is a UI LABEL — 3-5 words for the status row and the permission prompt.
        // Passing it as the briefing puts "Analyze TextWrapping failures" into the highest-authority
        // position in the child's system message, under the heading "this is what you were created
        // to do; where it disagrees with anything above, follow this". Harmless because it is
        // contentless, and structurally the wrong thing in the wrong slot.
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

        // WHERE IT RUNS, FROM THE TYPE'S OWN LIST — config is the only source of a child's provider.
        // Applied by replacing the type's routing, so the factory builds the child exactly as it always
        // has from type.Provider, its window and its instance name.
        var pick = type.Pick(Read(call, "provider"));
        if (pick.Refusal is { } refusal) return "error: " + refusal;
        type = type with { Routing = pick.Routing! };

        string? planPath = null;
        var child = _factory.Create(
            // THE BRIEFING COMES FROM THE TYPE, never from the parent (D9). Config is the only
            // legitimate author of the highest-authority text in a child's prompt.
            briefing: null,
            callerContext: WithPlanPath(Read(call, "context"), type, Read(call, "description"),
                canWrite: ChildCanWrite(turnTools, type), out planPath),
            // The label the USER sees — status row and permission prompts. Never sent to the model.
            label: Read(call, "description"),
            type: type,
            // So the child's logs land UNDER this agent's directory rather than beside it.
            parentAgentId: parentAgentId,
            turnTools: turnTools);
        onChild?.Invoke(child);

        // KEPT THE MOMENT IT EXISTS, NOT WHEN IT FINISHES. A child is reachable — by agent_send,
        // task_output, task_stop — for the whole time it is running, which is exactly when the parent
        // wants to ask how far it has got or tell it something it needs. Kept only on completion, it
        // would be invisible to task_list for the whole of its run.
        var keptAs = _store?.Keep(child, Read(call, "description"));

        // BACKGROUND BY DEFAULT, WHERE IT CAN WORK: a store (or nothing could reach or stop the child), a delivery
        // port and a parent id (or nobody could be told it ended), and a runtime that allows it — headless
        // does not (spec decision 8). Anything else spawns in the foreground, where none of these is needed.
        var background = Args.ReadBool(call, "background") ?? true;
        if (background && keptAs is not null && _factory.AllowBackground
            && _factory.Delivery is { } delivery && parentAgentId is not null)
        {
            // CLAIMED AND QUEUED BEFORE THE RECEIPT RETURNS, so a task_output or task_stop the parent
            // sends straight after finds a run to act on rather than a child that looks idle.
            _store!.TryClaim(keptAs);
            var run = _store.BeginBackground(
                new BackgroundStart(Name: keptAs, ParentAgentId: parentAgentId, Brief: prompt,
                    Description: Read(call, "description"), IsResume: false),
                new Jobs.ResultHandoff(BackgroundRunner.LeaveFor(delivery, parentAgentId, keptAs)));
            _ = BackgroundRunner.Start(new BackgroundWork(_store, _store.Find(keptAs)!, run, prompt,
                _factory.ConcurrencySlot, delivery,
                result => SubAgentEnvelope.Render(child.Agent.Id, keptAs,
                    result with { Text = WithPlanOutcome(result.Text, planPath) }, SubAgentEnvelope.WorkOf(child))));
            return SubAgentEnvelope.Receipt(child.Agent.Id, keptAs, ChildCanWrite(turnTools, type));
        }

        // CLAIMED SILENTLY FOR ITS WHOLE LIFE, ANNOUNCED ONLY WITH THE SLOT IN HAND — the order
        // AgentReachTools keeps for a resume. The claim makes a send while it waits go to its mailbox
        // rather than start a second turn on a context this one will append to; the announcement starts
        // its row's clock and the archive's run, and a child sitting in a queue is doing neither.
        if (keptAs is not null) _store!.TryClaim(keptAs);

        // THE CAP, WAITED HERE — inside the started task, never on the parent's walk. Waiting on the
        // walk would stall the turn's INLINE tools behind a queued child, turning a limit on
        // concurrency into a serialiser for work that was never capped.
        //
        // Null when unconfigured, which is the default: whatever the model emits, runs.
        var slot = _factory.ConcurrencySlot;
        var announced = false;

        // HANDED TO A BACKGROUND RUN, by a move: from then on the run owns the claim, the slot and the child's token,
        // and none of them is released here.
        var adopted = false;

        // THE ENVELOPE, HOISTED SO THE RELEASE CAN READ ITS STATE. The release is what announces the
        // stop, and the listener that writes this run's account runs inside it — so the outcome word
        // has to be in hand before the finally, not after the call returns. Null on every path that
        // threw, where "completed" is not a claim anyone should make.
        string? envelope = null;
        try
        {
            if (slot is not null)
            {
                // QUEUED IS STILL WAITED ON: the hint counts it, and Ctrl+B says it cannot move it yet.
                if (keptAs is not null) _store!.BeginQueued(keptAs);
                try { await slot.WaitAsync(ct); }
                finally { if (keptAs is not null) _store!.EndQueued(keptAs); }
            }
            try
            {
                if (keptAs is not null)
                {
                    announced = true;
                    _store!.AnnounceBegin(keptAs);
                }

                // THE BRIEF IS THE PARENT'S, shown as its note in the child's own transcript.
                child.Agent.EchoPrompt(prompt, fromUser: false);
                child.Agent.History?.NoteIncoming(Incoming.Brief);

                // MAIL AFTER THE BRIEF, as a background spawn reads it: anything sent while this child
                // queued was sent after its brief, and is a correction to the work the brief sets.
                // ITS OWN TOKEN, LINKED TO THE TURN'S (Ctrl+B spec): Escape still ends it, but a stop can end it alone
                // and a move can unlink it. A child nothing kept cannot be reached by either, so it keeps the turn's.
                var foreground = keptAs is null ? null
                    : _store!.BeginForeground(keptAs, ct, movable: _factory.Delivery is not null && parentAgentId is not null);
                try
                {
                    var send = child.Agent.SendAsync(prompt, foreground?.Token ?? ct, mailFirst: false);

                    // THE ANSWER, OR WHATEVER LET IT GO FIRST: a stop answers the call now, and the turn goes on.
                    // AN ANSWER THAT WON THE RACE IS CLAIMED, so a stop or move landing before this call returns is
                    // refused rather than accepted for a child that already finished.
                    var released = foreground is null
                        || (await Task.WhenAny(send, foreground.Released) == send && foreground.TryAnswer())
                        ? (ForegroundRelease?)null
                        : await foreground.Released;

                    // MOVED: THE CHILD GOES ON, THE CALL IS ANSWERED NOW. The background run takes the claim, the slot
                    // and the send; its own stop reaches the child through the foreground token.
                    if (released == ForegroundRelease.Move && _factory.Delivery is { } reports && parentAgentId is not null)
                    {
                        var run = _store!.BeginBackground(new BackgroundStart(Name: keptAs!, ParentAgentId: parentAgentId,
                                Brief: prompt, Description: Read(call, "description"), IsResume: false),
                            new Jobs.ResultHandoff(BackgroundRunner.LeaveFor(reports, parentAgentId, keptAs!)));
                        run.Token.Register(foreground!.Cancel);
                        adopted = true;
                        _ = BackgroundRunner.Adopt(new BackgroundWork(_store, _store.Find(keptAs!)!, run, prompt, slot, reports,
                                result => SubAgentEnvelope.Render(child.Agent.Id, keptAs,
                                    result with { Text = WithPlanOutcome(result.Text, planPath) }, SubAgentEnvelope.WorkOf(child))),
                            send).ContinueWith(_ => foreground.Dispose(), TaskScheduler.Default);
                        return SubAgentEnvelope.Moved(child.Agent.Id, keptAs!, ChildCanWrite(turnTools, type));
                    }

                    if (released == ForegroundRelease.Stop)
                    {
                        try { await send; }
                        catch (OperationCanceledException) { }
                        return envelope = SubAgentEnvelope.Render(child.Agent.Id, keptAs,
                            new SendResult(StoppedByUser, SendOutcome.Cancelled), SubAgentEnvelope.WorkOf(child));
                    }

                    var result = await send;

                    // THE NAME IT WAS KEPT UNDER at spawn — a lookup, not a second Keep, which would
                    // re-register the same child and mint a second handle for it. It goes into the envelope
                    // so the model can reach the child without a listing call.
                    return envelope = SubAgentEnvelope.Render(child.Agent.Id, keptAs,
                        result with { Text = WithPlanOutcome(result.Text, planPath) }, SubAgentEnvelope.WorkOf(child));
                }
                finally
                {
                    if (foreground is not null)
                    {
                        _store!.EndForeground(keptAs!);
                        if (!adopted) foreground.Dispose();
                    }
                }
            }
            finally
            {
                if (!adopted) slot?.Release();
            }
        }
        finally
        {
            // RELEASED WHATEVER HAPPENED, or a child that threw — or was cancelled while queued — stays
            // claimed forever and every later send to it is answered with a delivery confirmation nobody
            // drains. THROUGH WHICHEVER CALL MATCHES THE ANNOUNCEMENT: an announced stop is an archived
            // run, and a run that never started must not archive one of zero turns that /stats averages.
            //
            // THE STATE READ BACK OUT OF THE ENVELOPE rather than passed from `result`: the envelope is
            // the one artefact that always carries it, so the word recorded is the word the parent's
            // model reads — two copies could disagree, and both would be plausible strings.
            if (keptAs is not null && !adopted)
            {
                if (announced) _store!.EndSend(keptAs, SubAgentEnvelope.StateOf(envelope));
                else _store!.Release(keptAs);
            }
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
    ///
    /// <para>AND A TAKEN NAME GETS A SUFFIX, because slugging the label only separates planners the
    /// parent chose to label differently. Two planners spawned with one label — which is what a
    /// parent writes when it splits one job in two — slug identically, and then the pair the
    /// paragraph above exists to keep apart overwrite each other after all: both are told to write
    /// the same file, both do, and the survivor is whichever finished last. The loser's run is
    /// spent and its plan is gone with no error anywhere.</para>
    /// </summary>
    private string PlanPathFor(string? label)
    {
        var slug = new string((label ?? "plan")
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray())
            .Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        if (slug.Length == 0) slug = "plan";
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');

        // CLAIMED BY EXISTENCE, not by a counter: the spawner holds no list of paths it has handed
        // out, and a planner still running has already created its file. Bounded because an
        // unbounded search over a directory nobody is cleaning would hang the spawn; past the
        // ceiling the collision is accepted rather than the spawn refused, since a lost plan is a
        // smaller failure than a parent that cannot delegate at all.
        var candidate = $"plans/{slug}.md";
        for (var n = 2; n <= 20 && PlanFileExists(candidate); n++)
            candidate = $"plans/{slug}-{n}.md";
        return candidate;
    }

    /// <summary>Whether something is already at this path, resolved the same way the outcome check
    /// resolves it — a relative path means nothing without the working directory.</summary>
    private bool PlanFileExists(string relative)
    {
        var full = _factory.WorkingDir is { Length: > 0 } root
            ? Path.Combine(root, relative) : relative;
        try { return File.Exists(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>
    /// A markdown file written into the working directory root during this run that is not where a
    /// plan was asked for — the planner's own choice of name.
    ///
    /// <para>THE ROOT ONLY, AND RECENT ONLY. A repository is full of markdown; what distinguishes a
    /// stray plan is that it appeared while this child ran, at the top level, where the names
    /// planners reach for live. Searching deeper would start proposing READMEs. Nothing is read
    /// here — the parent is handed a path and decides for itself.</para>
    /// </summary>
    private string? StrayPlan()
    {
        var root = _factory.WorkingDir;
        if (string.IsNullOrWhiteSpace(root)) return null;

        try
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-15);
            return new DirectoryInfo(root).GetFiles("*.md", SearchOption.TopDirectoryOnly)
                .Where(f => f.LastWriteTimeUtc >= cutoff && f.Length > 0)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.Name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// Whether a child under this selection would be offered a write tool.
    ///
    /// <para>A SYNTHETIC APPLY against a two-element set: the child does not exist yet, and Apply
    /// only ever filters what it is handed, so asking about two names is sound. The composition
    /// mirrors SubAgentFactory.Create — session, then turn, then type — because a difference between
    /// them would be a mechanism disagreeing with the thing it predicts.</para>
    /// </summary>
    private bool ChildCanWrite(Jobs.ToolSelection? turnTools, AgentType? type)
    {
        var selection = Jobs.ToolSelection.Then(
            Jobs.ToolSelection.Then(_factory.SessionToolSelection, turnTools), type?.Tools);
        if (selection is null) return true;

        var writes = new[] { Jobs.Tool.WriteFile, Jobs.Tool.ReplaceInFile }
            .Select(n => new Llm.ToolDefinition(n, n,
                System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" })))
            .ToList();

        return selection.Apply(writes).Count > 0;
    }

    /// <param name="canWrite">
    /// Whether the child will actually be offered a write tool.
    ///
    /// <para>WITHOUT THIS THE MECHANISM CONTRADICTS ITSELF. It tells the child to write a file at an
    /// exact path, then replaces a marker in the answer with what is on disk — so a child that
    /// cannot write is instructed to do the impossible and then reported as having failed at it. The
    /// contradiction is GUARANTEED rather than possible, which is why the path is not handed out at
    /// all rather than handed out with a warning.</para>
    ///
    /// <para>This is one of the mechanisms that fails SILENTLY under a selection: nothing errors, a
    /// plan simply never appears and the parent writes its own from chat text — which happened once
    /// already, and the builder it fed went looking for Rust sources in a C# repository.</para>
    /// </param>
    /// <param name="context">What the parent passed down, which this may append to.</param>
    /// <param name="type">The child's type — only a plan-writing one gets a path.</param>
    /// <param name="label">The child's short name, used to build a readable filename.</param>
    /// <param name="planPath">Where the plan should be written, or null when the child cannot write.</param>
    private string? WithPlanPath(string? context, AgentType type, string? label,
        bool canWrite, out string? planPath)
    {
        planPath = null;
        if (!WritesAPlan(type) || !canWrite) return context;
        planPath = PlanPathFor(label);

        // THE PATH REPEATED, AND THE ALTERNATIVE NAMED AND REFUSED. Told once, politely, two
        // planners in one session both ignored it and wrote /tmp/<repo>/REFACTOR_PLAN.md instead —
        // independently, which makes that filename a prior strong enough to beat an instruction the
        // model did read. So the wrong answer is named: a rule that says what NOT to write is
        // checkable against the path in hand, where "that exact path" only reads as emphasis.
        var line = $"Write your plan file to `{planPath}` — that exact path, relative to the working "
                 + "directory. The parent reads that file; it does not read this answer for the plan."
                 + $"\n\nNOT a name of your own choosing. Not REFACTOR_PLAN.md, not PLAN.md, not the "
                 + $"repository root: `{planPath}` or the plan is lost, however good it is.";
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

        if (written) return text + $"\n\n[cxagent] plan file: {planPath}";

        // A PLAN AT THE WRONG PATH IS STILL A PLAN. Measured: two planners in one session each wrote
        // a real 303-line plan to REFACTOR_PLAN.md at the repository root, and the parent was told
        // "there is no plan" — true of the path it asked for, false about the work. It re-spawned
        // the planner, which made the same choice again. Naming what was found turns a wasted run
        // into a usable one, and the parent still learns the path was ignored.
        if (StrayPlan() is { } stray)
            return text + $"\n\n[cxagent] NO PLAN FILE at {planPath}, but the planner wrote "
                        + $"`{stray}` instead. Read that, and point a builder at that path — the "
                        + "planner ignored the path it was given, so it is not where you asked.";

        return text + $"\n\n[cxagent] NO PLAN FILE was written at {planPath}. There is no plan. Do "
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

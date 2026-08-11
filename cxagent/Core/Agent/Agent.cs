using System.Text;
using CxAgent.Core.Llm;
using CxAgent.Core.Execution;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Agent;

/// <summary>
/// The whole of single-agent mode: one model, its tools, and a turn loop. No plan, no DAG, no
/// consult.
///
/// <para>WHY THIS EXISTS. The plan/drive/consult cycle asks the orchestrator to describe work it
/// cannot yet see. A `file replace` needs the target's exact bytes; those arrive only after a read
/// job FINISHES, and by then the orchestrator is being asked whether the goal is done rather than
/// what to do next. Measured across a long session: it produced a perfect edit — right tabs, right
/// house style, the exact exception asked for — and then had nowhere to put it, so it emitted the
/// edit as prose under an invented `{"action":"edit_file"}` schema and nothing was written. Another
/// drive read twelve files and reported success having changed none. The failure was never the
/// prompt; three wordings were tried. It is that describing an action and taking one were different
/// channels, and only the describing channel was open.</para>
///
/// <para>Here they are the same channel. The model calls <c>read_file</c>, sees the bytes in its own
/// context, and calls <c>replace_in_file</c> with text it is LOOKING AT. Nothing is reconstructed
/// from a digest because nothing round-trips through one.</para>
///
/// <para>PERMISSIONS ARE UNCHANGED, and that is structural rather than careful: every call goes
/// through <see cref="WorkerToolset.InvokeAsync"/> into the same <see cref="PluginRegistry"/>, whose
/// file/shell/http plugins are wrapped in <c>PermissionGatedPlugin</c>. The gate reads
/// <c>(TypeName, parameters)</c> and nothing else — no part of the job path was load-bearing for it,
/// which is what makes this substitution safe.</para>
///
/// <para>WHAT IS LOST, stated plainly: copilot's whole-plan pre-approval has no plan to approve, and
/// the DAG's parallelism is gone.</para>
/// </summary>
public sealed class Agent
{
    private readonly ILlmProvider _provider;
    private readonly PluginRegistry _plugins;
    private readonly TokenLedger _ledger;
    private readonly IChatSink _sink;
    private readonly IJobPanel _jobs;
    private readonly LogFileManager? _logs;
    private readonly int _maxTurns;

    /// <summary>
    /// This agent's identity, for its whole life. Keys its log directory and its job rows.
    ///
    /// <para>ONE ID, NOT ONE PER PROMPT. A fresh id was minted on every user message, so one linear
    /// session's diagnostics fragmented across directories with turn numbering restarting at 000 in
    /// each — and the session id on screen churned every time the user typed.</para>
    /// </summary>
    public string Id { get; } = Helpers.UlidGenerator.NewId();

    /// <summary>
    /// The last build and test verdicts, and the turn counter — session state, NOT per-prompt state.
    ///
    /// <para>These were locals in the turn loop, so they reset on every user message. A broken build
    /// is not forgotten because the user typed again: the tree is still broken, and the gate that
    /// catches it has to see the verdict that outlived the prompt. <c>_turn</c> is monotonic for the
    /// same reason the id is stable — log turn numbers that restart at 000 on each message make one
    /// session's diagnostics unreadable.</para>
    /// </summary>
    private string? _lastBuild;
    private string? _lastTest;
    private int _turn;

    /// <summary>
    /// The date this agent started, frozen.
    ///
    /// <para>NOT <c>DateTime.Now</c> PER PROMPT. The system message is the prompt-cache prefix, and a
    /// session running past midnight would otherwise rebuild it with a new date and throw away every
    /// cached read for the rest of the conversation. What day it is does not change the work.</para>
    /// </summary>
    private readonly DateOnly _startedOn = DateOnly.FromDateTime(DateTime.Now);

    /// <summary>
    /// Where the user's own instruction file lives, or null when there is none to read.
    ///
    /// <para>opencode reads <c>~/.config/opencode/AGENTS.md</c> alongside the project's. It carries
    /// what is true of the USER wherever they work, which a per-repo file cannot express.</para>
    /// </summary>
    private readonly string? _globalInstructionsDir;

    /// <summary>Connected MCP servers, or null when none are configured — which is the common case
    /// and must cost nothing.</summary>
    private readonly Core.Mcp.McpToolset? _mcp;

    /// <summary>
    /// How this agent spawns children, or NULL — and null is what makes no-nesting structural.
    ///
    /// <para>A field rather than a parameter because <see cref="InvokeAndShowAsync"/> is an instance
    /// method. A child is constructed without one and therefore cannot spawn: not a rule it is asked
    /// to follow, a tool it was never given.</para>
    /// </summary>
    private readonly ISubAgentSpawner? _spawner;

    /// <summary>
    /// Whether this agent is a CHILD, fixed at construction.
    ///
    /// <para>Not derived from <c>_spawner is null</c>, which would be the tempting shortcut and is
    /// wrong: a session with sub-agents disabled also has no spawner, and it would then be told it is
    /// a sub-agent with no user to talk to. The two facts are independent and are stated
    /// independently.</para>
    /// </summary>
    private readonly bool _isSubAgent;

    /// <summary>
    /// How this agent identifies itself when it asks the user for permission — null for the
    /// session's own agent, which needs no attribution because it IS the session.
    ///
    /// <para>Derived from the briefing (what this agent was created to do) rather than from its id: a
    /// prompt saying "01KZQN97XT8DA… wants to run rm -rf" is unanswerable, where "the sub-agent you
    /// asked to analyse the test failure wants to run rm -rf" is a decision someone can make.</para>
    /// </summary>
    private readonly string? _requesterLabel;

    /// <summary>
    /// What THIS agent was created to do, fixed at construction — null for a plain session.
    ///
    /// <para>The seam a caller uses to tell one agent something the others are not told: a
    /// sub-agent's task, a skill's instructions, a role. Constructor-only ON PURPOSE. It becomes part
    /// of the system message, which is the prompt-cache prefix; a mutable briefing would rewrite that
    /// prefix mid-session and throw away every cached token at the moment the conversation is
    /// longest. Fixed at construction, it is byte-identical on every turn for the agent's whole life,
    /// so it costs one prefix and then nothing.</para>
    ///
    /// <para>An agent that needs a DIFFERENT briefing is a different agent. That is not a limitation
    /// — it is the same self-containment rule the context follows.</para>
    /// </summary>
    private readonly string? _briefing;

    /// <summary>
    /// This agent's conversation, for its whole life — the thing that makes it self-contained.
    ///
    /// <para>A field rather than a local inside <see cref="RunCoreAsync"/> because a context that
    /// exists only for the duration of one method cannot be owned by anything: not by a readout that
    /// wants to report real occupancy, not by a <c>/compress</c> that wants a single meaningful
    /// target, and not by an agent that is supposed to carry what it learned into its next task. A
    /// sub-agent gets its own <see cref="Agent"/> and therefore its own context, which is
    /// precisely what the fan-out design assumes it already had.</para>
    /// </summary>
    private readonly AgentContext _context;

    /// <summary>This agent's context — its messages, its occupancy, its window.</summary>
    public AgentContext Context => _context;

    /// <summary>Raised when a turn finishes, with its tool-call count. A callback rather than a
    /// AgentHost reference: the loop needs to ANNOUNCE a turn boundary, not to know what listens.</summary>
    public event Action<int>? TurnCompleted;

    /// <summary>
    /// Raised after every turn with what the provider reported it RECEIVED — the live context size.
    ///
    /// <para>Separate from <see cref="TurnCompleted"/>, which carries only a tool-call count, and that
    /// gap was a real defect: in single-agent mode nothing else observes usage, so the status bar had
    /// no source for occupancy and fell back to the cumulative ledger total — a sum that outgrows any
    /// window and never falls, least of all after the compression this same number triggers.</para>
    /// </summary>
    public event Action<int>? ContextUsed;

    /// <summary>Raised when this loop's own per-turn compression actually shrank the conversation, so
    /// the readout can stop presenting its last measurement as current.</summary>
    public event Action<int, int>? ContextCompressed;

    /// <summary>Raised with a SCALED occupancy figure after compaction — arithmetic, not a
    /// measurement, so the readout marks it approximate until a real reading arrives.</summary>
    public event Action<int>? ContextEstimated;


    /// <summary>
    /// Input tokens past which the loop compresses its own context, or null to never compress.
    ///
    /// <para>THE BOUND THAT REPLACES THE TURN CAP. Single-agent has no turn ceiling by design — a
    /// number of turns has nothing to do with the task — and the comment at the construction site
    /// says the context window ends a session that cannot continue. It did not: AgentHost's
    /// auto-compression sits in a `finally` around the whole GOAL, and a single-agent goal is ONE
    /// RunAsync that loops internally, so the check fired after the run that blew past it. Measured
    /// live at 1.16M input tokens against a 40,000 threshold, never once compressing.</para>
    /// </summary>
    private readonly int? _compressAbove;

    /// <param name="context">
    /// The agent's context. Optional so existing callers and tests keep working — omitting it gives
    /// this agent a fresh one of its own, which is the right default: an agent that is not handed a
    /// context still HAS one, rather than borrowing the caller's list.
    /// </param>
    public Agent(ILlmProvider provider, PluginRegistry plugins, TokenLedger ledger,
        IChatSink sink, IJobPanel jobs, LogFileManager? logs, int maxTurns, int? compressAbove = null,
        AgentContext? context = null, string? globalInstructionsDir = null,
        Core.Mcp.McpToolset? mcp = null,
        string? briefing = null,
        ISubAgentSpawner? spawner = null,
        bool isSubAgent = false)
    {
        // ONLY A CHILD LABELS ITSELF. The parent's requests are unattributed on purpose: prefixing
        // every prompt in an ordinary session with "the main agent wants to…" is noise that trains
        // people to stop reading the heading, which is the opposite of what attribution is for.
        _requesterLabel = isSubAgent
            ? (string.IsNullOrWhiteSpace(briefing) ? "a sub-agent" : briefing.Trim())
            : null;
        _mcp = mcp;
        _spawner = spawner;
        _isSubAgent = isSubAgent;
        _briefing = string.IsNullOrWhiteSpace(briefing) ? null : briefing.Trim();
        _provider = provider;
        _plugins = plugins;
        _ledger = ledger;
        _sink = sink;
        _jobs = jobs;
        _logs = logs;
        // ZERO MEANS NO CAP, the same meaning AgentHost's ConfiguredMaxWorkerTurns already carries
        // and opencode's `agent.steps ?? Infinity`. Taken literally 0 is a ceiling of zero turns: the
        // agent stops before its first call — and NOT harmlessly, because the cap path makes a real
        // provider call to salvage a summary, so it costs a request and returns a plausible-sounding
        // summary of a run that never happened. Nobody configures that on purpose, so the number is
        // free to carry the meaning someone actually intends by it.
        //
        // Translated HERE rather than only in AgentHost, because a sub-agent factory constructs an
        // Agent directly and would otherwise inherit the trap.
        _maxTurns = maxTurns <= 0 ? int.MaxValue : maxTurns;
        _compressAbove = compressAbove;
        _context = context ?? new AgentContext();
        _globalInstructionsDir = globalInstructionsDir;
    }

    /// <summary>Every tool, always. Roles used to slice this per worker name; that mechanism is gone
    /// and safety lives in the permission gate, not in withholding capability.</summary>
    private static readonly IReadOnlyList<WorkerTool> AllTools = Enum.GetValues<WorkerTool>();

    /// <summary>
    /// One exchange on the linear path: prompt → tools → answer.
    /// </summary>
    /// <remarks>
    /// TAKES A PROMPT, RETURNS AN ANSWER. It used to take the caller's transcript list and mutate it,
    /// which coupled the agent's context to the UI's record of the conversation. The transcript is the
    /// UI's; <see cref="Context"/> is what the model sees. The caller appends both.
    ///
    /// <para>The <c>ToolCallId</c> hazard that justified rebuilding the context per prompt — a tool
    /// result outliving the call it belongs to, which providers reject — is handled where it belongs:
    /// the compressor snaps its split so a kept result always keeps its call.</para>
    ///
    /// <para>NO COMPRESSION CHECK AROUND THIS CALL. One used to run in a <c>finally</c> here, on the
    /// reasoning that a single-turn exchange has no "next turn" for the in-loop check to catch. It was
    /// a task-boundary trigger in a mode that no longer has task boundaries, it ran on
    /// <see cref="CancellationToken.None"/> so a cancelled session still paid for it, and the pre-send
    /// check at the top of the turn loop already guarantees nothing over the threshold is ever sent.</para>
    /// </remarks>
    public async Task<SendResult> SendAsync(string prompt, CancellationToken ct)
    {
        // THE AGENT'S OWN CONTEXT, CARRIED ACROSS GOALS. This used to be
        // `new List<ChatMessage>(conversation)` — a fresh working list built from the session history
        // at the start of every goal and dropped at the end, so goal N's tool calls, file reads and
        // reasoning were gone before goal N+1 began (measured on a real run: 33 turns discarded, a
        // session falling from 58,000 tokens to ~5,000 the moment the goal ended). "Read X and explain
        // it" followed by "now change it" re-read X, because nothing of the first goal remained.
        //
        // Nobody else works that way: Claude Code, Codex, opencode, gemini-cli, Cline and goose all
        // keep ONE growing list across prompts and compact on TOKEN pressure rather than at a task
        // boundary. The rebuild also guaranteed a prompt-cache miss — those agents append to a stable
        // prefix precisely so cached reads keep hitting, and discarding cached context saves far less
        // than it costs to rebuild.
        var messages = _context.Messages;

        // The user's prompt joins the agent's context. The caller puts its own copy on the session
        // transcript; this is the one the MODEL sees. A plain append either way — the old branch on
        // `messages.Count > 0` existed only because an empty context was seeded from the caller's
        // list, and there is no caller list any more.
        messages.Add(new ChatMessage
        {
            Role = "user",
            Content = prompt,
            Timestamp = DateTimeOffset.UtcNow,
        });


        // WHERE IT IS. A fresh context has never seen a shell prompt, and measured across one
        // session, ten of twenty shell calls were `find`/`ls` hunting for paths that do not exist on
        // this machine — /Users/<someone>/…, /home/user, bare /.
        //
        // REBUILT EVERY PROMPT, AND REPLACED ONLY IF IT CHANGED.
        //
        // The instruction files are read again each time, so editing AGENTS.md mid-session takes
        // effect on the next prompt. That is the user's call to make: they edited the file, and an
        // agent that silently ignores it until a restart is behaving as though it knows better.
        //
        // The cache is still protected, because the message is REPLACED only when the text actually
        // differs. Unchanged files produce a byte-identical system message, the prefix is stable, and
        // the cached reads keep hitting. A change costs one prefix — which is exactly what the user
        // asked for by editing the file.
        var cwd = TryGetWorkingDirectory();
        if (cwd is not null)
        {
            var systemText = SystemPrompt.Build(new SystemPromptContext(
                    WorkingDirectory: cwd,
                    // Exists, not Directory.Exists: in a git WORKTREE .git is a FILE pointing at the
                    // real one, and treating that as "not a repo" would be wrong in exactly the
                    // checkout style this project is developed in.
                    IsGitRepo: Directory.Exists(Path.Combine(cwd, ".git"))
                            || File.Exists(Path.Combine(cwd, ".git")),
                    Platform: Environment.OSVersion.Platform.ToString(),
                    Today: _startedOn,
                    ModelId: _provider.ModelId)
                {
                    // Read fresh each prompt, like the instruction files above: a server that
                    // connected since the last turn contributes its guidance from the next one, with
                    // no restart. Empty when nothing is connected, which leaves the prompt — and so
                    // the cache prefix — byte-identical for anyone without MCP.
                    McpInstructions = _mcp?.InstructionsByServer()
                                      ?? new Dictionary<string, string>(),

                    // A CHILD GETS A DIFFERENT PROMPT (D24): the commands block dropped, and
                    // # Answering replaced with one written for a model rather than a person at a
                    // terminal. Fixed for this agent's whole life, so its prefix stays byte-identical
                    // — two prefixes per session rather than one, which is correct because they are
                    // two different agents.
                    IsSubAgent = _isSubAgent,
                })
                // AFTER the general prompt, so a project can override it.
                + ProjectInstructions.Render(ProjectInstructions.Find(cwd, _globalInstructionsDir))
                // AND THE BRIEFING LAST, so what this agent was created to do outranks both the
                // general prompt and the project's — it is the most specific instruction there is.
                // Constant for the agent's life, so it extends the cache prefix rather than churning
                // it.
                + RenderBriefing(_briefing);

            var existing = messages.FirstOrDefault(m => m.Role == "system");
            if (existing is null)
                messages.Insert(0, new ChatMessage
                {
                    Role = "system",
                    Content = systemText,
                        // NO DEBUGGING ADVICE HERE. A paragraph on tracing a value between where it
                        // is set and where it is used lived here briefly, added after three drives
                        // failed to find one bug. It was generalised from a single case whose answer
                        // was already known, and it rode on EVERY goal — including the ones that only
                        // ask a question. The cap was the real constraint: an 8 KB window on a
                        // 1,587-line file meant the model read a quarter of it at a time, and no
                        // amount of coaching fixes a window too small to look through. Raising the
                        // window removes the problem; describing how to page around it only hides it.
                    Timestamp = DateTimeOffset.UtcNow,
                });
            else if (!string.Equals(existing.Content, systemText, StringComparison.Ordinal))
            {
                // The instructions changed on disk. Replace in place rather than inserting a second
                // system message: two of them read as a contradiction the model has to resolve.
                messages[messages.IndexOf(existing)] = existing with
                {
                    Content = systemText,
                    Timestamp = DateTimeOffset.UtcNow,
                };
            }
        }

        // MCP tools join the built-ins here, at the point the request is built, so a server that
        // connected since the last prompt is picked up with no restart. Mid-request the list is
        // fixed, which is correct: a tool appearing between two turns of one request would be a
        // moving target for the model.
        var tools = WorkerToolset.For(AllTools, _plugins)
            .Concat(_mcp?.Definitions() ?? [])
            // THE SPAWN TOOL, only when this agent has a spawner. A child has none, so its tool list
            // simply does not contain it — which is the whole no-nesting mechanism, visible here.
            .Concat(_spawner is null ? [] : new[] { _spawner.Definition })
            .ToList();
        var wrote = false;
        var challenges = 0;

        // The last build and test verdicts are FIELDS (_lastBuild/_lastTest), not locals: see their
        // declaration. A broken build outlives the prompt that broke it.

        // Identical (call, arguments, result) triples seen this request, for stuck detection below.
        // Per-request deliberately: a new user message is a genuine perturbation, and carrying the
        // counts across it would nudge about repeats the user has already redirected.
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        // Times the server claimed "tool_use" while no call was parsed. Bounded so a server that
        // reports it on EVERY turn cannot spin the loop.
        var toolUseMismatches = 0;

        // Set when a call has repeated past the point where a nudge could still help, which ends the
        // request. Carried out of the tool loop rather than returned from inside it: the remaining
        // calls of THIS turn still need their results appended, or the conversation ends holding a
        // tool call with no matching result — the orphan providers reject.
        string? stuckOn = null;
        var stuckTimes = 0;

        // Whether a length refusal has already been answered with a compaction this request. One
        // attempt only — see the catch that sets it.
        var overflowRecovered = false;

        // TWO COUNTERS, and they answer different questions. `turn` bounds THIS request against
        // _maxTurns; `_turn` numbers log files across the agent's whole life. Folding them into one
        // would silently tighten the cap on every prompt — the second message in a session would
        // start with the first message's turns already counted against it.
        //
        // _turn IS ADVANCED IN THE BODY, not here. In the increment clause it only ran when the loop
        // CONTINUED, and the commonest turn of all — a prose answer — returns from inside the body
        // instead. So a session of one-turn exchanges logged every prompt as context-000, each
        // silently overwriting the last: the exact log fragmentation this counter exists to prevent,
        // reintroduced by the one path that never reaches a `continue`.
        for (var turn = 0; ; turn++)
        {
            ct.ThrowIfCancellationRequested();

            // COMPRESS ON PRESSURE, BEFORE SENDING — not after the response, which is where this used
            // to sit and where it could not work. A turn's TOOL RESULTS are appended after the
            // response is handled, so a check placed there tests the size of the conversation as it
            // was BEFORE this turn's file reads landed: it fired on a reading that predated the growth
            // it was meant to relieve, and the goal then ended with the grown context never
            // re-measured. Measured live: compaction reported −32% while the token figure moved 20,
            // because it had removed exactly the content that arrived after the last measurement.
            //
            // Here the previous turn's results are in, so the check describes what is about to be
            // sent. The threshold is the context's OWN window less a reserve — see
            // AgentContext.IsUnderPressure — rather than a separately configured number that can
            // disagree with the window the panel shows.
            await MaybeCompressAsync(Id, ct);

            // AT THE CAP, ASK FOR A HANDOFF rather than discarding the run. Hitting the cap used to
            // print one line and throw away everything the model had learned — the user was left
            // with a half-edited tree and no account of what happened or what remains.
            //
            // opencode does the inverse of a keep-going nudge here: it injects a forced-stop prompt
            // ("Tools are disabled until next user input… MUST provide a text response summarizing
            // work done so far") and takes a summary. SWE-agent auto-submits whatever diff exists,
            // on the same principle — an interrupted run should still yield its artifact. Both
            // salvage; neither discards.
            //
            // The summary turn runs WITHOUT tools, so it cannot start new work, and it is the last
            // thing the loop does either way.
            if (turn >= _maxTurns)
            {
                var summary = await SummariseAtCapAsync(messages, tools, ct);
                _sink.ShowError($"stopped after {_maxTurns} turns without finishing.");

                // The salvaged summary IS the answer on this path — the caller puts it on the
                // transcript, exactly as it does for an ordinary reply. CAPPED, not Completed: it is
                // an account of unfinished work, and a caller that cannot tell the difference acts
                // on it as though the work were done.
                return new SendResult(summary, SendOutcome.Capped);
            }

            // OPEN THE TURN BEFORE THE CALL, not after it. A turn is created with thinking:true and
            // the control clears that flag when body content arrives, so opening it here puts the
            // spinner on screen for the whole wait — which is exactly the part that takes seconds to
            // minutes on a local model.
            //
            // It used to be opened and closed together, AFTER the response had fully arrived: the
            // one moment nothing needed indicating. Between a tool result and the next response the
            // transcript sat completely still, with no way to tell a model that is thinking from one
            // that has died somewhere in the silicon.
            var turnId = _sink.BeginAssistantTurn();

            // BEFORE the call, so what is recorded is what was actually sent — including on a turn
            // that then fails, which is the one you most want to look at afterwards. The token count
            // carried is the PREVIOUS turn's measurement, since this turn's has not happened yet.
            LogContext(Id, _turn, messages, _context.Used);

            // THE SIZE THE PROVIDER IS ABOUT TO SEE. Captured here rather than after the response,
            // because by then this turn's reply and tool results have been appended and the figure no
            // longer describes what the reading covers.
            var sentChars = _context.TotalChars();

            LlmResponse response;
            try
            {
                response = await StreamTurnAsync(messages, tools, ct, turnId);
            }
            catch (LlmProviderException ex) when (
                !overflowRecovered &&
                ContextOverflow.IsOverflow(ex.Message, ex.HttpStatus, ex.VendorBody))
            {
                // THE PROVIDER REFUSED IT FOR LENGTH — the second firing moment, and the only one
                // that cannot be wrong. The predictive check ahead of the send works from a
                // CONFIGURED window, which may not be what the endpoint actually serves (a local
                // llama.cpp splits n_ctx across slots) and is silent entirely when no usage is
                // reported. This is the endpoint saying so in its own words, so it is worth more
                // than any estimate: compact and try the same turn again.
                _sink.EndAssistantTurn(turnId);

                // ONCE. If compacting did not make it fit, retrying the same refusal forever is
                // worse than reporting it — the guard is a flag rather than a counter because a
                // second overflow means compaction is not the answer, whatever the count.
                overflowRecovered = true;

                // The refused attempt already wrote its context-NNN log, so spend the number rather
                // than letting the retry overwrite it — the refusal and what followed are two
                // different states of the conversation and both are worth reading afterwards.
                _turn++;

                await CompressionRun.RunAsync(_context, _provider, _jobs, Id,
                    "compress context · provider refused the request as too long",
                    _ledger.Record, ct, compressed: (b, a) =>
                    {
                        ContextCompressed?.Invoke(b, a);
                        if (_context.Used is { } estimated) ContextEstimated?.Invoke(estimated);
                    });

                continue;
            }
            catch (Exception)
            {
                // The turn MUST be closed on every path. A spinner left running after a failure is
                // worse than no spinner: it says "still working" about a goal that is already over.
                _sink.EndAssistantTurn(turnId);
                throw;
            }

            _ledger.Record(response.Usage);

            // RECORD IT ON THE CONTEXT, which needs both the reading and the size it was taken at to
            // estimate honestly after a compaction. Published BEFORE the compression check below, so
            // the reading that TRIGGERS a compression is the one the user sees; the row that follows
            // then explains the drop.
            _context.RecordUsage(response.Usage.InputTokens, sentChars);
            if (response.Usage.InputTokens > 0)
                ContextUsed?.Invoke(response.Usage.InputTokens);


            // LOG THE RAW RESPONSE. Only tool RESULTS were ever written, so the model's own output —
            // the prose, the reasoning, the markdown — existed nowhere once the screen scrolled. A
            // rendering bug reported from a screenshot was undiagnosable: the input that produced it
            // could not be recovered, and every hypothesis about it stayed a guess.
            //
            // Raw, before StripReasoning, because the reasoning block is part of what arrived and a
            // fault in the stripping itself would be invisible in stripped output.
            LogTurn(Id, _turn, response);

            // THIS turn's number is now spent — both LogContext above and LogTurn just now used it,
            // so they pair up as context-NNN/turn-NNN. Advanced here rather than in the loop header
            // so that a turn which RETURNS still counts: see the note there.
            _turn++;

            TurnCompleted?.Invoke(response.ToolCalls.Count);

            var text = ModelOutput.StripReasoning(response.Text);

            // Nothing more will be appended to this turn. Closing it stops the spinner; the text (if
            // any) was streamed in as it arrived.
            _sink.EndAssistantTurn(turnId);

            // KEEP GOING IF THE SERVER SAID "tool_use" BUT WE PARSED NO CALLS. The two disagree only
            // when something went wrong in between — a truncated stream, a malformed arguments blob
            // the accumulator dropped — and ending the goal there discards a turn the model believed
            // it was mid-way through.
            //
            // The mirror case is the one opencode documents: "Some providers return 'stop' even when
            // the assistant message contains tool calls." Both it and crush therefore AND the stop
            // reason with a real scan for tool calls rather than trusting either alone, and a local
            // llama.cpp or vLLM endpoint is exactly the kind that gets this wrong. Trusting the
            // PARSED CALLS as the primary signal covers that half; this covers the other.
            if (response.ToolCalls.Count == 0
                && response.StopReason == "tool_use"
                && toolUseMismatches < MaxToolUseMismatches)
            {
                toolUseMismatches++;
                messages.Add(new ChatMessage
                {
                    Role = "user",
                    Content = "Your last response was cut off before its tool call arrived. Re-issue "
                            + "the call you intended, or say what you want to do next.",
                    Timestamp = DateTimeOffset.UtcNow,
                });
                continue;
            }

            if (response.ToolCalls.Count == 0)
            {
                // A turn with no tool calls is the model saying it is done. CHALLENGE IT if the goal
                // asked for a change and nothing was written — the failure this mode exists to fix
                // ends exactly here, with a confident summary of work that never happened.
                //
                // NO "CANNOT:" ESCAPE HATCH. One lived here — a reply containing that literal
                // suppressed the challenge, so a model could end the loop by saying the right word.
                // It only ever guarded the no-write challenge, which is gone, and it was the same
                // string-matching mistake: an invented protocol token the model was never told about,
                // which a real refusal ("I can't do that because…") would not have used anyway.
                //
                // Two ways a change request can finish badly, and they need different words: nothing
                // was written at all, or something was written that does not build.
                var brokenBuild = wrote && _lastBuild is not null && BuildFailed(_lastBuild);
                var brokenTest = wrote && _lastTest is not null && BuildFailed(_lastTest);
                var broken = brokenBuild || brokenTest;

                // NO "YOU DIDN'T WRITE ANYTHING" CHALLENGE. There was one, and it was removed: it
                // fired when the prompt contained any of seventeen common verbs — "add ", "fix ",
                // "change ", "update " — and no file had been written. That is a substring match on
                // ordinary English, so it challenged questions. Measured against eight realistic
                // prompts, SIX were false positives: "why does the compressor add a summary?",
                // "what would you change about this design?", "who calls update on the panel?".
                // Each cost up to three wasted turns and then an error on screen telling the user
                // their question had failed.
                //
                // No other agent CLI does this — opencode has nothing like it. The failure it was
                // built for (describing an edit instead of making one) is addressed where it belongs,
                // in the system prompt: "USE THEM ... Text in a message changes nothing."
                //
                // The BROKEN BUILD check below is deliberately kept. It is not a guess about intent:
                // a build actually ran and actually failed, and that is a fact about the tree rather
                // than an inference from the wording of a prompt.
                if (broken && challenges < MaxChallenges)
                {
                    challenges++;
                    messages.Add(new ChatMessage
                    {
                        Role = "user",
                        // The FAILING one's output, and the build first when both are red: a test
                        // failure reported against a tree that does not compile is noise.
                        Content = BrokenBuildChallenge(brokenBuild ? _lastBuild! : _lastTest!),
                        Timestamp = DateTimeOffset.UtcNow,
                    });
                    continue;
                }

                // A BROKEN BUILD IS A FAILED REQUEST. Measured live: a correct diagnosis, a patch that
                // did not compile, "Build FAILED" in the transcript, and a confident success summary
                // in the same turn.
                if (broken)
                    _sink.ShowError(
                        "changes were written but the build did not succeed. The last build or test "
                        + "run reported a failure and it was not resolved.");

                // The answer, either way. It is already ON SCREEN — it streamed into the turn opened
                // above — so this is for the caller's transcript, and it is returned rather than
                // pushed onto a list the agent was handed.
                return new SendResult(text, SendOutcome.Completed);
            }

            // Prose that came WITH tool calls is narration ("let me check that file"). It has
            // already streamed into this turn; nothing more to render.

            messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = response.Text ?? "",
                ToolCalls = response.ToolCalls.ToList(),
            });

            foreach (var call in response.ToolCalls)
            {
                var result = await InvokeAndShowAsync(Id, call, ct);
                if (IsWrite(call.Name) && !LooksLikeFailure(result)) wrote = true;

                // STUCK: the same call returning the same result, over and over. Measured on one
                // drive that produced nothing in 42 calls — MarkupParser.cs was READ six times and
                // SEARCHED five times, each returning what it had already returned. A model in that
                // state is not making progress and will not spontaneously leave it; every repeat is
                // a paid turn against the cap.
                //
                // OpenHands calls this "scenario 1: same action, same observation" and nudges once
                // before killing, which is the right order — the model may simply have lost track,
                // and telling it so is far cheaper than failing the goal.
                var signature = call.Name + "\0" + call.Arguments.ToString() + "\0" + result;
                seen.TryGetValue(signature, out var times);
                seen[signature] = ++times;

                if (times == StuckRepeats)
                    messages.Add(new ChatMessage
                    {
                        Role = "user",
                        Content = $"You have called {call.Name} with the same arguments {times} times "
                                + "and received the same result each time. Repeating it will not "
                                + "produce anything new. Use what you already have, or try a "
                                + "genuinely different approach.",
                        Timestamp = DateTimeOffset.UtcNow,
                    });

                // AND THE NUDGE IS NOT A FAILSAFE. It is a request, and a model in a loop is
                // precisely one that is not responding to requests — measured in Plan 1, a provider
                // yielding the same call every turn ran until something else stopped it, and with no
                // turn ceiling in production there was nothing else. The doc below this loop has
                // promised "twice that many before the goal is failed" since the nudge was written;
                // this is the code that makes the promise true.
                //
                // FLAGGED, NOT BROKEN OUT OF. This turn's remaining calls still need their results
                // appended: a tool call left without its result is the orphan providers reject, and
                // ending the request is no reason to leave the context malformed.
                if (times >= StuckRepeats * 2)
                {
                    stuckOn = call.Name;
                    stuckTimes = times;
                }

                // A build or test run REPLACES the previous verdict of ITS OWN KIND rather than
                // accumulating: what matters at the end is whether the tree compiles NOW and whether
                // the tests pass NOW, not whether either ever did. A model that breaks the build,
                // fixes it, and stops has finished the job.
                //
                // Two slots, not one. A build and a test answer different questions, and folding
                // them together lets the answer to one erase the answer to the other — see lastTest.
                if (call.Name == "run_shell" && LooksLikeBuildOrTest(call))
                {
                    if (LooksLikeTest(call)) _lastTest = result;
                    else _lastBuild = result;
                }

                messages.Add(new ChatMessage
                {
                    Role = "tool",
                    // call.Id ?? call.Name, never a bare Id: ToolCallId is the ONLY field marking a
                    // message as a tool result, and a null turns it into an ordinary user turn — no
                    // error, no warning, the model simply never sees the result.
                    ToolCallId = call.Id ?? call.Name,
                    Content = result,
                });
            }

            // STUCK, AND THE NUDGE DID NOT REACH IT. Every result of this turn is now on the context,
            // so the conversation is well-formed and the request can end here. Reported as an error
            // because it is one: the work did not finish, and saying so is the difference between a
            // session that stopped and a session that appears to still be running.
            if (stuckOn is not null)
            {
                _sink.ShowError(
                    $"stopped: {stuckOn} was called with the same arguments {stuckTimes} times and "
                    + "returned the same result each time. The run was not making progress.");
                return new SendResult(text, SendOutcome.Stuck);
            }
        }
    }

    /// <summary>
    /// One final turn asking what was done and what remains, shown in the transcript.
    ///
    /// <para>MATCHES OPENCODE (<c>core/session/runner/max-steps.ts</c>, injected at
    /// <c>session/prompt.ts:1281</c>) in the three things that shape the reply:</para>
    ///
    /// <para>THE MESSAGE IS AN ASSISTANT TURN, not a user one. It reads as the model's own
    /// constraint rather than a new instruction from the person — a user message at this point is one
    /// more request to weigh against the earlier ones, and the earlier ones asked for edits.</para>
    ///
    /// <para>TOOLS STAY BOUND. Passing an empty list was the obvious way to make "no tools"
    /// structural, and it is what this did. But a model whose tools vanish mid-task has been handed a
    /// second puzzle at exactly the wrong moment, and opencode keeps them bound and says instead that
    /// "any attempt to use tools is a critical violation". The instruction is explicit enough to
    /// carry it, and the reply comes back in the shape the rest of the session was speaking in.</para>
    ///
    /// <para>Best-effort: a provider failure here must not replace the cap message with a stack
    /// trace, since the run has already ended and the summary is a courtesy on top of it.</para>
    /// </summary>
    private async Task<string> SummariseAtCapAsync(List<ChatMessage> messages,
        List<ToolDefinition> tools, CancellationToken ct)
    {
        var ask = new List<ChatMessage>(messages)
        {
            new()
            {
                Role = "assistant",
                Content = MaxStepsPrompt,
                Timestamp = DateTimeOffset.UtcNow,
            },
        };

        var turnId = _sink.BeginAssistantTurn();
        try
        {
            var response = await StreamTurnAsync(ask, tools, ct, turnId);
            _ledger.Record(response.Usage);
            return ModelOutput.StripReasoning(response.Text);
        }
        catch (Exception)
        {
            // The run has already ended; a failed courtesy must not replace the cap message with a
            // stack trace.
            return "";
        }
        finally
        {
            _sink.EndAssistantTurn(turnId);
        }
    }

    /// <summary>
    /// Writes one turn's raw model output to the goal's log directory, fire-and-forget.
    ///
    /// <para>Uses the same store as tool results, under a per-turn id, so a session reads in order:
    /// what the model said, then what its calls returned. The tool-call names and arguments are
    /// recorded alongside the prose because a turn is often ONLY calls, and a log that showed
    /// nothing for those turns would look like the model had gone silent.</para>
    ///
    /// <para>Never throws and never awaits: logging is diagnostics, and a goal must not fail — or
    /// stall — because a disk did.</para>
    /// </summary>
    /// <summary>
    /// Records WHAT WAS SENT this turn — the context, one line per message.
    ///
    /// <para>The response was logged from the start; the input never was, and that is the half you
    /// need to answer the questions that actually come up: why did the model not know something it
    /// was told, what is occupying the window, did compaction drop the wrong thing. A tool result
    /// that has been pruned shows as its tombstone here, so a gap in the model's knowledge can be
    /// traced to the turn that created it.</para>
    ///
    /// <para>Sizes and roles rather than full content: the whole point is to see the SHAPE of a
    /// context that may be hundreds of thousands of characters, and a log that reproduces all of it
    /// on every turn is one nobody opens twice. The first line of each message is enough to
    /// recognise it.</para>
    /// </summary>
    private void LogContext(string agentId, int turn, IReadOnlyList<ChatMessage> messages, int? inputTokens)
    {
        if (_logs is null) return;

        try
        {
            var sb = new StringBuilder();
            var chars = 0;
            foreach (var m in messages) chars += m.Content?.Length ?? 0;

            sb.AppendLine($"=== turn {turn:D3} · {messages.Count} messages · {chars:N0} chars"
                        + (inputTokens is { } t ? $" · {t:N0} input tokens" : "") + " ===");

            for (var i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                var role = m.ToolCallId is not null ? "tool" : m.Role;
                var body = (m.Content ?? "").ReplaceLineEndings(" ");
                var head = body.Length <= 120 ? body : body[..120] + "…";
                var calls = m.ToolCalls is { Count: > 0 }
                    ? " [calls: " + string.Join(", ", m.ToolCalls.Select(c => c.Name)) + "]"
                    : "";
                sb.AppendLine($"[{i:D3}] {role,-9} {(m.Content?.Length ?? 0),8:N0}ch{calls}  {head}");
            }

            _ = _logs.AppendAsync(agentId, $"context-{turn:D3}", "log", sb.ToString());
        }
        catch (Exception)
        {
            // Diagnostics must never take down the thing they are diagnosing.
        }
    }

    private void LogTurn(string agentId, int turn, LlmResponse response)
    {
        if (_logs is null) return;

        try
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(response.Text)) sb.AppendLine(response.Text);

            foreach (var call in response.ToolCalls)
                sb.AppendLine($"→ {call.Name} {call.Arguments}");

            if (sb.Length == 0) return;

            // "log", not "response": PathFor VALIDATES the stream against log/stdout/stderr and
            // throws on anything else. An invented name would have thrown on every turn, been
            // swallowed by the catch below, and logged nothing at all — a diagnostic that silently
            // does not work is worse than none, because it is trusted.
            _ = _logs.AppendAsync(agentId, $"turn-{turn:D3}", "log", sb.ToString());
        }
        catch (Exception)
        {
            // Diagnostics must never take down the thing they are diagnosing.
        }
    }

    /// <summary>
    /// Dispatches one tool call and renders it as a transcript row.
    ///
    /// <para>The row is a SYNTHETIC job — it enters no scheduler and no dag. It exists because the
    /// user already reads job rows ("Tool  Read HexEncoder.cs · done · 0.0s") and a tool call is the
    /// same event; inventing a second visual language for it would be gratuitous. Without this the
    /// calls are invisible: <c>ToolCallReported</c> has no UI subscriber anywhere in the app.</para>
    /// </summary>
    private async Task<string> InvokeAndShowAsync(string agentId, ToolCall call, CancellationToken ct)
    {
        var jobId = Helpers.UlidGenerator.NewId();
        var job = new Job
        {
            Id = jobId,
            PlanLocalId = call.Name,
            AgentId = agentId,
            PluginType = ToolPluginType(call.Name),
            DisplayName = DescribeCall(call),
            State = JobState.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            StartedAt = DateTimeOffset.UtcNow,
        };
        _jobs.SetJobs(new[] { job });

        var started = DateTimeOffset.UtcNow;   // rebased by ctx.WorkStarted below

        // THE CHILD'S ID ADDRESSES THIS ROW (D14). Rows key on job.Id, minted per tool call above;
        // the child mints its own Agent.Id. Associating them HERE — the one place both exist — is
        // what lets telemetry, and later background reporting and aggregation, all address the same
        // row through one identifier.
        // The child's id, once it exists. NOT written onto job.AgentId, which is `required init` and
        // already carries the SPAWNING agent's id — the row belongs to the parent's turn, and
        // overwriting that would misattribute it. Held alongside instead, which is what a later
        // expand-the-row swap keys on.
        string? childId = null;

        // A PERIODIC TICK, owned by this call and disposed in its finally.
        //
        // Turn boundaries alone are not enough: a child spends most of a long run INSIDE one turn,
        // waiting on a provider or a slow tool, and a row whose elapsed time only moves between
        // turns reads exactly like a frozen one. MainWindow._panelClock cannot be borrowed for this
        // — it refreshes nothing when the panel is hidden.
        Timer? tick = null;

        void OnChildSpawned(SubAgent child)
        {
            childId = child.Agent.Id;
            job.ProgressMessage = "starting…";
            // UpdateProgress, NEVER UpdateJob, for anything that fires repeatedly: UpdateJob
            // force-expands the row and blanks its body on every call, so a per-second tick would
            // re-open a row the user collapsed and erase whatever was in it.
            _jobs.UpdateProgress(job);

            // The child's own events, straight onto the row. These are EVENTS now rather than
            // settable callbacks, which is what lets a per-child reporter and a later session
            // aggregator both subscribe to one signal.
            var turns = 0;
            child.Agent.TurnCompleted += _ =>
            {
                turns++;
                Report(child, turns);
            };
            child.Agent.ContextUsed += _ => Report(child, turns);

            tick = new Timer(_ => Report(child, turns), null,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        void Report(SubAgent child, int turns)
        {
            // ELAPSED, so a long-running child is visibly ALIVE rather than merely un-finished. A
            // five-minute child otherwise shows one line of numbers that never moves between turns,
            // which reads exactly like a frozen row.
            var elapsed = DateTimeOffset.UtcNow - started;
            var age = elapsed.TotalMinutes >= 1
                ? $" · {(int)elapsed.TotalMinutes}m{elapsed.Seconds:00}s"
                : $" · {elapsed.TotalSeconds:0}s";

            // UsedFraction is null until the provider first reports usage, so an early tick shows
            // turns alone rather than "0% ctx", which would read as a measurement rather than as the
            // absence of one.
            var occupancy = child.Agent.Context.UsedFraction is { } f
                ? $" · {f:P0} ctx"
                : "";
            job.ProgressMessage = $"{turns} turn{(turns == 1 ? "" : "s")}{occupancy}{age}";
            _jobs.UpdateProgress(job);
        }

        var ctx = new JobContext(agentId, jobId, new Dictionary<string, JobResult>(), _logs)
        {
            // Rides with every tool call this agent makes, so the gate can say who is asking.
            Requester = _requesterLabel,
        };

        // THE CLOCK RESTARTS WHEN THE WORK DOES. `started` above is stamped when the ROW appears,
        // which is before the permission gate has asked the user anything — so a command the user
        // took four minutes to approve reported four minutes of runtime. Seen live: a shell call
        // whose own timeout fired at 15s rendered as `failed · 270.8s`, a number that sends whoever
        // reads it hunting for a slow command that never existed.
        //
        // Rebasing rather than replacing: a plugin that never reports (no gate, no
        // WorkStarting call) keeps the original stamp and the old behaviour exactly.
        ctx.WorkStarted += () => started = DateTimeOffset.UtcNow;
        // MCP FIRST, then the built-ins. TryInvokeAsync returns null for a name no server owns, so
        // WorkerToolset's "no such tool" text stays the single message for a name nobody owns — two
        // sources each producing their own version is how a model gets told a tool does not exist by
        // one and nothing by the other.
        //
        // Inside the job wrapper deliberately: an MCP call gets the same transcript row, the same
        // result rendering and the same ct as a built-in. Composed around it, a slow server would
        // show a frozen spinner with no indication of what was being waited on.
        string result;
        try
        {
            // SPAWN FIRST, then MCP, then the built-ins — each returning null for a name it does not
            // own, so the chain is one ?? per source and "no such tool" stays WorkerToolset's single
            // message. Spawn leads because it is the only name that could otherwise be shadowed: an
            // MCP server is free to advertise anything.
            result = (_spawner is null ? null : await _spawner.TryInvokeAsync(call, OnChildSpawned, ct))
                ?? (_mcp is null ? null : await _mcp.TryInvokeAsync(call, ct))
                ?? await WorkerToolset.InvokeAsync(call, AllTools, _plugins, ctx, ct, _mcp?.Names());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // NEVER THROW FOR ANYTHING BUT CANCELLATION, and the reason is severe enough to spell
            // out. The assistant message carrying the tool calls is appended BEFORE they run, so an
            // exception unwinding the foreach leaves tool calls with no matching results — and an
            // orphan 400 is NOT a length error, so ContextOverflow.IsOverflow does not match, the
            // recovery path never runs, and compaction only fires on measured pressure that a small
            // orphaned context never reaches. Every later prompt in the session then fails with the
            // provider's 400 and nothing recovers it but /clear. Worse, it presents on the turn
            // AFTER the failure, which is what makes it hard to diagnose.
            //
            // So the error becomes the tool RESULT and falls through to the messages.Add below.
            // WorkerToolset.InvokeAsync already holds this contract for built-ins; this extends it to
            // the two sources that did not — the spawn branch and _mcp.TryInvokeAsync.
            //
            // NOTE THE FALL-THROUGH RATHER THAN A RETURN. An early `return ErrorEnvelope(ex)` reads
            // naturally and is exactly wrong: it leaves the method before the messages.Add that the
            // result exists to become, producing the orphan this catch was written to prevent.
            //
            // A FAILED SPAWN KEEPS THE ENVELOPE SHAPE. The parent's model was told to expect
            // <sub_agent id state>; handing it a bare "error:" string for the one case that matters
            // most means the failure arrives in a shape it was never told about.
            result = childId is null
                ? $"error: {ex.Message}"
                : SubAgentEnvelope.Render(childId, SendOutcome.Failed, ex.Message);
        }
        catch (OperationCanceledException)
        {
            // THE ROW MUST CLOSE EVEN THOUGH THE TURN IS ENDING. Without this the method was
            // straight-line, so a cancellation skipped everything below and left the job Running —
            // a row that spins for the rest of the session, because nothing sweeps them.
            //
            // NOT REPRODUCIBLE WITH run_shell, and that is worth stating precisely: ProcessRunner
            // catches the OCE, kills the tree and returns a result, so a cancelled shell call comes
            // back as a string and closes as Failed. The path that genuinely escapes is
            // _mcp.TryInvokeAsync, which has no catch at all — cancel during a slow MCP tool call
            // and the row never closes. Anyone who tries to reproduce this with run_shell will
            // fail, and must not conclude the guard is unnecessary.
            job.State = JobState.Cancelled;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.Result = new JobResult
            {
                Success = false,
                ExitCode = -1,
                Duration = DateTimeOffset.UtcNow - started,
                ErrorMessage = "cancelled",
            };
            _jobs.UpdateJob(job);

            // RETHROWN, NOT SWALLOWED. The turn is over: the loop is unwinding and there is no next
            // request, so there is nobody to hand a tool result to. Returning a string here would
            // feed "cancelled" back as though the tool had answered.
            throw;
        }
        finally
        {
            // THE TICK STOPS HOWEVER THIS ENDS — answer, error, or cancellation. A Timer left running
            // holds a closure over the child and keeps writing to a row that has already closed, once
            // a second, for the rest of the session. Null on every path that never spawned, which is
            // almost all of them.
            tick?.Dispose();
        }

        var failed = LooksLikeFailure(result);
        job.State = failed ? JobState.Failed : JobState.Succeeded;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.Result = new JobResult
        {
            Success = !failed,
            ExitCode = failed ? -1 : 0,
            Duration = DateTimeOffset.UtcNow - started,
            ErrorMessage = failed ? result : null,
            Output = new Dictionary<string, object?> { ["content"] = result },
        };
        _jobs.UpdateJob(job);

        return result;
    }

    private async Task<LlmResponse> StreamTurnAsync(List<ChatMessage> messages,
        List<ToolDefinition> tools, CancellationToken ct, ChatMessageId turnId)
    {
        var text = new StringBuilder();
        var calls = new List<ToolCall>();
        LlmUsage usage = new();
        var stop = "";

        // How much of the (reasoning-stripped) text has already been shown. Deltas arrive raw, and a
        // reasoning block can span many of them, so what is SAFE to display is recomputed from the
        // accumulated text after each chunk rather than appended blindly — streaming the raw delta
        // would put the model's <think> block on screen, which is exactly what StripReasoning exists
        // to prevent.
        var shown = 0;

        // Same trick for reasoning: a reasoning block spans many deltas, so only the part not yet
        // written is appended after each chunk.
        var shownReasoning = 0;

        await foreach (var chunk in _provider.ChatStreamAsync(messages, tools, ct))
        {
            if (!string.IsNullOrEmpty(chunk.TextDelta))
            {
                text.Append(chunk.TextDelta);

                var accumulated = text.ToString();

                var visible = ModelOutput.StripReasoning(accumulated);
                if (visible.Length > shown)
                {
                    _sink.AppendAssistant(turnId, visible[shown..]);
                    shown = visible.Length;
                }

                // REASONING GOES IN THE BODY, dimmed — not into the header.
                //
                // It WAS a one-line header that rewrote itself on every new line of thought, and
                // that was wrong twice over. A single line that overwrites itself discards the
                // reasoning as fast as it arrives, so the thing it is meant to make visible can
                // never actually be read; and because nothing clears the header when the turn ends,
                // the last line of thinking stayed welded to the finished message as its title.
                //
                // The body can hold all of it, in order, where it scrolls with everything else.
                //
                // THE AGENT SAYS WHAT KIND OF TEXT THIS IS; the sink decides how it looks. This
                // used to build the markup here — a colour decision inside the turn loop — and the
                // sink could not then tell styled reasoning from unstyled body text on the same
                // method, so only one of the two was ever escaped.
                //
                // The semantic decision that DOES belong here: reasoning is worth showing at all.
                // ChatTranscriptControl clears a message's thinking flag as soon as body content
                // arrives, so this costs the spinner — the right trade, because the reasoning text
                // is better evidence the model is alive than a spinner is: it says WHAT it is doing.
                var reasoning = ModelOutput.ExtractReasoning(accumulated);
                if (reasoning.Length > shownReasoning)
                {
                    _sink.AppendReasoning(turnId, reasoning[shownReasoning..]);
                    shownReasoning = reasoning.Length;
                }
            }

            if (chunk.ToolCallDelta is { } tc) calls.Add(tc);
            if (chunk.Usage is { } u) usage = u;
            if (chunk.StopReason is { Length: > 0 } sr) stop = sr;
        }

        return new LlmResponse
        {
            Text = text.ToString(), ToolCalls = calls, Usage = usage, StopReason = stop,
        };
    }


    /// <summary>
    /// Summarises the older half of <paramref name="messages"/> when the last turn's input crossed
    /// <see cref="_compressAbove"/>.
    ///
    /// <para>THROUGH THE MODEL, not by eviction. Dropping tool results and leaving receipts was the
    /// obvious cheap fix and it is the wrong one: a file read is not dead weight once consumed — what
    /// the model CONCLUDED from it is the value, and that lives nowhere else. Only the model can tell
    /// "this defines the interface I am changing" from "this was irrelevant", and a size-based rule
    /// loses both identically. Every agent in this space compacts by asking the model to write a
    /// handoff, and SessionCompressor already does exactly that.</para>
    ///
    /// <para>Never throws: compression failing must not end a goal that is otherwise working.
    /// SessionCompressor falls back to truncation on a provider error, and its result says which
    /// happened so the transcript can be honest about it.</para>
    /// </summary>
    private async Task MaybeCompressAsync(string agentId, CancellationToken ct)
    {
        // TWO WAYS TO BE OVER, and a configured number wins where it applies: someone who set an
        // explicit threshold knows something about their endpoint that a window size does not capture
        // (a shared or rate-limited box, a provider that charges differently). Absent that, the
        // context's own window decides — the honest ceiling, and the one the panel already shows.
        //
        // BOTH READ REPORTED TOKENS, and neither substitutes anything when none arrive. See
        // AgentContext.IsUnderPressure: a provider that does not report usage is that provider's
        // defect, and estimating around it would mean acting on a guess at the exact number it
        // declined to give.
        string reason;
        if (_compressAbove is { } thresholdTokens)
        {
            if (_context.ProjectedUsed is not { } configuredUsed || configuredUsed <= thresholdTokens) return;
            reason = $"{configuredUsed:N0} tokens over {thresholdTokens:N0}";
        }
        else
        {
            if (!_context.IsUnderPressure) return;
            reason = $"{_context.ProjectedUsed:N0} of {_context.Window:N0} tokens";
        }

        // The row itself lives in CompressionRun, which every compressing route now shares — this one
        // and the /compress command. The threshold test stays here because only this caller measures
        // per-turn pressure.
        await CompressionRun.RunAsync(_context, _provider, _jobs, agentId,
            $"compress context · {reason}",
            _ledger.Record, ct, compressed: (b, a) =>
            {
                ContextCompressed?.Invoke(b, a);
                // The context re-estimated its own occupancy while compacting; publish it so the
                // readout shows where that leaves us rather than the pre-compaction figure.
                if (_context.Used is { } estimated) ContextEstimated?.Invoke(estimated);
            });
    }

    private static bool IsWrite(string toolName) =>
        toolName is "write_file" or "replace_in_file";

    /// <summary>How many times a no-write finish is challenged before the goal is failed.</summary>
    /// <summary>
    /// What the model is told when the turn ceiling is reached. Adapted from opencode's
    /// MAX_STEPS_PROMPT, including the point that this overrides earlier instructions — without it a
    /// model that was asked to make edits treats the request to stop as one more competing
    /// instruction rather than the binding one.
    /// </summary>
    private const string MaxStepsPrompt =
        "CRITICAL - MAXIMUM STEPS REACHED\n\n"
        + "The maximum number of steps allowed for this task has been reached. Tools are disabled "
        + "until next user input. Respond with text only.\n\n"
        + "STRICT REQUIREMENTS:\n"
        + "1. Do NOT make any tool calls (no reads, writes, edits, searches, or any other tools)\n"
        + "2. MUST provide a text response summarising work done so far\n"
        + "3. This constraint overrides ALL other instructions, including any user requests for "
        + "edits or tool use\n\n"
        + "Response must include:\n"
        + "- Statement that maximum steps for this agent have been reached\n"
        + "- Summary of what has been accomplished so far\n"
        + "- List of any remaining tasks that were not completed\n"
        + "- Recommendations for what should be done next\n\n"
        + "Any attempt to use tools is a critical violation. Respond with text ONLY.";

    private const int MaxChallenges = 3;

    /// <summary>
    /// Identical repeats of one (call, arguments, result) before the model is told; twice that many
    /// before the request is ended. Three is high enough that a legitimate re-read after changing
    /// something is never mistaken for a loop.
    ///
    /// <para>THE SECOND THRESHOLD WAS DOCUMENTED HERE LONG BEFORE IT EXISTED. Only the nudge was
    /// ever implemented, so this sentence described an intention rather than the code: a model that
    /// ignored the nudge repeated indefinitely, and with the production turn ceiling at int.MaxValue
    /// nothing else stopped it. Both halves are real now.</para>
    /// </summary>
    private const int StuckRepeats = 3;

    /// <summary>Retries for a "tool_use" turn that carried no parseable call, before the response is
    /// taken at face value. Two, because a genuine truncation is transient and a server that always
    /// misreports would otherwise never let the goal end.</summary>
    private const int MaxToolUseMismatches = 2;

    /// <summary>
    /// Whether a tool result reads as a failure. WorkerToolset never throws — every failure comes
    /// back as a STRING — so "did that write land" cannot be answered by exception handling. Matched
    /// on the two shapes the plugins actually produce.
    /// </summary>
    /// <remarks>
    /// THIS CATCHES MCP RESULTS TOO, and that is intended rather than incidental.
    /// <see cref="Core.Mcp.McpClient.CallToolAsync"/> returns an <c>isError</c> result as text
    /// beginning "error: ", so a failed third-party tool marks its job row failed and shows red —
    /// which is what the user should see. The cost of the heuristic is a server whose SUCCESSFUL
    /// output happens to start with "error" being mislabelled; that is a cosmetic row state, not a
    /// behaviour change, and the model reads the text either way.
    /// </remarks>
    private static bool LooksLikeFailure(string result) =>
        result.StartsWith("error", StringComparison.OrdinalIgnoreCase)
        || result.Contains("was not found", StringComparison.Ordinal)
        || result.Contains("is required", StringComparison.Ordinal);

    /// <summary>
    /// Whether a shell call was a BUILD or TEST run — the commands whose result says whether the
    /// edits actually work.
    ///
    /// <para>Matched on the command text, which is the only signal available: run_shell is one tool
    /// and every toolchain looks different through it. Deliberately narrow — a command that is not
    /// recognised simply does not update the verdict, which fails safe (the goal is judged on the
    /// last build it DID run, or on nothing at all).</para>
    /// </summary>
    /// <summary>
    /// Whether a shell call is running TESTS specifically, as opposed to compiling.
    ///
    /// <para>Both are gates on a finished goal, but they must be remembered separately: a rebuild
    /// after a failing test run would otherwise overwrite the failure with a success and let the
    /// goal finish red. Deliberately a subset of <see cref="LooksLikeBuildOrTest"/> — anything that
    /// is not recognisably a test run is treated as a build, so a new verb defaults to the stricter
    /// reading rather than being silently ignored.</para>
    /// </summary>
    private static bool LooksLikeTest(ToolCall call)
    {
        var cmd = TryGetArgument(call, "command");
        if (string.IsNullOrEmpty(cmd)) return false;

        ReadOnlySpan<string> verbs =
        [
            "dotnet test", "cargo test", "go test",
            "npm test", "yarn test", "pnpm test", "pytest", "vitest", "jest",
        ];
        foreach (var v in verbs)
            if (cmd.Contains(v, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool LooksLikeBuildOrTest(ToolCall call)
    {
        var cmd = TryGetArgument(call, "command");
        if (string.IsNullOrEmpty(cmd)) return false;

        ReadOnlySpan<string> verbs =
        [
            "dotnet build", "dotnet test", "msbuild",
            "cargo build", "cargo test", "cargo check",
            "go build", "go test",
            "npm run build", "npm test", "yarn build", "yarn test", "pnpm build", "pnpm test",
            "make", "cmake --build", "gradle", "mvn ", "pytest", "tsc",
        ];
        foreach (var v in verbs)
            if (cmd.Contains(v, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Whether a build/test result reads as a failure.
    ///
    /// <para>Exit code would be the honest signal, but it does not survive: WorkerToolset renders a
    /// shell result as text, and a non-zero exit already arrives prefixed "error:". Both forms are
    /// matched, plus the phrases the major toolchains print, because a command that fails INSIDE a
    /// pipeline (`… | tail -30`) exits 0 and only says so in its output — which is exactly how the
    /// live failure was invisible: `dotnet build … 2>&amp;1 | tail -30` returned success while its
    /// text said "Build FAILED".</para>
    /// </summary>
    private static bool BuildFailed(string result)
    {
        if (result.StartsWith("error", StringComparison.OrdinalIgnoreCase)) return true;

        ReadOnlySpan<string> markers =
        [
            "Build FAILED", "error CS", "error MSB",
            "Failed!", "FAILED", "Test Run Failed",
            "error[E", "error: could not compile",
            "npm ERR!", "Compilation failed", "SyntaxError", "cannot find symbol",
        ];
        foreach (var m in markers)
            if (result.Contains(m, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// The nudge for a goal whose edits do not build. Carries the build OUTPUT, because the model
    /// has already seen it once and moved on — repeating the fact without the detail would earn the
    /// same shrug.
    /// </summary>
    private static string BrokenBuildChallenge(string buildResult)
    {
        var detail = buildResult.Length > 1500 ? buildResult[..1500] + "…" : buildResult;
        return "The build is broken. Your changes were written, but the last build or test run "
             + "failed and you stopped without fixing it — a change that does not compile is not a "
             + "finished change. Fix it now, or revert your edits and say plainly why it cannot be "
             + "done.\n\nThe failing output was:\n" + detail;
    }

    /// <summary>One argument of a tool call as a string, or null when absent or not a string.</summary>
    private static string? TryGetArgument(ToolCall call, string name)
    {
        try
        {
            return call.Arguments.ValueKind == System.Text.Json.JsonValueKind.Object
                && call.Arguments.TryGetProperty(name, out var v)
                && v.ValueKind == System.Text.Json.JsonValueKind.String
                    ? v.GetString()
                    : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The last non-blank line of a reasoning stream, capped for a one-line header.</summary>
    private static string LastLine(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            return line.Length > 110 ? line[..110] + "…" : line;
        }
        return "";
    }

    /// <summary>
    /// The briefing as its own attributed block, or nothing at all.
    ///
    /// <para>NAMED, like the project instructions above it. An unattributed paragraph appended to a
    /// system prompt reads as though the app said it, leaving the model no way to weigh "what I was
    /// asked to do" against a general rule. Absent entirely when there is no briefing, so a plain
    /// session's prompt — and therefore its cache prefix — is byte-identical to what it was before
    /// this existed.</para>
    /// </summary>
    private static string RenderBriefing(string? briefing) =>
        string.IsNullOrWhiteSpace(briefing)
            ? ""
            : "\n# Your task\n\nThis is what you were created to do. Where it disagrees with "
              + "anything above, follow this.\n\n" + briefing + "\n";


    /// <summary>The plugin a tool dispatches to, for the transcript row's author label only.</summary>
    private string ToolPluginType(string toolName) => toolName switch
    {
        "run_shell" => "shell",
        "http_request" => "http",

        // A WORKER, NOT A FILE OPERATION. Without this the `_ => "file"` below labels a spawn a file
        // op — and worse, InlineJobSink.IsCompactRow treats anything that is not "llm_agent" as
        // compact, so THE ROW COLLAPSES THE MOMENT THE CHILD FINISHES, hiding the answer behind an
        // "expand…". The sink's own comment says so: collapsing at the finish line snatches away the
        // thing the user was reading.
        //
        // NOT A WORKAROUND. llm_agent is already first-class at five sites in InlineJobSink —
        // AuthorFor gives the row a Worker author, IsCompactRow keeps it out of the compact branch,
        // keepOpen leaves it expanded, StatusText returns null so the worker's own content shows, and
        // JobDigest does not placeholder its bulk output. The concept was built for exactly this and
        // was, until now, unused.
        "spawn_agent" => "llm_agent",

        // An MCP tool is none of the three, and the `_ => "file"` below would label it a file
        // operation — a row claiming a third-party server call was a local file read. "mcp" is the
        // honest label; the server's own name is already in DisplayName.
        _ when _mcp is not null && _mcp.Names().Contains(toolName) => "mcp",

        _ => "file",
    };

    private static string DescribeCall(ToolCall call)
    {
        var args = call.Arguments.ToString();
        var detail = args.Length > 60 ? args[..60] + "…" : args;
        return $"{call.Name} {detail}";
    }

    private static string? TryGetWorkingDirectory()
    {
        try { return Directory.GetCurrentDirectory(); }
        catch (Exception) { return null; }
    }
}

using System.Text;
using System.Threading;
using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Execution;
using CxAgent.Core.Models;
using CxAgent.Core.Jobs;
using CxAgent.Core.Storage;
using CxAgent.Core.Sessions;

namespace CxAgent.Core.Agents;

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
/// through <see cref="ToolBindings.InvokeAsync"/> into the same <see cref="JobRegistry"/>, whose
/// file/shell/http executors are wrapped in <c>PermissionGatedExecutor</c>. The gate reads
/// <c>(TypeName, parameters)</c> and nothing else — no part of the job path was load-bearing for it,
/// which is what makes this substitution safe.</para>
///
/// <para>WHAT IS LOST, stated plainly: copilot's whole-plan pre-approval has no plan to approve, and
/// the DAG's parallelism is gone.</para>
/// </summary>
public sealed class Agent
{
    // NOT readonly: /model swaps these in place rather than rebuilding the agent. Every read is at
    // call time — SpendLabel is computed, the system prompt re-reads ModelId each turn and is
    // replaced only when its text differs, and CompressionRun takes the provider as an argument — so
    // there is no derived state a swap could leave stale. See SwapProvider.
    private ILlmProvider _provider;
    private readonly JobRegistry _executors;
    private readonly TokenLedger _ledger;
    private readonly ISessionObserver _sink;
    private readonly IToolObserver _jobs;

    /// <summary>
    /// Mints turn ids. The SESSION owns identity — see <see cref="ChatMessageId"/> for why it stopped
    /// being the observer's job.
    ///
    /// <para>IT DID NOT ACTUALLY OWN IT. This class and <c>AgentHost</c> each held a private counter
    /// and minted into the SAME sink, so one exchange produced ids <c>1, 2, 1</c> — the host minting
    /// the user turn and the assistant turn, then this minting from 1 again. Not cosmetic:
    /// ChatTranscriptSink keys its row map by <c>id.Value</c>, so the second use of an id overwrites
    /// the row the first is still streaming into.</para>
    ///
    /// <para>A DELEGATE, NOT A SESSION REFERENCE, because a child agent has no session — it is driven
    /// by SubAgentSpawner directly. A child is handed its parent's minter so parent and child cannot
    /// collide either, and a bare Agent with nobody supplying one falls back to its own counter,
    /// which is correct for a transcript nothing else writes to.</para>
    ///
    /// <para>A PROPERTY rather than a 17th constructor parameter, following <see cref="Mode"/> and
    /// <see cref="TakePendingSteer"/>: the list is already long enough that one more would be read
    /// positionally by nobody.</para>
    /// </summary>
    public Func<ChatMessageId>? MintTurnId { get; set; }

    private long _nextTurnId;

    private ChatMessageId NextTurnId() =>
        MintTurnId?.Invoke() ?? new ChatMessageId(Interlocked.Increment(ref _nextTurnId));
    private readonly LogFileManager? _logs;
    private readonly int _maxTurns;
    private readonly string? _workingDir;
    private string? _instanceName;

    /// <summary>
    /// How this agent's spend is attributed: <c>instance:model</c> when the instance is known.
    ///
    /// <para>The same label the UI shows, so a figure in <c>/stats</c> and a name in the panel are
    /// the same string rather than two spellings a reader has to reconcile.</para>
    /// </summary>
    private string SpendLabel =>
        _instanceName is { Length: > 0 } instance
            ? $"{instance}:{_provider.ModelId}"
            : _provider.ModelId;

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
    /// <para>Read alongside the project's. It carries what is true of the USER wherever they work,
    /// which a per-repo file cannot express.</para>
    /// </summary>
    private readonly string? _globalInstructionsDir;

    /// <summary>Connected MCP servers, or null when none are configured — which is the common case
    /// and must cost nothing.</summary>
    private readonly Core.Mcp.McpToolset? _mcp;

    /// <summary>
    /// The MCP guidance this agent puts in its system prompt, resolved ONCE and then held.
    ///
    /// <para>Null until the first prompt is built. See <see cref="McpPrompt"/> for why it is pinned
    /// rather than read per turn.</para>
    /// </summary>
    private IReadOnlyDictionary<string, string>? _mcpPrompt;

    /// <summary>
    /// The MCP section's text, fixed at whatever was connected when this agent first built a prompt.
    ///
    /// <para>THE PREFIX MUST NOT MOVE. Everything before the first changed byte of a conversation is
    /// served from the provider's cache; rewriting the system message re-processes the WHOLE context
    /// at cold speed. A server that connects on turn 82 would otherwise charge that turn for a
    /// decision made on turn 1.</para>
    ///
    /// <para>EMPTY IS PINNED TOO, and deliberately: "no servers had anything to say" is an answer,
    /// and treating it as "not resolved yet" would re-check every turn and reintroduce the churn for
    /// exactly the sessions where a server connects late.</para>
    /// </summary>
    private IReadOnlyDictionary<string, string> McpPrompt() =>
        _mcpPrompt ??= _mcp?.InstructionsByServer() ?? new Dictionary<string, string>();

    /// <summary>
    /// How this agent spawns children, or NULL — and null is what makes no-nesting structural.
    ///
    /// <para>A field rather than a parameter because <see cref="InvokeAndShowAsync"/> is an instance
    /// method. A child is constructed without one and therefore cannot spawn: not a rule it is asked
    /// to follow, a tool it was never given.</para>
    /// </summary>
    private readonly ISubAgentSpawner? _spawner;

    /// <summary>
    /// Loads skill bodies on demand. Built here rather than injected because it needs nothing from
    /// the outside: its catalog comes from the same per-turn discovery the prompt uses, so parent and
    /// child each get their own without anything being threaded through the factory.
    /// </summary>
    private readonly Skills.SkillLoader _skills;

    /// <summary>
    /// This agent's own plan. State rather than a tool result, so compaction cannot delete it — see
    /// <see cref="TodoList"/>.
    /// </summary>
    private readonly TodoList _todos = new();

    private readonly TodoTool _todoTool;

    /// <summary>
    /// Asks the user a question, when there is a user to ask. Null for a sub-agent and for any host
    /// that has no UI — see <see cref="AskUserTool"/> for why a child must never have this.
    /// </summary>
    private readonly AskUserTool? _askUser;

    /// <summary>The embedder's own tools, or null when nothing was injected. Offered to a child as
    /// well as a parent by default: a child edits and acts exactly as its parent does, so a
    /// sub-agent denied the embedder's tools would do the work and skip whatever those tools are
    /// for. A tool that must not go to a child says so itself, via OfferToSubAgents.</summary>
    private readonly Jobs.AgentToolset? _agentTools;
    private readonly Permissions.PermissionPolicy? _policy;

    /// <summary>Task 11's speculation handle — see the constructor parameter doc. Null wherever no
    /// classifier is reachable, in which case every call site below is a no-op by construction
    /// (each checks this for null before calling Speculate).</summary>
    private readonly Permissions.ActionClassifier? _classifier;

    /// <summary>
    /// S1 and S2 composed, or null when neither expressed an opinion.
    ///
    /// <para>NOT THE WHOLE PICTURE: S3 arrives per request and is composed onto this at the assembly
    /// site, which is why the composed result is NOT cached here. Caching it would make turn-level
    /// selection silently inert for the session's own agent — the caller most likely to use it.</para>
    /// </summary>
    private readonly Jobs.ToolSelection? _toolSelection;

    /// <summary>
    /// THIS REQUEST's selection, held for the turn so the dispatch site sees what the offer site
    /// showed.
    ///
    /// <para>NOT readonly, and not a parameter: InvokeAndShowAsync is a separate method a thousand
    /// lines from the assembly, and threading the value through every call between them would touch
    /// paths that have nothing to do with selection. Set at the top of SendAsync and cleared in its
    /// finally, so a turn cannot leak its selection into the next.</para>
    ///
    /// <para>ONE TURN AT A TIME is what makes a field safe here — Session.Submit enforces it, and
    /// two concurrent sends on one agent would corrupt Context.Messages long before this mattered.</para>
    /// </summary>
    private Jobs.ToolSelection? _turnTools;

    /// <summary>
    /// The names actually OFFERED for this request, so a call can be answered honestly.
    ///
    /// <para>Two conditions need distinguishing and only this set can do it: a name that matches
    /// nothing that exists is "no such tool", while a name that exists but was not offered is "not
    /// available" — and the second must make the model STOP rather than retry variations.</para>
    ///
    /// <para>Set where the list is assembled, for the same reason <see cref="_turnTools"/> is: the
    /// dispatch chain is a separate method and threading it through every call between them would
    /// touch paths that have nothing to do with selection.</para>
    /// </summary>
    private HashSet<string> _offeredNames = [];

    /// <summary>
    /// Whether the <c>skill</c> tool is offered, for the compaction notice.
    ///
    /// <para>ASKED OF THE AGENT rather than threaded through CompressionRun, which already carries
    /// eight parameters and has three call sites — two here and one on /compress, all of which mean
    /// the same session and would compute the same answer. A bool through three signatures for one
    /// sentence is a worse trade than one accessor.</para>
    ///
    /// <para>TRUE BEFORE THE FIRST TURN, when nothing has been assembled yet: a compaction cannot
    /// have happened either, so the notice this guards has not been written.</para>
    /// </summary>
    internal bool SkillToolOffered =>
        _offeredNames.Count == 0 || _offeredNames.Contains(Jobs.Tool.Skill);

    /// <summary>
    /// The built-ins this agent may run, after its selection.
    ///
    /// <para>ONE DECISION, TWO SITES. The assembly uses it to decide what the model is TOLD;
    /// <see cref="InvokeAndShowAsync"/> passes it to ToolBindings.InvokeAsync to decide what it may
    /// RUN. Touch one without the other and you get a tool that is offered and refused, or hidden
    /// and callable — which is exactly why InvokeAsync's enforcement was kept through the role
    /// removal, for a caller that did not yet exist.</para>
    ///
    /// <para>Computed rather than cached: a selection composed with a per-request S3 is not known at
    /// construction, and caching would make turn-level selection inert for the session's own agent.
    /// It is a set operation over twelve names, called once per tool call.</para>
    /// </summary>
    /// <summary>
    /// Whether one tool survives this request's selection — the SINGLE expression the prompt and the
    /// tool list both consult.
    ///
    /// <para>THE PROMPT IS BUILT BEFORE THE TOOL LIST (`:790` against `:906`), so the gates cannot
    /// read <c>_offeredNames</c> — it is not populated yet. Computing the answer twice is how
    /// <c>Agent.cs:696</c> and <c>:777</c> became two independent reads of the skills catalog, which
    /// is the divergence this feature keeps finding. One predicate, called from both.</para>
    ///
    /// <para>S0 STILL BOUNDS IT: this answers only "does the selection allow it", and the caller
    /// combines that with whether the agent structurally has the tool at all.</para>
    /// </summary>
    /// CACHE: THIS TEXT RIDES IN THE CACHED PREFIX. A system-prompt change is compared at `:834`
    /// and, if it differs, rewrites the prefix from token ZERO — measured on a 116-turn drive at
    /// 67,367 tokens and about 21 seconds of prompt-eval for a 134-character change.
    ///
    /// <para>SAFE AT S1 AND S2, which are fixed for the agent's life: the gated text is byte-
    /// identical every turn, the comparison finds no change, and nothing is invalidated. An S3
    /// selection that VARIES between requests does rewrite it, once per change.</para>
    ///
    /// <para>NOT PINNED, unlike the MCP server list at `:742`, and the distinction there governs:
    /// a caller passing turn tools ASKED for this request to differ and can see why they paid.
    /// Nobody asks for an MCP handshake to land on turn 82. Documented as "set it once per session
    /// unless you mean to", rather than prevented.</para>
    ///
    /// <para>MOVING THIS TEXT LATER IN THE PROMPT WOULD NOT HELP. Providers cache a prefix, not a
    /// diff, and the whole conversation trails the system message — so any position within it
    /// invalidates the same tokens. The layout is already the best available.</para>
    private bool SelectionAllows(string toolName, Jobs.ToolSelection? turnTools)
        => Jobs.ToolSelection.Offers(
            Jobs.ToolSelection.Then(_toolSelection, turnTools), toolName);

    /// <summary>
    /// Whether an OFFERED built-in already owns this name.
    ///
    /// <para>Asked of the injected link, which is dispatched ahead of the built-ins and would
    /// otherwise take the name. A built-in that selection has withheld owns nothing — the name is
    /// free and an injected tool may have it.</para>
    /// </summary>
    private static bool ShadowsLiveBuiltin(string name, IReadOnlyList<BuiltinTool> allowed) =>
        ToolBindings.NamesFor(allowed).Contains(name, StringComparer.Ordinal);

    /// <summary>
    /// Row 7 of the collision matrix: a per-request selection re-enabled a built-in that an
    /// injected tool of the same name had taken. The built-in wins — see
    /// <see cref="ShadowsLiveBuiltin"/> — but unlike the ordinary case (no injected tool has this
    /// name, nothing to say), the model was about to reach a DIFFERENT tool than the one that
    /// registered the name, and a silent skip is the surprise the matrix exists to avoid.
    ///
    /// <para>Always returns null, so the caller's <c>??</c> chain falls through to the built-in
    /// exactly as it did before this existed — this only decides whether to say something first.
    /// </para>
    /// </summary>
    private ToolOutcome? ReportShadowedInjectedTool(string name, IReadOnlyList<BuiltinTool> allowedBuiltins)
    {
        if (_agentTools?.Knows(name) == true)
            _sink.Said(new Message(
                $"an injected tool named `{name}` is registered, but this request's tool selection "
              + $"re-enabled the built-in `{name}` — the built-in ran instead.", Severity.Warning));

        return null;
    }

    /// <summary>
    /// Row 8 of the collision matrix: an MCP server answered a name that an injected tool also
    /// owns. MCP resolves earlier in the dispatch chain, so its answer already stands by the time
    /// this runs — nothing here changes the outcome, only whether the transcript says why the
    /// injected tool of the same name never ran.
    ///
    /// <para><paramref name="mcpResult"/> is passed through unchanged. Null means MCP did not
    /// answer this name at all, which is the ordinary case for every name no server advertises —
    /// nothing to report, and the chain falls through exactly as it did before this existed.</para>
    /// </summary>
    private string? ReportMcpWonOverInjected(string name, string? mcpResult)
    {
        if (mcpResult is not null && _agentTools?.Knows(name) == true)
            _sink.Said(new Message(
                $"an MCP server answered `{name}` before the injected tool of the same name could "
              + "run — the injected tool is unreachable while that server is connected.",
                Severity.Warning));

        return mcpResult;
    }

    private IReadOnlyList<BuiltinTool> AllowedBuiltins(Jobs.ToolSelection? selection)
    {
        var composed = Jobs.ToolSelection.Then(_toolSelection, selection);
        if (composed is null) return AllBuiltins;

        var offered = ToolBindings.For(AllBuiltins, _executors);
        return Jobs.ToolBindings.ToolsNamed(composed.Apply(offered).Select(t => t.Name));
    }

    /// <summary>Test seam: whether an injected tool was OFFERED to this agent. The filtering happens
    /// in the constructor, so it cannot be observed any other way without running a turn.</summary>
    internal bool KnowsInjectedToolForTest(string name) => _agentTools?.Knows(name) ?? false;

    /// <summary>Injected tool names withdrawn for colliding with another injected tool — see
    /// <see cref="Jobs.AgentToolset.Withdrawn"/>. Empty when no offered set was built at all, which
    /// is the same "nothing to report" as an empty toolset.</summary>
    internal IReadOnlyList<string> WithdrawnAgentTools => _agentTools?.Withdrawn ?? [];

    /// <summary>The plan as it stands, for the UI. Empty is the common case.</summary>
    public IReadOnlyList<TodoItem> Todos => _todos.Items;

    /// <summary>Raised when the model rewrites its plan, so a panel can follow along.</summary>
    public event Action? TodosChanged;

    /// <summary>
    /// Whether this agent works alone or may delegate — SETTABLE, and the only mutable thing about
    /// how this agent is configured.
    ///
    /// <para>MUTABLE BECAUSE IT COSTS NOTHING TO BE. Both things a mode changes are rebuilt on every
    /// prompt anyway: the tool list at the request build, and the system message reconciled at index 0
    /// (replaced only when its text differs). So the next <c>SendAsync</c> simply reads this and acts
    /// on it — no rebuilt agent, no re-wire, and the conversation is not even involved, since it
    /// belongs to <see cref="AgentHost"/> and is handed in.</para>
    ///
    /// <para>Contrast the briefing, which is constructor-only: that IS the cache prefix and rewriting
    /// it mid-session would throw away every cached token at the moment a conversation is longest. A
    /// mode change costs exactly one prefix miss, which is what the user asked for by changing it.</para>
    ///
    /// <para>NOT read mid-request. The tool list is fixed once a request begins (see the request
    /// build), so a change between two turns of one request cannot make the model chase a tool that
    /// vanished underneath it.</para>
    /// </summary>
    /// <remarks>
    /// A <see cref="WorkingMode"/> rather than a bare <see cref="AgentMode"/>: delegation is one
    /// axis of how a session is set up, and the others arriving later would otherwise each thread
    /// themselves through the same ten files. <c>Mode = AgentMode.FanOut</c> still compiles — the
    /// implicit conversion is a widening.
    /// </remarks>
    public WorkingMode Mode { get; set; } = WorkingMode.Default;

    /// <summary>
    /// Where a mid-turn correction comes from, or null for an agent nobody can steer.
    ///
    /// <para>A FUNCTION, NOT THE SESSION. The agent has no business knowing what a Session is — this
    /// asks "is there anything for me?" and takes it, which a test satisfies with a lambda over a
    /// local. It returns the text and CLEARS it, so the same correction cannot be delivered twice.</para>
    ///
    /// <para>NULL FOR CHILDREN, and that is the design rather than an omission. A sub-agent was
    /// spawned with a brief; redirecting it mid-flight would mean the parent's account of what it
    /// asked for no longer matches what happened. Steering is a conversation the user is having with
    /// the session, and a child is not in it.</para>
    ///
    /// <para>A PROPERTY rather than a 17th constructor parameter, following <see cref="Mode"/>: the
    /// list is already long enough that one more would be read positionally by nobody.</para>
    /// </summary>
    public Func<string?>? TakePendingSteer { get; set; }

    /// <summary>
    /// Points this agent at a different model, keeping everything else.
    ///
    /// <para>THE CONVERSATION NEVER MOVES, which is the whole reason this exists. The alternative —
    /// /model rebuilding the agent and the host, then carrying the context and the ledger across the
    /// gap by hand — is apparatus for changing one field. Nothing is carried here because nothing is
    /// left behind.</para>
    ///
    /// <para>THE WINDOW COMES TOO, and it is the only part with behaviour attached. A session moving
    /// to a smaller-context model keeps every message it had, so it must start measuring against the
    /// new denominator at once; the turn loop tests pressure before composing each request, so the
    /// next turn compacts if it needs to. Nothing is forced here — compacting at the moment of the
    /// switch would summarise a conversation the user might not send another turn on, which is the
    /// same reason /model has never compacted.</para>
    ///
    /// <para>A CONFIGURED compressAbove IS NOT TOUCHED. That is a user's token budget, not a fact
    /// about the model, and a switch has no business rewriting it.</para>
    ///
    /// <para>NOT MID-TURN. The caller refuses while a turn runs, and must keep doing so: swapping
    /// between two calls of one turn would send half a conversation to one model and half to
    /// another.</para>
    /// </summary>
    public void SwapProvider(ILlmProvider provider, string? instanceName, int? contextWindow)
    {
        _provider = provider;
        _instanceName = instanceName;
        if (contextWindow is not null) _context.Window = contextWindow;
    }

    /// <summary>True when this agent can actually spawn: fan-out mode AND a spawner to do it with.
    /// A child has no spawner whatever its mode says, which is what makes no-nesting structural.</summary>
    private bool CanSpawn => Mode.CanDelegate && _spawner is not null;

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
    /// What the CALLER knew that this agent could not — situational context, fixed at construction.
    ///
    /// <para>A SECOND CHANNEL RATHER THAN MORE BRIEFING, because the two have different authors and
    /// therefore different authority (D9). A briefing comes from CONFIG: written by a human,
    /// inspectable, and it is the highest-precedence text in the prompt — <see cref="RenderBriefing"/>
    /// tells the agent to follow it where it disagrees with anything above. Context comes from a
    /// PARENT MODEL: generated per call, reviewed by nobody. Ranking them the same way would let a
    /// parent that fancies writing talk its way past a config that said "read only", which is silent
    /// capability escalation.</para>
    ///
    /// <para>So this renders BELOW the briefing and claims no authority: it says what is true, not
    /// what to do. "The build is currently broken in IndentShift.cs, ignore that file" — a fact a
    /// fixed config type cannot express and that often saves a child several wasted turns.</para>
    ///
    /// <para>IN THE SYSTEM MESSAGE, not the prompt, and that is the whole mechanical point.
    /// <c>PinnedHeadCount</c> pins index 0, so this survives compaction while a prompt is summarised
    /// away with the older half of the conversation. A long-running child forgets what it was asked
    /// and never forgets what it was told.</para>
    /// </summary>
    private readonly string? _callerContext;

    /// <summary>
    /// This agent's conversation, for its whole life — the thing that makes it self-contained.
    ///
    /// <para>A field rather than a local inside <see cref="SendAsync"/> because a context that
    /// exists only for the duration of one method cannot be owned by anything: not by a readout that
    /// wants to report real occupancy, not by a <c>/compress</c> that wants a single meaningful
    /// target, and not by an agent that is supposed to carry what it learned into its next task. A
    /// sub-agent gets its own <see cref="Agent"/> and therefore its own context, which is
    /// precisely what the fan-out design assumes it already had.</para>
    /// </summary>
    private readonly AgentContext _context;

    /// <summary>This agent's context — its messages, its occupancy, its window.</summary>
    public AgentContext Context => _context;

    /// <summary>
    /// The skills whose bodies are STILL IN THIS AGENT'S WINDOW, in load order.
    ///
    /// <para>DERIVED, NOT TRACKED — the same scan the load tool answers with, for the same reason: a
    /// list that drifted from the window would tell the user a skill is shaping the answer after
    /// compaction removed it. It reports what is true right now, including that a skill silently
    /// stopped applying.</para>
    ///
    /// <para>A WORKER'S ROW IS THE ONLY PLACE THIS IS VISIBLE. A child's context is invisible by
    /// design, so a skill it loaded is the one thing shaping its answer that the parent cannot
    /// otherwise learn — and by the time the row finishes, the context is gone.</para>
    /// </summary>
    public IReadOnlyList<string> LoadedSkills => Skills.SkillLoader.LoadedIn(_context.Messages);

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
    /// Raised when a CHILD has spent tokens, so the session readout can repaint mid-turn.
    ///
    /// <para>A child records into the shared ledger under its OWN model id, correctly and live — but
    /// the only thing that repainted spend was <see cref="TurnCompleted"/>, and this agent completes
    /// no turns while it is blocked inside the spawn tool waiting for that child. So a worker could
    /// burn a window's worth of tokens on a second model and the panel showed the figures from
    /// before it started: right in memory, stale on screen, and most wrong exactly when a second
    /// model is in play, which is the case the per-model breakdown exists to show.</para>
    ///
    /// <para>Carries nothing. The ledger is shared and already correct — this says only WHEN to
    /// re-read it, which keeps the child's spend attributed by the ledger rather than by whoever
    /// happens to be listening.</para>
    /// </summary>
    public event Action? ChildSpend;

    /// <summary>
    /// Raised when a tool call finishes: name, executor type, outcome, duration, and how many
    /// characters its result put INTO the context.
    ///
    /// <para>AN EVENT, NOT A STORE REFERENCE. The loop must not know that history is a database, or
    /// that there is one — this is the same reason logging is being moved to an event
    /// (isolated-kernel.md item 1). The host subscribes and writes; a host that does not subscribe
    /// records nothing and the loop cannot tell.</para>
    ///
    /// <para><c>result_chars</c> is the field worth having: it is what a tool COST the context, and
    /// the whole premise of delegation is moving large results out of the parent.</para>
    /// </summary>
    public event Action<ToolCallReport>? ToolCallFinished;

    /// <summary>
    /// Raised when a spawned child finishes, with everything the parent knows about the run.
    ///
    /// <para>THE PARENT REPORTS IT, because a child never reaches a store — <c>SubAgentFactory</c>
    /// builds children directly as <see cref="Agent"/> precisely so they cannot write a session row
    /// that resume would later offer as a crashed session the user never ran. The parent is also the
    /// only party that sees both ends of the run.</para>
    /// </summary>
    public event Action<ChildRunReport>? ChildFinished;

    /// <summary>
    /// Raised the moment a child is BUILT, before it runs — the pairing of the row already on screen
    /// with the child that row is about.
    ///
    /// <para>THE ONLY PLACE THAT PAIRING EXISTS. A spawn's row is minted here, per tool call, and the
    /// child mints its own id inside the spawner; nothing downstream sees both. <see cref="ChildFinished"/>
    /// is the same pairing an hour too late — a row that wants to show what its child is doing needs
    /// it at the start, not at the end — and the envelope, which is how a FINISHED row joins to its
    /// child, does not exist until the child has answered.</para>
    ///
    /// <para>BEFORE IT RUNS is the load-bearing half. A child that spends four minutes inside its
    /// first turn raises no other event at all, and that is precisely the run whose progress somebody
    /// is watching.</para>
    ///
    /// <para>AN EVENT, NOT A STORE OR A CONTROL REFERENCE, for the reason <see cref="ToolCallFinished"/>
    /// gives: the loop must not know what is listening, and a host that does not subscribe learns
    /// nothing and the loop cannot tell.</para>
    /// </summary>
    public event Action<SpawnedChild>? ChildSpawned;

    /// <summary>
    /// What THIS agent has spent, input and output. A private tally, not a share of the ledger.
    ///
    /// <para>The ledger is deliberately shared — a budget belongs to the session, not to an agent —
    /// so it can say what all children spent together but never what ONE child cost. That is the
    /// figure a finished worker row needs: "this planner cost 41k" is actionable in a way that a
    /// session total is not.</para>
    /// </summary>
    public (int Input, int Output) Spend => (Volatile.Read(ref _spentInput), Volatile.Read(ref _spentOutput));

    private int _spentInput;
    private int _spentOutput;

    /// <summary>
    /// True while this agent is stopped at a permission prompt.
    ///
    /// <para>Read CROSS-THREAD by a parent's row timer while this agent's own flow writes it, hence
    /// volatile — a bool write is atomic but the reader must not cache it in a register and show a
    /// row that never changes.</para>
    ///
    /// <para>On the AGENT rather than routed from the gate, because the gate is SHARED: one instance
    /// serves the parent and every child, and its request carries a display label rather than an id.
    /// Two children of the same type would be indistinguishable to anything routing by label, while
    /// each agent knows perfectly well whether it is the one waiting.</para>
    /// </summary>
    public bool IsWaitingOnPermission
    {
        get => Volatile.Read(ref _waitingOnPermission) != 0;
        internal set => Volatile.Write(ref _waitingOnPermission, value ? 1 : 0);
    }

    private int _waitingOnPermission;

    // Interlocked, because a child's tally is read by the PARENT'S tick timer on another thread
    // while the child's own turn loop writes it.
    private void RecordOwnSpend(LlmUsage usage)
    {
        Interlocked.Add(ref _spentInput, usage.InputTokens);
        Interlocked.Add(ref _spentOutput, usage.OutputTokens);
    }


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

    /// <param name="provider">The model this agent talks to. Sub-agents may be given a different one.</param>
    /// <param name="executors">The tool implementations behind the built-in tool names.</param>
    /// <param name="ledger">Where this agent's token spend is recorded; shared with its children.</param>
    /// <param name="sink">Where the agent's words go — the observer an embedder supplies.</param>
    /// <param name="jobs">Where tool activity is reported.</param>
    /// <param name="logs">Per-turn transcripts on disk, or null to keep none.</param>
    /// <param name="maxTurns">How many turns one goal may take before the agent stops and summarises.</param>
    /// <param name="compressAbove">
    /// Input tokens past which the loop compresses its own context, or null to never compress. See
    /// the field of the same name for why this exists rather than a turn cap.
    /// </param>
    /// <param name="context">
    /// The agent's context. Optional so existing callers and tests keep working — omitting it gives
    /// this agent a fresh one of its own, which is the right default: an agent that is not handed a
    /// context still HAS one, rather than borrowing the caller's list.
    /// </param>
    /// <param name="globalInstructionsDir">Where user-level AGENTS.md and friends are read from.</param>
    /// <param name="mcp">Connected MCP servers, whose tools are offered beside the built-ins.</param>
    /// <param name="briefing">
    /// The highest-authority text in this agent's prompt — what an agent TYPE is told it is for.
    /// Null for an agent with no type.
    /// </param>
    /// <param name="spawner">
    /// How this agent delegates, or null if it cannot. A child is constructed without one, which is
    /// what makes "no sub-agents of sub-agents" structural rather than a rule it is asked to obey.
    /// </param>
    /// <param name="isSubAgent">Whether this agent is itself a child — it reports and renders differently.</param>
    /// <param name="callerContext">What the parent already knew and passed down. Ranks below the briefing.</param>
    /// <param name="label">A short name for this agent in the UI, for children whose work needs a row.</param>
    /// <param name="askUser">
    /// How the agent asks its user a question, or null when nobody is listening — a sub-agent, or a
    /// headless host. Null removes the ask_user tool rather than making it fail.
    /// </param>
    /// <param name="workingDir">The folder this agent works in; also the permission boundary.</param>
    /// <param name="instanceName">
    /// Which configured provider entry this is, for spend attribution. Two entries can serve the same
    /// model against different endpoints, and keying spend by model alone would merge them.
    /// </param>
    /// <param name="agentTools">Tools the embedder injected, offered beside the built-ins.</param>
    /// <param name="toolSelection">
    /// Which tools this agent is offered, or null for all of them. See <see cref="Jobs.ToolSelection"/>.
    /// </param>
    /// <param name="policy">
    /// The session's permission policy, passed to MCP calls. Null refuses every MCP call.
    /// </param>
    /// <param name="classifier">
    /// TASK 11: the same classifier <see cref="Permissions.PermissionDecider"/> consults when a
    /// gated call actually needs a verdict. Passed here so this agent can call
    /// <see cref="Permissions.ActionClassifier.Speculate"/> the moment a tool call is PARSED,
    /// before <see cref="Permissions.PermissionGatedExecutor"/> asks for one synchronously. Null
    /// wherever no classifier is reachable (headless runs, most tests) — the agent simply never
    /// speculates, and every gated call falls back to paying its own synchronous cost.
    /// </param>
    public Agent(ILlmProvider provider, JobRegistry executors, TokenLedger ledger,
        ISessionObserver sink, IToolObserver jobs, LogFileManager? logs, int maxTurns, int? compressAbove = null,
        AgentContext? context = null, string? globalInstructionsDir = null,
        Core.Mcp.McpToolset? mcp = null,
        string? briefing = null,
        ISubAgentSpawner? spawner = null,
        bool isSubAgent = false,
        string? callerContext = null,
        string? label = null,
        Func<IReadOnlyList<UserQuestion>, CancellationToken, Task<QuestionAnswers>>? askUser = null,
        string? workingDir = null,
        string? instanceName = null,
        IReadOnlyList<Jobs.IAgentTool>? agentTools = null,
        Jobs.ToolSelection? toolSelection = null,
        Permissions.PermissionPolicy? policy = null,
        Permissions.ActionClassifier? classifier = null)
    {
        // CARRIED FOR MCP, which builds its own PermissionRequest rather than going through
        // PermissionGatedExecutor. Without it the gate refuses every MCP call for want of a policy —
        // see McpToolset.TryInvokeAsync.
        _policy = policy;
        // TASK 11'S SPECULATION HANDLE. Held as-is, not wrapped — Speculate is itself already the
        // "start it and forget it" API, so there is nothing for this layer to add beyond deciding
        // WHEN to call it (see the tool-call dispatch loop below).
        _classifier = classifier;
        // WHICH CONFIGURED INSTANCE THIS IS, for spend attribution.
        //
        // Two `providers` entries can serve the SAME model against different endpoints with
        // different windows — `local:qwen3` and `small:qwen3` — and keying spend by model alone
        // merges them into one row that answers nothing. The provider driver cannot supply this: an
        // instance NAME is config's, not the driver's.
        _instanceName = instanceName;

        // THE DIRECTORY THIS AGENT WORKS IN, as data.
        //
        // Reading Directory.GetCurrentDirectory() at every use instead would be PROCESS-global — the
        // skills discovered, the AGENTS.md read and the path told to the model would come from
        // wherever the process happens to be pointing, while this agent's OWN host records sessions
        // against the directory it was constructed for. Two sources for one fact, identical only for
        // as long as nothing moves the process.
        //
        // Null falls back to the process, for a caller that has no opinion.
        _workingDir = workingDir;
        // NAMED callerContext, NOT context: `context` on this constructor is already the
        // AgentContext — the conversation itself. Two different things called the same word at one
        // call site is how a caller passes the wrong one and gets a child that shares its parent's
        // messages.
        _callerContext = string.IsNullOrWhiteSpace(callerContext) ? null : callerContext.Trim();
        // ONLY A CHILD LABELS ITSELF. The parent's requests are unattributed on purpose: prefixing
        // every prompt in an ordinary session with "the main agent wants to…" is noise that trains
        // people to stop reading the heading, which is the opposite of what attribution is for.
        //
        // ITS OWN PARAMETER, NOT DERIVED FROM THE BRIEFING. It briefly was, and that coupled a UI
        // label to the highest-authority text in the prompt: emptying the briefing (correctly, since
        // config types do not exist yet) silently emptied the permission prompt's attribution too.
        // A label and a standing instruction are different things and now travel separately.
        _requesterLabel = isSubAgent
            ? (string.IsNullOrWhiteSpace(label) ? "a sub-agent" : label.Trim())
            : null;
        _mcp = mcp;
        _spawner = spawner;

        // RESOLVED PER CALL, not captured here: the catalog is read from disk each turn, so a skill
        // added mid-session is loadable from the same turn its description reaches the prompt. A
        // snapshot taken at construction would let the two disagree — the model reading about a skill
        // the loader cannot find.
        _todoTool = new TodoTool(_todos);

        // NEVER FOR A SUB-AGENT, whatever the caller passed. A child has no user: its output goes to
        // its parent, and a child blocking on a question nobody can see is a hang that ends only
        // when the parent's turn is cancelled. Enforced here rather than trusted to the factory, so
        // the guarantee holds for any construction path.
        _askUser = askUser is not null && !isSubAgent ? new AskUserTool(askUser) : null;

        // A CHILD KEEPS THESE, EXCEPT THE ONES THAT NEED A SCREEN. Injected tools are inherited by
        // default — a child edits files exactly as its parent does — but a tool whose output is for
        // a PERSON cannot work here: a child's rows go to a BufferedJobPanel that nothing displays,
        // so the tool would render, report success, and have its output discarded. The model would
        // be told its showing worked when nobody saw anything.
        //
        // FILTERED HERE, NOT IN THE FACTORY, for the reason _askUser is: enforced at construction,
        // the guarantee holds for any path that builds a child. A withheld tool is one the child was
        // never given, so calling it gets the ordinary "no such tool".
        var offered = isSubAgent
            ? agentTools?.Where(t => t.OfferToSubAgents).ToList()
            : agentTools;
        _agentTools = offered is { Count: > 0 } ? new Jobs.AgentToolset(offered) : null;
        _toolSelection = toolSelection;

        _skills = new Skills.SkillLoader(() =>
        {
            var cwd = TryGetWorkingDirectory();
            return cwd is null
                ? new Skills.SkillCatalogResult([], [], null)
                : Skills.SkillCatalog.Find(cwd, _globalInstructionsDir);
        });
        _isSubAgent = isSubAgent;
        _briefing = string.IsNullOrWhiteSpace(briefing) ? null : briefing.Trim();
        _provider = provider;
        _executors = executors;
        _ledger = ledger;
        _sink = sink;
        _jobs = jobs;
        _logs = logs;
        // ZERO MEANS NO CAP, the same meaning AgentHost.CeilingFor gives a configured 0. Taken
        // literally 0 is a ceiling of zero turns: the agent stops before its first call — and NOT
        // harmlessly, because the cap path makes a real
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

    /// <summary>Every tool, always. Safety lives in the permission gate, not in withholding
    /// capability from a worker by name.</summary>
    private static readonly IReadOnlyList<BuiltinTool> AllBuiltins = Enum.GetValues<BuiltinTool>();

    /// <summary>
    /// One exchange on the linear path: prompt → tools → answer.
    /// </summary>
    /// <remarks>
    /// TAKES A PROMPT, RETURNS AN ANSWER. Taking the caller's transcript list and mutating it would
    /// couple the agent's context to the UI's record of the conversation. The transcript is the UI's;
    /// <see cref="Context"/> is what the model sees. The caller appends both.
    ///
    /// <para>The <c>ToolCallId</c> hazard that justified rebuilding the context per prompt — a tool
    /// result outliving the call it belongs to, which providers reject — is handled where it belongs:
    /// the compressor snaps its split so a kept result always keeps its call.</para>
    ///
    /// <para>NO COMPRESSION CHECK AROUND THIS CALL, despite the tempting argument that a single-turn
    /// exchange has no "next turn" for the in-loop check to catch. Such a check is a task-boundary
    /// trigger in a mode that has no task boundaries; a <c>finally</c> here would run on
    /// <see cref="CancellationToken.None"/> so a cancelled session still paid for it; and the pre-send
    /// check at the top of the turn loop already guarantees nothing over the threshold is ever sent.</para>
    /// </remarks>
    public async Task<SendResult> SendAsync(string prompt, CancellationToken ct,
        Jobs.ToolSelection? turnTools = null)
    {
        // HELD FOR THE TURN so the dispatch site can see what the offer site showed.
        //
        // ASSIGNED, NOT SCOPED. A try/finally around a method with several returns and a thousand
        // lines between them is a larger change than this earns, and it would buy nothing: the next
        // SendAsync overwrites this before any tool runs, and ONE TURN AT A TIME is enforced by
        // Session.Submit — two concurrent sends would corrupt Context.Messages long before a stale
        // selection mattered.
        _turnTools = turnTools;
        // THE AGENT'S OWN CONTEXT, CARRIED ACROSS GOALS. A per-goal working list —
        // `new List<ChatMessage>(conversation)` built at the start of every goal and dropped at the
        // end — loses goal N's tool calls, file reads and reasoning before goal N+1 begins (measured
        // on a real run: 33 turns discarded, a session falling from 58,000 tokens to ~5,000 the moment
        // the goal ended). "Read X and explain it" followed by "now change it" re-reads X, because
        // nothing of the first goal remains.
        //
        // So there is ONE growing list across prompts, compacted on TOKEN pressure rather than at a
        // task boundary. Rebuilding also guarantees a prompt-cache miss — appending to a stable
        // prefix is what keeps cached reads hitting, and discarding cached context saves far less
        // than it costs to rebuild.
        var messages = _context.Messages;

        // The user's prompt joins the agent's context. The caller puts its own copy on the session
        // transcript; this is the one the MODEL sees. A plain append unconditionally: nothing seeds
        // an empty context from a caller's list, so there is no first-message case to branch on.
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
                    // PINNED ON FIRST USE, unlike the instruction files above — and the difference is
                    // who asked for the change.
                    //
                    // Reading this fresh every prompt lets a server finishing its handshake
                    // mid-session rewrite the system message and invalidate the cache prefix from
                    // token ZERO. Measured on a 116-turn drive: a 134-character change at turn 82
                    // forces a full reprocess of 67,367 tokens. On the same endpoint an identical
                    // prompt costs 43ms cached against 1,420ms cold, so one late connection buys
                    // about 21 seconds of prompt-eval and changes nothing the model does.
                    //
                    // A user who edits AGENTS.md ASKED for the next prompt to differ and can see why
                    // they paid. Nobody asks for an MCP handshake to land on turn 82. Tool
                    // DEFINITIONS stay live — Definitions() is still read per turn, so a server that
                    // connects late still offers its tools; it just cannot retroactively rewrite the
                    // prose above them.
                    McpInstructions = McpPrompt(),

                    // READ FROM DISK EACH PROMPT, exactly like the instruction files below and for
                    // the same reason: a skill edited mid-session takes effect on the next turn. The
                    // cache is protected by the text COMPARISON above, not by avoiding the read —
                    // unchanged files render byte-identical and the message is not replaced.
                    //
                    // EVERY AGENT DISCOVERS ITS OWN. A child runs this same code from the same
                    // working directory, so it gets the same catalog without anything being threaded
                    // through the factory. What a child does NOT inherit is whatever the parent
                    // LOADED — that lives in the parent's messages, which a child never sees.
                    Skills = _skills.Catalog().Skills,

                    // A CHILD GETS A DIFFERENT PROMPT (D24): the commands block dropped, and
                    // # Answering replaced with one written for a model rather than a person at a
                    // terminal. Fixed for this agent's whole life, so its prefix stays byte-identical
                    // — two prefixes per session rather than one, which is correct because they are
                    // two different agents.
                    IsSubAgent = _isSubAgent,

                    // The three parent-obligation lines are for an agent that can actually delegate.
                    // In single mode they describe machinery it does not have.
                    // && THE SELECTION, so the prompt does not spend a block teaching delegation to
                    // an agent whose `agent` tool was withheld. A child never has it either way —
                    // this is the PARENT case, which the structural gate alone cannot see.
                    CanSpawn = CanSpawn && SelectionAllows(Jobs.Tool.Agent, turnTools),

                    // Gated the same way, and false for a child by construction — _askUser is null
                    // for a sub-agent whatever the caller passed.
                    CanAskUser = _askUser is not null && SelectionAllows(Jobs.Tool.AskUser, turnTools),
                })
                // AFTER the general prompt, so a project can override it.
                + ProjectInstructions.Render(ProjectInstructions.Find(cwd, _globalInstructionsDir))
                // AND THE BRIEFING LAST, so what this agent was created to do outranks both the
                // general prompt and the project's — it is the most specific instruction there is.
                // Constant for the agent's life, so it extends the cache prefix rather than churning
                // it.
                + RenderBriefing(_briefing)
                // CONTEXT BELOW THE BRIEFING. Both survive compaction; only the briefing carries
                // authority. See _context for why a parent-written instruction must not outrank a
                // config-written one.
                + RenderContext(_callerContext);

            // THE PLAN DOES NOT BELONG HERE — see PlaceTaskList. Appending it above looks safe on
            // the reasoning that a rewrite "invalidates only the tail of the cached prefix", but a
            // prefix cache has no tail: it matches the longest common prefix and stops at the first
            // differing byte, so a change at the END of this message invalidates the whole
            // conversation after it. Measured, a 134-character plan edit re-processes 67,367 tokens.
            //
            // Nor does the system message make the plan outlive compaction. It is rebuilt from
            // _todos every turn, so the plan is RE-INJECTED, never preserved.

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
        var tools = ToolBindings.For(AllBuiltins, _executors)
            .Concat(_mcp?.Definitions() ?? [])
            // THE SPAWN TOOL, only when this agent CAN spawn — fan-out mode, and a spawner to do it
            // with. Two independent reasons not to offer it, and both matter: a child has no spawner
            // (the no-nesting mechanism), and a single-mode parent has one but is not using it.
            .Concat(CanSpawn ? new[] { _spawner!.Definition } : [])
            // THE LOAD TOOL, only when there is something to load. Offering it with an empty catalog
            // advertises a capability whose every call can only fail, and costs schema bytes in the
            // request for every session that has no skills — the same reasoning that keeps the
            // catalog section out of the prompt when it is empty.
            .Concat(_skills.Catalog().Skills.Count > 0 ? new[] { _skills.Definition } : [])
            // THE PLAN TOOL, always. Unlike skills there is no catalog to be empty — an agent can
            // always have work worth tracking, and the list starting empty is the normal state
            // rather than a reason to withhold the tool.
            .Concat(new[] { _todoTool.Definition })
            // ONLY WHEN THERE IS SOMEONE TO ASK. Withheld from a child and from any host with no UI
            // — the same mechanism that makes "no sub-agents of sub-agents" structural: not a rule
            // the agent is asked to follow, a tool it was never given.
            .Concat(_askUser is not null ? new[] { _askUser.Definition } : [])
            // THE EMBEDDER'S OWN. Last in the list, and dispatched last among the named tools, so a
            // consumer can never shadow a built-in name the model already trusts. A tool the model
            // is never TOLD about can never be called, so dispatch alone would have been half a
            // feature.
            .Concat(_agentTools?.Definitions() ?? [])
            .ToList();

        // THE SELECTION, APPLIED ONCE, HERE. Everything above has already run every structural gate
        // — a child has no ask_user, a single-mode agent has no `agent`, an empty catalog has no
        // `skill` — so `tools` IS the S0 set and a `+` term can only match something already in it.
        // That is why S0 needs no check of its own: a second place deciding it is a second place
        // that can disagree with the first.
        //
        // MCP IS EXEMPT, and by name rather than by position: enabled servers' tools are added to
        // every request whatever a selection says, because `enabled` per server is their control and
        // their names arrive after config is read. Filtering them here would be the delta-timing bug
        // this design removed rather than mitigated.
        // S3 COMPOSED HERE, NOT AT CONSTRUCTION. The request's own selection arrives as an argument
        // and joins the session's; composing at construction would make it inert for the session's
        // own agent, which is the caller most likely to use it.
        if (Jobs.ToolSelection.Then(_toolSelection, turnTools) is { } selection)
        {
            var mcpNames = _mcp?.Names().ToHashSet(StringComparer.Ordinal) ?? [];
            var selectable = tools.Where(t => !mcpNames.Contains(t.Name)).ToList();
            var kept = selection.Apply(selectable);

            // ORDER PRESERVED: the assembly order is deliberate (built-ins first, so a consumer
            // cannot appear ahead of a name the model already trusts), so this filters in place
            // rather than concatenating the two groups back together.
            var keptNames = kept.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            tools = [.. tools.Where(t => mcpNames.Contains(t.Name) || keptNames.Contains(t.Name))];
        }

        // AFTER THE FILTER, so a refusal lists what was actually offered rather than what could have
        // been. Held for the turn because the dispatch chain is a separate method a thousand lines
        // away, and threading it through every call between would touch unrelated paths.
        _offeredNames = [.. tools.Select(t => t.Name)];

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

            // AFTER COMPACTION, AND EVERY TURN. After, because compacting a list that was just
            // placed could cut it or leave it mid-conversation when the entire point is that it is
            // last. Every turn rather than once per goal, because a todowrite issued during a tool
            // loop has to reach the model on the following turn — which is precisely when the model
            // is acting on it.
            PlaceTaskList();

            // AT THE CAP, ASK FOR A HANDOFF rather than discarding the run. Hitting the cap used to
            // print one line and throw away everything the model had learned — the user was left
            // with a half-edited tree and no account of what happened or what remains.
            //
            // So the cap injects a forced-stop prompt ("Tools are disabled until next user input…
            // MUST provide a text response summarizing work done so far") and takes a summary: an
            // interrupted run should still yield its artifact rather than discard it.
            //
            // The summary turn runs WITHOUT tools, so it cannot start new work, and it is the last
            // thing the loop does either way.
            if (turn >= _maxTurns)
            {
                var summary = await SummariseAtCapAsync(messages, tools, ct);
                _sink.Said(new Message($"stopped after {_maxTurns} turns without finishing.", Severity.Error));

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
            // Opening and closing the turn together, AFTER the response has fully arrived, indicates
            // the one moment nothing needs indicating: between a tool result and the next response
            // the transcript sits completely still, with no way to tell a model that is thinking from
            // one that has died somewhere in the silicon.
            var turnId = NextTurnId();
            _sink.AssistantTurnBegan(turnId);

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
                _sink.AssistantTurnEnded(turnId);

                // ONCE. If compacting did not make it fit, retrying the same refusal forever is
                // worse than reporting it — the guard is a flag rather than a counter because a
                // second overflow means compaction is not the answer, whatever the count.
                overflowRecovered = true;

                // The refused attempt already wrote its context-NNN log, so spend the number rather
                // than letting the retry overwrite it — the refusal and what followed are two
                // different states of the conversation and both are worth reading afterwards.
                _turn++;

                await CompressionRun.RunAsync(
                    new CompressionRun.CompressionWork(_context, _provider, SkillToolOffered),
                    new CompressionRun.CompressionReport(_jobs, Id,
                        "compress context · provider refused the request as too long",
                        // ATTRIBUTED LIKE ANY OTHER CALL. Compaction is a real request to a real
                        // model, and a summarisation turn that vanished from the per-model tally
                        // would make the numbers disagree with the session total for no reason a
                        // reader could work out.
                        u => { _ledger.Record(u, SpendLabel, _isSubAgent); RecordOwnSpend(u); },
                        (b, a) =>
                        {
                            ContextCompressed?.Invoke(b, a);
                            if (_context.Used is { } estimated) ContextEstimated?.Invoke(estimated);
                        }),
                    ct);

                continue;
            }
            catch (Exception)
            {
                // The turn MUST be closed on every path. A spinner left running after a failure is
                // worse than no spinner: it says "still working" about a goal that is already over.
                _sink.AssistantTurnEnded(turnId);
                throw;
            }

            _ledger.Record(response.Usage, SpendLabel, _isSubAgent);
            RecordOwnSpend(response.Usage);

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
            _sink.AssistantTurnEnded(turnId);

            // KEEP GOING IF THE SERVER SAID "tool_use" BUT WE PARSED NO CALLS. The two disagree only
            // when something went wrong in between — a truncated stream, a malformed arguments blob
            // the accumulator dropped — and ending the goal there discards a turn the model believed
            // it was mid-way through.
            //
            // The mirror case is a provider returning 'stop' even when the assistant message
            // contains tool calls. So the stop reason is ANDed with a real scan for tool calls
            // rather than either being trusted alone, and a local llama.cpp or vLLM endpoint is
            // exactly the kind that gets this wrong. Trusting the PARSED CALLS as the primary signal
            // covers that half; this covers the other.
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
                // THE MODEL'S OWN ANSWER GOES INTO THE CONVERSATION, and this line is the whole
                // difference between a chat and a series of unrelated questions. The tool-call path
                // below appends its assistant message; without this line THIS path returns without
                // one, so a plain conversational reply is rendered to the user, streamed into the
                // transcript, counted in the token totals — and never added to `messages`.
                //
                // WHAT THAT LOOKS LIKE: ask "say something", get "Hello! How can I help you today?",
                // then ask "what have you replied before?" and be told "This is the first message in
                // our conversation, so I haven't replied to you before." The model is telling the
                // truth about what it can see. Seen in a live session, with the token counter showing
                // history WAS being sent — which is what makes it confusing: the user's turns are all
                // there, and only the assistant's are missing.
                //
                // BEFORE THE CHALLENGE BLOCK BELOW, not after: that block appends a USER message and
                // `continue`s, so an assistant reply added later would arrive out of order — the model
                // would see its challenge before the answer that provoked it.
                if (!string.IsNullOrWhiteSpace(response.Text))
                {
                    messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = response.Text,
                        Timestamp = DateTimeOffset.UtcNow,
                    });

                    // AND PUT THE TASK LIST BACK LAST. It is pinned to the end of the conversation
                    // (PlaceTaskList removes and re-appends it for exactly this reason), so anything
                    // appended after it silently demotes it — which is what appending the reply did
                    // until this line existed.
                    PlaceTaskList();
                }

                // A turn with no tool calls is the model saying it is done. CHALLENGE IT if the goal
                // asked for a change and nothing was written — the failure this mode exists to fix
                // ends exactly here, with a confident summary of work that never happened.
                //
                // NO "CANNOT:" ESCAPE HATCH, and none should be added. Suppressing the challenge on a
                // literal token lets a model end the loop by saying the right word, and the token is
                // one it was never told about — a real refusal ("I can't do that because…") would not
                // use it, so the string match catches only the model that guessed.
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
                // The failure it was built for (describing an edit instead of making one) is
                // addressed where it belongs, in the system prompt: "USE THEM ... Text in a message
                // changes nothing."
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
                    _sink.Said(new Message(
                        "changes were written but the build did not succeed. The last build or test "
                        + "run reported a failure and it was not resolved.", Severity.Error));

                // The answer, either way. It is already ON SCREEN — it streamed into the turn opened
                // above — so this is for the caller's transcript, and it is returned rather than
                // pushed onto a list the agent was handed.
                // NOTHING TO SAY IS NOT THE SAME AS FINISHING. An empty answer here means the loop
                // ended without the model ever producing text — a provider call that never returned,
                // which on a saturated local server is what a dropped request looks like from the
                // inside. Reported as Completed it was indistinguishable from a finished run with a
                // terse answer, so a parent read "" and had to guess; see SendOutcome.Silent.
                if (string.IsNullOrWhiteSpace(text))
                {
                    _sink.Said(new Message(
                        "the model returned no answer — the request may have been dropped or timed "
                        + "out. Nothing was lost, but this run produced no result.", Severity.Error));

                    return new SendResult(text, SendOutcome.Silent);
                }

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

            // TASK 11: SPECULATE THE MOMENT THE CALLS ARE PARSED, not when the walk below reaches
            // each one. response.ToolCalls is complete right here — every call this turn's model
            // response asked for, before any of them has actually been dispatched — so this is the
            // earliest point that exists to start the classifier's 10-second round trip ahead of the
            // synchronous gate that will eventually need its answer. By the time the walk below
            // reaches a gated call, the verdict is often already sitting in Task 10's cache.
            //
            // ONLY WHERE A VERDICT COULD MATTER. ToolBindings.RequestsFor answers empty for
            // anything this toolset does not recognise (MCP, an embedder tool, a made-up name), and
            // EffectFor(request) == None for anything the gate would never consult the classifier
            // for anyway (not auto mode, an untrusted folder, a kind EffectFor never gates) — both
            // checked so a wasted call is a genuine possible-but-not-taken action, not a call for a
            // request that structurally could never reach the classifier.
            //
            // NOTHING IS AWAITED HERE. Speculate itself is fire-and-forget (see its own doc comment
            // for why a fault inside it can never surface into this turn), and this loop's only job
            // is to START calls, not wait on them — waiting would erase the entire benefit of
            // starting early.
            if (_classifier is not null && _policy is not null)
            {
                foreach (var call in response.ToolCalls)
                {
                    foreach (var request in Jobs.ToolBindings.RequestsFor(call, _workingDir))
                    {
                        if (_policy.EffectFor(request) == Permissions.ReviewEffect.None) continue;
                        _classifier.Speculate(request, ct);
                    }
                }
            }

            // CANCELLATION MUST LEAVE THE CONVERSATION WELL-FORMED.
            //
            // The assistant message above is already in the LIVE context — `messages` IS
            // `_context.Messages` — and it names every tool call the model asked for. If the loop
            // unwinds before each of those has a matching `tool` result, the context keeps a call
            // with no answer: the orphan of §1b. The provider rejects the WHOLE conversation with a
            // 400, `ContextOverflow.IsOverflow` does not match it, and nothing recovers but /clear.
            //
            // The turn is over either way. The difference is whether the SESSION survives it — and
            // the user pressing Escape expects to keep talking, not to lose the conversation.
            //
            // Reachable through any tool that observes the token: a spawned child (its own loop
            // throws at its top-of-turn check) and MCP. `run_shell` is immune only because
            // ProcessRunner swallows OperationCanceledException, which is why this went unseen.
            // Children started this turn and not yet awaited. Declared outside the try so the
            // cancellation handler can join them — a child left running past its turn keeps ticking
            // into a closed row and may finish onto an id the backfill has already answered.
            var spawned = new List<(ToolCall Call, Task<string> Task)>();

            try
            {
                // TWO PHASES, AND THE SPLIT IS THE POINT.
                //
                // Dispatch produces a STRING. Everything else — the write flag, the stuck ledger, the
                // nudge, the build/test verdicts, the tool result itself — happens in Record below,
                // on this thread, in call order.
                //
                // Today both phases run in one sequential pass and this is a pure refactor: the
                // observable behaviour is byte-for-byte what it was. It exists so that when a spawn is
                // later STARTED rather than awaited, the concurrent part carries no shared state at
                // all. Nothing races because nothing concurrent touches anything — which is a much
                // cheaper guarantee than locking six mutation sites and hoping the list stays at six.
                void Record(ToolCall call, string result)
                {
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

                // THE WALK — in EMITTED ORDER, always.
                //
                // A spawn is STARTED and not awaited; everything else is awaited inline exactly as
                // before. Nothing is reordered: a `run_shell` that preceded a spawn still runs before
                // that spawn begins. The alternative considered and rejected was partitioning the
                // calls and hoisting spawns to the front — which starts a child against a tree that a
                // preceding command had not yet changed, silently and with no error anywhere.
                //
                // Deferring the AWAIT rather than moving the CALL buys the same overlap: the child
                // runs while the rest of the turn's tools do, because a child is minutes and a
                // read_file is milliseconds.
                foreach (var call in response.ToolCalls)
                {
                    var task = InvokeAndShowAsync(Id, call, ct);

                    if (CanSpawn && string.Equals(call.Name, _spawner!.ToolName, StringComparison.Ordinal))
                    {
                        // HELD, NOT AWAITED. Its result is recorded after the walk.
                        spawned.Add((call, task));
                        continue;
                    }

                    Record(call, await task);
                }

                // THE BARRIER. Every child is resolved before the loop resumes, because an assistant
                // message whose tool call has no result is the orphan that 400s the session — see the
                // cancellation handler above, which exists for the same reason.
                //
                // Recorded AFTER the inline results rather than woven among them. The list is
                // therefore not in emitted order for a mixed turn, and that is correct: the wire
                // matches results to calls by ToolCallId, never by position. Buffering the inline
                // results to "fix" the order would be worse than useless — the cancellation backfill
                // reads `messages` to find what is already answered, so buffered results would be
                // invisible to it and every one of them double-answered.
                foreach (var (call, task) in spawned)
                    Record(call, await task);

                spawned.Clear();

                // AFTER THE BARRIER, BEFORE THE NEXT REQUEST — the only point in a turn where a user
                // message can legally be appended. The assistant message above declared N tool calls
                // and every one of them must be answered before anything else joins the conversation;
                // a user turn spliced among them is the orphan shape that 400s a session. By here
                // every result is recorded, including every child's, so the list is complete.
                //
                // WHICH MEANS A CHILD DELAYS IT, by design and not by accident. A four-minute
                // sub-agent holds the barrier for four minutes and the correction waits — correct,
                // because the parent has nothing to reconsider until it sees what the child found.
                //
                // BEFORE the loop's compression check rather than at the top of the next iteration:
                // the correction is then part of what pressure is measured against, so it cannot be
                // summarised away in the same turn it arrived, and PlaceTaskList runs after it.
                if (TakePendingSteer?.Invoke() is { Length: > 0 } steer)
                {
                    messages.Add(new ChatMessage
                    {
                        Role = "user",
                        Content = steer,
                        Timestamp = DateTimeOffset.UtcNow,
                    });

                    // ANNOUNCED, so the transcript can replace its "queued" placeholder with a real
                    // user turn. Without this the model changes direction for no visible reason.
                    _sink.UserTurnAdded(NextTurnId(), steer);

                    // THE BUDGET GOES BACK. A correction arriving at turn 90 of a 100-turn cap would
                    // otherwise get ten turns to act on instructions it had never seen. The turn is
                    // doing different work now, so its budget starts again.
                    turn = 0;
                }
            }
            catch (OperationCanceledException)
            {
                // JOIN THE CHILDREN FIRST. They share this turn's token, so they are already ending —
                // but "already ending" is not "ended", and letting the exception leave while one is
                // still running is the barrier violation D27 forbids arriving through the back door:
                // a timer still ticking into a closed row, a sink writing to a finished transcript,
                // and a child completing onto an id the backfill below has just answered.
                //
                // Awaited for their SIDE EFFECTS ONLY — rows closing, timers disposing. The results
                // are discarded because the backfill answers every call uniformly, and a child that
                // happened to finish in the gap does not deserve different treatment from its sibling
                // that did not. Exceptions are swallowed for the same reason: this path is already
                // unwinding, and a faulted child must not replace the cancellation the user asked for.
                foreach (var (_, task) in spawned)
                {
                    try { await task; }
                    catch (Exception) { }
                }

                // BACKFILL EVERY UNANSWERED CALL, then let the cancellation continue.
                //
                // Includes calls that never STARTED: the loop may have been three into a list of
                // five, and the model's message named all five. A call nobody ran is as orphaning as
                // one that was interrupted — the provider only checks that each id has a result.
                //
                // Matched by id against what is already there rather than by counting, because the
                // interrupted call may or may not have appended before it threw, and appending a
                // second result for one id would be its own malformation.
                var answered = messages
                    .Where(m => m.Role == "tool" && m.ToolCallId is not null)
                    .Select(m => m.ToolCallId!)
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var call in response.ToolCalls)
                {
                    var id = call.Id ?? call.Name;
                    if (!answered.Add(id)) continue;

                    messages.Add(new ChatMessage
                    {
                        Role = "tool",
                        ToolCallId = id,
                        // SAID PLAINLY, because the model reads this on the next turn and a blank
                        // result is indistinguishable from a tool that found nothing.
                        Content = "cancelled: the user stopped this turn before this call completed.",
                    });
                }

                throw;
            }

            // STUCK, AND THE NUDGE DID NOT REACH IT. Every result of this turn is now on the context,
            // so the conversation is well-formed and the request can end here. Reported as an error
            // because it is one: the work did not finish, and saying so is the difference between a
            // session that stopped and a session that appears to still be running.
            if (stuckOn is not null)
            {
                _sink.Said(new Message(
                    $"stopped: {stuckOn} was called with the same arguments {stuckTimes} times and "
                    + "returned the same result each time. The run was not making progress.", Severity.Error));
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

        var turnId = NextTurnId();
        _sink.AssistantTurnBegan(turnId);
        try
        {
            var response = await StreamTurnAsync(ask, tools, ct, turnId);
            _ledger.Record(response.Usage, SpendLabel, _isSubAgent);
            RecordOwnSpend(response.Usage);
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
            _sink.AssistantTurnEnded(turnId);
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
    /// <para>AN INDEX, THEN THE BODIES. The summary block came first and is still what you read to
    /// see the SHAPE of a context — sizes, roles, which turn made which tool call. But a preview-only
    /// log cannot answer "was that instruction actually in the prompt", and the truncation is
    /// invisible when you grep it: a search for text that IS in the context returns nothing, which
    /// reads as absence rather than as truncation. That cost a wrong diagnosis — a system-prompt line
    /// was reported missing from a live run when it had been there all along.</para>
    ///
    /// <para>So the index stays, and the full text follows it under a marker. Scanning is unchanged;
    /// grepping now answers the question it appeared to answer before.</para>
    ///
    /// <para>The first line of each message is enough to
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
                // ONE LINE PER MESSAGE for the index, so newlines are flattened HERE and only here —
                // the full text below keeps its own.
                var body = (m.Content ?? "").ReplaceLineEndings(" ");
                var head = body.Length <= 120 ? body : body[..120] + "…";
                var calls = m.ToolCalls is { Count: > 0 }
                    ? " [calls: " + string.Join(", ", m.ToolCalls.Select(c => c.Name)) + "]"
                    : "";
                sb.AppendLine($"[{i:D3}] {role,-9} {(m.Content?.Length ?? 0),8:N0}ch{calls}  {head}");
            }

            // THE FULL TEXT, VERBATIM, UNDER THE INDEX. Nothing is elided: this is the file you open
            // to find out what the model was actually sent, and a log that answers "roughly what" is
            // the one that sends you looking for a bug that is not there.
            //
            // Tool ARGUMENTS are included too. A call rendered as its name alone tells you a search
            // happened but not what it searched for, which is exactly the detail that explains why a
            // model went the way it did.
            sb.AppendLine();
            sb.AppendLine("=== full messages ===");
            for (var i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                var role = m.ToolCallId is not null ? "tool" : m.Role;
                sb.AppendLine();
                sb.AppendLine($"--- [{i:D3}] {role} ---");
                if (!string.IsNullOrEmpty(m.Content)) sb.AppendLine(m.Content);
                if (m.ToolCalls is { Count: > 0 })
                    foreach (var c in m.ToolCalls)
                        sb.AppendLine($"[tool call] {c.Name} {c.Arguments}");
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
    /// <summary>
    /// The refusal for a tool this agent exists to have but was not offered, or null to dispatch.
    ///
    /// <para>KNOWN-BUT-WITHHELD ONLY. A name nobody owns falls through to ToolBindings's "no such
    /// tool", which lists what IS available and is the right answer for a typo. This one is for a
    /// name that would have worked under a different selection.</para>
    ///
    /// <para>NO ALIASES TO RESOLVE. Every tool answers to exactly one name since 2026-08-19, so a
    /// raw comparison is sound — an earlier design needed list_files→glob canonicalisation here, and
    /// would have refused a legitimate call from a resumed conversation without it.</para>
    /// </summary>
    private string? Withheld(string name)
    {
        if (_offeredNames.Count == 0 || _offeredNames.Contains(name)) return null;

        // A NAME AN INJECTED TOOL OWNS AND A BUILT-IN ALSO HAS IS NOT WITHHELD BY THE BUILT-IN'S
        // REMOVAL. Selection withholding `write_file` frees the name for an injected tool, so
        // refusing here would answer for a built-in the selection already removed.
        //
        // ONLY WHEN A BUILT-IN SHARES THE NAME, which is what keeps a WITHHELD INJECTED tool
        // withheld: selection can name injected tools too, and one removed by `-echo_tool` must
        // stay removed. The narrow case is a collision the selection resolved in the injected
        // tool's favour, not any injected tool at all.
        if (_agentTools?.Knows(name) == true && ToolBindings.IsBuiltinName(name)) return null;

        // COULD THIS NAME EVER BE OFFERED? Only a name this build knows is "withheld"; anything else
        // is a typo or a stale memory, and belongs to the terminator's message.
        if (!Jobs.Tool.IsKnown(name) && !(_agentTools?.Knows(name) ?? false)) return null;

        return $"tool '{name}' is not available. Available: {string.Join(", ", _offeredNames)}";
    }

    private async Task<string> InvokeAndShowAsync(string agentId, ToolCall call, CancellationToken ct)
    {
        var jobId = Helpers.UlidGenerator.NewId();
        var job = new Job
        {
            Id = jobId,
            PlanLocalId = call.Name,
            AgentId = agentId,
            JobType = ToolJobType(call.Name),
            DisplayName = DescribeCall(call),
            State = JobState.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            StartedAt = DateTimeOffset.UtcNow,
        };
        _jobs.ToolsChanged(new[] { job });

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

        // THE PROMPT THIS CHILD WAS GIVEN, read from the call rather than from the child: the child
        // holds it as a user message inside a buffered context, and digging it back out would couple
        // the row to the message layout. Read once — it never changes.
        var childPrompt = ReadArg(call, "prompt");

        // THE CHILD ITSELF, once built — so the finished row can account for it after SendAsync has
        // returned. Null on every failure path before the child exists, which is why every read of
        // it is guarded rather than assumed.
        SubAgent? spawned = null;
        var childTurns = 0;

        // THE SKILLS IT LOADED, captured on each tick so the finished row can name them after the
        // child's context is gone. The live row reads child.Agent directly; the finished row is built
        // once SendAsync has returned, and cannot.
        IReadOnlyList<string> childSkills = [];

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
            spawned = child;
            job.ProgressMessage = "starting…";
            // ToolProgressed, NEVER ToolUpdated, for anything that fires repeatedly: ToolUpdated
            // force-expands the row and blanks its body on every call, so a per-second tick would
            // re-open a row the user collapsed and erase whatever was in it.
            _jobs.ToolProgressed(job);

            // The child's own events, straight onto the row. These are EVENTS now rather than
            // settable callbacks, which is what lets a per-child reporter and a later session
            // aggregator both subscribe to one signal.
            // childTurns is the ENCLOSING local, not one scoped here: the finished row states how
            // many turns the run took, and a counter that died with this closure could not say.
            child.Agent.TurnCompleted += _ =>
            {
                childTurns++;
                Report(child, childTurns);
            };
            child.Agent.ContextUsed += _ => Report(child, childTurns);

            // A CHILD'S TOOL CALLS, FORWARDED. The child has no store and no host — that is the whole
            // isolation design — so its calls would otherwise be recorded nowhere, and a child's calls
            // are the interesting ones: that is where the expensive reading happens. The report keeps
            // the CHILD's agent id, so forwarding attributes rather than absorbs.
            child.Agent.ToolCallFinished += report => ToolCallFinished?.Invoke(report);

            // AND THE PAIRING ITSELF, announced. Everything above attaches this parent to the child's
            // events; this hands the child to whoever is drawing the row, which is the one thing the
            // events cannot do — they carry measurements, and a live row needs the child.
            //
            // AFTER the wiring rather than before it, so a subscriber that reads the child on this
            // call finds it fully attached rather than half-built.
            ChildSpawned?.Invoke(new SpawnedChild(job.Id, child));

            tick = new Timer(_ => Report(child, childTurns), null,
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
                ? $" · {Commands.StatsDashboard.Percent(f)} ctx"
                : "";
            // WAITING SAYS SO, AND SAYS IT FIRST. Turns and occupancy keep ticking while a child sits
            // at a prompt, so a row showing only those reads as working — and with several children
            // up, the user cannot tell which one their answer would release. The state changes too,
            // so anything reasoning about the row (not just the header text) sees it.
            var waiting = child.Agent.IsWaitingOnPermission;
            job.State = waiting ? JobState.WaitingOnPermission : JobState.Running;

            job.ProgressMessage = waiting
                ? $"waiting for permission · {turns} turn{(turns == 1 ? "" : "s")}{age}"
                : $"{turns} turn{(turns == 1 ? "" : "s")}{occupancy}{age}";

            // WHAT IT IS DOING, behind the expand. The header's counters say a child is ALIVE; only
            // its tool calls say whether it is on the right track — which is the question a
            // minutes-long run provokes and the one that decides whether to press Escape.
            //
            // The child's own BufferedJobPanel already holds every row it has drawn (that is what
            // keeps them out of the parent's transcript), so this is a read of something recorded
            // rather than new bookkeeping.
            //
            // THE LAST FEW ONLY. A child that has made forty calls has a scrollback nobody wants
            // inline; the recent ones are what "on the right track" is judged from.
            var recent = child.Jobs.Jobs
                .OrderByDescending(j => j.StartedAt)
                .Take(6)
                .Reverse()
                .Select(j => $"  {j.DisplayName}")
                .ToList();

            // WHAT IT IS, above what it is doing. The recent-tools list answers "on the right track",
            // but not "what did I even start" — and the header cannot carry that: it is one truncated
            // line, and the row's own name lost the type entirely until DescribeCall grew a spawn
            // branch. These are STANDING FACTS, unchanged for the child's whole life, so they belong
            // where they can be read at leisure rather than glanced at.
            //
            // The MODEL earns its line because a type may name its own provider: a worker running
            // somewhere other than the session's model is a thing the user has no other way to learn,
            // and by the time the row finishes the provider is gone.
            var facts = new List<string> { $"  type: {child.TypeName}" };
            if (!string.IsNullOrWhiteSpace(child.ModelId))
                facts.Add($"  model: {child.ModelId}");

            // WHAT IT LOADED, and this is the line the row earns most. A child's context is invisible
            // by design, so a skill it chose is the one thing shaping its answer that the parent
            // cannot otherwise learn — and the load itself appears in the recent-tools list below for
            // six calls before scrolling away for good.
            //
            // CAPTURED INTO THE ENCLOSING LOCAL, not just rendered. The finished row is built after
            // SendAsync returns and cannot read live state — this section argues the child's context
            // is gone by then, and a finished row reading it would depend on the very thing it says
            // has vanished. childTurns is captured for the same reason.
            //
            // NOT A STANDING FACT, unlike type and model: skills ACCUMULATE mid-run, so the line
            // appears only once something is loaded rather than sitting empty — the same reason
            // occupancy renders as "" until the provider first reports usage.
            childSkills = child.Agent.LoadedSkills;
            if (childSkills.Count > 0)
                facts.Add($"  skills: {Clip(string.Join(", ", childSkills), 60)}");

            // ITS TASK, the first line in full. The prompt is the parent's own words and is often a
            // page long — the first line is what the parent MEANT, and the rest is detail the row
            // cannot hold, but that first line is shown whole rather than clipped: cutting it short
            // can drop the very word that distinguishes this run from a sibling's. Shown only when it
            // says something the row's name does not already.
            if (!string.IsNullOrWhiteSpace(childPrompt))
            {
                var first = childPrompt!.Split('\n', 2)[0].Trim();
                if (first.Length > 0) facts.Add($"  task: {first}");
            }

            // The live counters repeat the header ON PURPOSE here: an expanded row is tall enough
            // that the header may be scrolled out of view, and these are the numbers being watched.
            facts.Add($"  {turns} turn{(turns == 1 ? "" : "s")}{occupancy}{age}");

            job.ProgressBody = recent.Count > 0
                ? string.Join("\n", facts) + "\n\n" + string.Join("\n", recent)
                : string.Join("\n", facts);

            _jobs.ToolProgressed(job);

            // AND THE SESSION READOUT. Raised from Report rather than from the child's TurnCompleted
            // because Report is also driven by the one-second tick — a child that spends four minutes
            // inside a single turn completes no turns to hang this on, which is precisely the run
            // whose spend the panel was missing.
            ChildSpend?.Invoke();
        }

        var ctx = new JobContext(agentId, jobId, new Dictionary<string, JobResult>(), _logs)
        {
            // Rides with every tool call this agent makes, so the gate can say who is asking.
            Requester = _requesterLabel,

            // AND WHERE IT WORKS, so a relative path resolves against THIS agent's folder rather
            // than the process's. Identical today, because one process runs one session from the
            // directory it was launched in — and silently different the moment that stops holding.
            WorkingDirectory = TryGetWorkingDirectory(),
        };

        // THIS AGENT MARKS ITSELF while one of its calls sits at a prompt. Set on the agent rather
        // than routed from the shared gate, which knows only a display label — two children of the
        // same type would be indistinguishable to it, while each agent knows whether it is the one
        // waiting. A parent's row timer reads the flag and says so.
        ctx.PermissionWaitChanged += waiting => IsWaitingOnPermission = waiting;

        // THE CLOCK RESTARTS WHEN THE WORK DOES. `started` above is stamped when the ROW appears,
        // which is before the permission gate has asked the user anything — so a command the user
        // took four minutes to approve reported four minutes of runtime. Seen live: a shell call
        // whose own timeout fired at 15s rendered as `failed · 270.8s`, a number that sends whoever
        // reads it hunting for a slow command that never existed.
        //
        // Rebasing rather than replacing: an executor that never reports (no gate, no
        // WorkStarting call) keeps the original stamp.
        //
        // THE SHAPE TO RECOGNISE, because it splits into two independent bugs that each look whole:
        // the two ends of this one feature read two different clocks, on two different schedules. A
        // JobResult exists ONLY at completion, so the finished row's duration is a number computed
        // once at the end; StartedAt is stamped at row CREATION and read continuously by the live
        // header. Nothing connects them, so fixing the duration here leaves the clock beside it
        // counting the same review time — which is why both consumers are handled below.
        //
        // BOTH CONSUMERS, and that is the second half of this fix. `started` feeds JobResult.Duration
        // — the number on the FINISHED row — and rebasing it alone left job.StartedAt still holding
        // the row-creation stamp. That field is what the LIVE clock in the running header reads, so
        // the same review time this comment says was removed from the finished row was still being
        // counted by the clock ticking beside it: the two disagreed, and the one the user watches
        // while they wait was the wrong one. Whatever the rule is, it has to be the same rule in
        // both places, so they are set together here.
        ctx.WorkStarted += () =>
        {
            started = DateTimeOffset.UtcNow;
            job.StartedAt = started;
        };

        // THE BADGE, THE MOMENT THE CLASSIFIER RULES — not when the tool finishes. The row already
        // exists by here (it was drawn when the model emitted the call); the gate decides next, and
        // only then does the tool run. Reading the decider off job.Result meant waiting for all
        // three, and a JobResult exists ONLY at completion — so a `dotnet build` the classifier
        // approved showed nothing for the minutes it ran, and an auto-DENIED call, which never
        // executes, had no result to badge from at all.
        //
        // ToolProgressed, NEVER ToolUpdated. That method force-expands the row and blanks its body
        // on every call — right for a real state transition, ruinous for a header-only stamp on a
        // row a user may have collapsed, and it would erase a ProgressBody a later step streams in.
        // See IToolObserver.ToolProgressed.
        //
        // The stamp lives on the Job, so it survives onto the finished row below without the
        // completion path having to re-derive it.
        ctx.DeciderReported += decidedBy =>
        {
            job.DecidedBy = decidedBy;
            _jobs.ToolProgressed(job);
        };

        // "reviewing…" WHILE THE CLASSIFIER IS OUT, for the identical reason the badge above rides
        // the Job rather than waiting for Result: the classifier is two-stage with a fresh deadline
        // per stage, so on a local model this can run for many seconds, and the row otherwise shows
        // nothing in that gap — indistinguishable from a hung tool. Cleared the moment the verdict
        // lands (ReviewingChanged fires false right before/around DeciderReported firing true) or
        // the request falls through to a user prompt, in which case ReportPermissionWait's existing
        // waiting-row mechanism takes over; the two never show at once because the gate always
        // stops reviewing before it either returns a verdict or falls through to the prompt.
        ctx.ReviewingChanged += reviewing =>
        {
            job.Reviewing = reviewing;
            _jobs.ToolProgressed(job);
        };
        // MCP FIRST, then the built-ins. TryInvokeAsync returns null for a name no server owns, so
        // ToolBindings's "no such tool" text stays the single message for a name nobody owns — two
        // sources each producing their own version is how a model gets told a tool does not exist by
        // one and nothing by the other.
        //
        // Inside the job wrapper deliberately: an MCP call gets the same transcript row, the same
        // result rendering and the same ct as a built-in. Composed around it, a slow server would
        // show a frozen spinner with no indication of what was being waited on.
        // THE OUTCOME, NOT JUST ITS TEXT. `job.Result` below starts FROM the executor's own result
        // rather than being rebuilt from this string — see ToolOutcome for why that rebuild was a
        // defect rather than a shortcut. Text-only sources (spawn, skills, todos, ask_user, MCP)
        // carry a null Result and lose nothing: their text is the whole answer they ever had.
        ToolOutcome outcome;
        try
        {
            // SPAWN FIRST, then MCP, then the built-ins — each returning null for a name it does not
            // own, so the chain is one ?? per source and "no such tool" stays ToolBindings's single
            // message. Spawn leads because it is the only name that could otherwise be shadowed: an
            // MCP server is free to advertise anything.
            // GATED ON CanSpawn, NOT on _spawner alone. Without the mode check a model that saw
            // task in an earlier fan-out turn could still call it by name after a switch to
            // single, and the branch would happily run a child the user had just turned off.
            // WITHHELD IS NOT UNKNOWN, and this is the only place that can tell them apart. Each
            // link below answers for its own names and returns null otherwise, so the chain ends at
            // ToolBindings's "no such tool" — which is honest for a name nobody owns and WRONG for
            // one this agent simply was not offered. The distinction matters to the model: "no such
            // tool" should make it pick a real one, while a configuration fault should make it STOP
            // rather than retry variations.
            //
            // ABOVE THE CHAIN, NOT INSIDE A LINK. Before this, only built-ins consulted the
            // selection on dispatch (ToolBindings's own guard) — so a withheld skill, todowrite,
            // ask_user, agent or injected tool was hidden from the offer and still callable by name.
            // A model that saw `agent` in an earlier turn would call it and it would run.
            //
            // MCP IS NOT IN _offeredNames' EXCLUSION because it does not need to be: MCP tools
            // bypass selection entirely, so they are always in the offered set and never match this.
            // Text(...) ON THE STRING-ONLY LINKS, because `??` needs one type across the chain and
            // `string?` does not implicitly become `ToolOutcome?` — a user-defined conversion is not
            // considered when lifting a nullable operand. It is a wrapper, not a decision: these
            // sources genuinely have no JobResult, and null still means "not my name, try the next".
            // ONE COMPUTATION FOR BOTH LINKS BELOW. The injected link asks whether a built-in owns
            // this name and the terminator dispatches from the same set; deriving them separately is
            // how they come to disagree about a selection composed once.
            var allowedBuiltins = AllowedBuiltins(_turnTools);

            if (Withheld(call.Name) is { } refusal) outcome = refusal;
            else
            outcome = Text(CanSpawn ? await _spawner!.TryInvokeAsync(call, OnChildSpawned, ct, Id, _turnTools) : null)
                // SKILLS BEFORE MCP, for the same reason spawn leads: a server is free to advertise
                // any name, and a skill load answered by an MCP server would be silently wrong.
                // Reads _context.Messages — the agent's own conversation, which is what lets it
                // answer "already loaded" without keeping state that could drift from the window.
                ?? Text(_skills.TryInvoke(call, _context.Messages))
                // The plan is this agent's own state, so it resolves here rather than in an executor —
                // and the event fires only on a real write, not on every call that missed.
                ?? Text(TryUpdateTodos(call))
                // Null when there is no user — the call then falls through to "no such tool", which
                // is the honest answer for a tool this agent was never offered.
                ?? Text(_askUser is null ? null : await _askUser.TryInvokeAsync(call, ct))
                // _requesterLabel, so an MCP prompt names the child that wants it — the same value
                // JobContext carries into the executor path a few lines below.
                //
                // ROW 8 OF THE COLLISION MATRIX LIVES HERE, not at the injected link below: an MCP
                // server connects after config is read and can advertise any name, so MCP resolving
                // FIRST is the only way this dispatch order can even be asked "did MCP just take a
                // name an injected tool owns?" — by the time the injected link would run, the answer
                // is already baked into whether this line returned null. ReportMcpWonOverInjected
                // checks Knows(call.Name) itself, so the ordinary case (no injected tool has this
                // name) says nothing.
                ?? Text(ReportMcpWonOverInjected(call.Name,
                        _mcp is null ? null : await _mcp.TryInvokeAsync(call, ct, _requesterLabel, _policy)))
                // INJECTED TOOLS IMMEDIATELY BEFORE THE TERMINATOR. ToolBindings.InvokeAsync
                // answers "no such tool" rather than null, so it ENDS this chain — a link placed
                // after it never runs at all, and looks perfectly correct while never running.
                //
                // A LIVE BUILT-IN KEEPS ITS NAME, and only a live one. This link runs BEFORE the
                // built-ins, so an injected `read_file` would otherwise win a name the model was
                // told it has — the model calls read_file, reaches something else, and nothing
                // downstream can tell. Skipping the injected tool when a built-in of that name is
                // OFFERED is what stops that.
                //
                // OFFERED, NOT MERELY EXISTING, and the distinction is the whole rule. A user who
                // disables write_file through tool selection has freed the name: nothing offers it,
                // so nothing is shadowed, and an injected tool is entitled to it. That is the escape
                // hatch the selection grammar exists to provide, and a check written against the
                // built-in ENUM rather than the offered set would deny it.
                //
                // COMPUTED PER REQUEST, from the same composed selection the terminator is handed
                // one line below, so the two cannot disagree about what is offered this turn.
                //
                // REPORTED ONLY WHEN THE SHADOW ACTUALLY TAKES SOMETHING. Knows(call.Name) is what
                // tells the ordinary case — no injected tool has this name, so nothing was taken —
                // apart from a per-request selection (S3) reopening a built-in an injected tool DID
                // register for: that is row 7 of the collision matrix, knowable only here, at
                // dispatch, since S3 does not exist until this call is composed. Guarded by
                // ShadowsLiveBuiltin too so the message names the actual reason, not just "collided".
                ?? (_agentTools is null || ShadowsLiveBuiltin(call.Name, allowedBuiltins)
                        ? ReportShadowedInjectedTool(call.Name, allowedBuiltins)
                        : await _agentTools.TryInvokeAsync(call, ctx, ct))
                ?? await ToolBindings.InvokeAsync(call, allowedBuiltins, _executors, ctx, ct, _mcp?.Names());
            // No Text() on the last two: they return the executor's own ToolOutcome, which is the
            // whole point of the chain's type — everything below builds job.Result from it.
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
            // ToolBindings.InvokeAsync already holds this contract for built-ins; this extends it to
            // the two sources that did not — the spawn branch and _mcp.TryInvokeAsync.
            //
            // NOTE THE FALL-THROUGH RATHER THAN A RETURN. An early `return ErrorEnvelope(ex)` reads
            // naturally and is exactly wrong: it leaves the method before the messages.Add that the
            // result exists to become, producing the orphan this catch was written to prevent.
            //
            // A FAILED SPAWN KEEPS THE ENVELOPE SHAPE. The parent's model was told to expect
            // <sub_agent id state>; handing it a bare "error:" string for the one case that matters
            // most means the failure arrives in a shape it was never told about.
            outcome = childId is null
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
            _jobs.ToolUpdated(job);

            // CANCELLED IS AN OUTCOME, not an absence. A tool the user stopped is a fact about how
            // the session went, and dropping it here would make Escape invisible in history while
            // leaving its cost in the totals.
            ToolCallFinished?.Invoke(new ToolCallReport(
                CallId: jobId,
                AgentId: Id,
                ToolName: call.Name,
                JobType: job.JobType,
                Outcome: "cancelled",
                DurationMs: (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                ResultChars: 0,
                StartedAt: started)
            {
                Target = job.DisplayName,
            });

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

        // THE STRING, once, for everything below that reasons about the text the model was told —
        // failure sniffing, the error message, the envelope's state, the recorded length.
        var result = outcome.Text;
        var failed = LooksLikeFailure(result);
        job.State = failed ? JobState.Failed : JobState.Succeeded;
        job.CompletedAt = DateTimeOffset.UtcNow;

        // A FINISHED CHILD'S HEADER STATES THE COST, not its last live tick. While running, the
        // header answers "is it alive"; once done, that question is settled and the numbers that
        // remain interesting are what the run took. Leaving the ticking line in place also reads as
        // though it were still going — the elapsed figure simply stops, which looks like a freeze
        // rather than a finish.
        if (childId is not null)
        {
            var took = DateTimeOffset.UtcNow - started;
            var duration = took.TotalMinutes >= 1
                ? $"{(int)took.TotalMinutes}m{took.Seconds:00}s"
                : $"{took.TotalSeconds:0}s";

            // WHAT IT COST, on the header. The session panel says what all workers spent together;
            // only here can a user see that THIS planner cost 41k while that explore cost 3k — which
            // is the comparison that decides whether a type is worth spawning again.
            var (spentIn, spentOut) = spawned?.Agent.Spend ?? (0, 0);
            var cost = spentIn + spentOut > 0 ? $" · {spentIn + spentOut:N0} tokens" : "";

            job.ProgressMessage = $"{(failed ? "failed" : "done")} · {duration}{cost}";

            // AND THE FULL ACCOUNT IN THE BODY, which survives the row being collapsed and is what
            // a run is read back from later. The live turn counter is replaced rather than joined:
            // once finished, "3 turns" is a fact about the run, not a thing still moving.
            if (spawned is not null)
            {
                var account = new List<string> { $"  type: {spawned.TypeName}" };
                if (!string.IsNullOrWhiteSpace(spawned.ModelId))
                    account.Add($"  model: {spawned.ModelId}");
                if (!string.IsNullOrWhiteSpace(childPrompt))
                {
                    var first = childPrompt!.Split('\n', 2)[0].Trim();
                    // UNCLIPPED, so the finished account matches what the live caption already
                    // showed — see the facts block above for why a shortened task line is the wrong
                    // trade.
                    if (first.Length > 0) account.Add($"  task: {first}");
                }
                // THE SKILLS IT LOADED, from the captured copy rather than a live read: by here the
                // child has finished and this section's own argument is that its context is gone.
                // A live read would depend on the thing that has vanished.
                //
                // ITS OWN LINE IN THIS LIST because ProgressBody is REPLACED here, not appended to —
                // a change made only to `facts` above would pass a live-row test and silently drop
                // this from the finished row, which is the surface that outlives the run.
                if (childSkills.Count > 0)
                    account.Add($"  skills: {Clip(string.Join(", ", childSkills), 60)}");

                // NO DURATION HERE. The row's own header already states it (ProgressMessage above,
                // and the UI header's own DurationSuffix reading the same JobResult.Duration) — a
                // second copy in the caption agreed with it only until the two were touched by
                // different code, which is exactly the defect a caption sitting beside a header is
                // supposed to avoid repeating.
                account.Add($"  {childTurns} turn{(childTurns == 1 ? "" : "s")}");
                if (spentIn + spentOut > 0)
                    account.Add($"  tokens: {spentIn + spentOut:N0}  ↑{spentIn:N0} ↓{spentOut:N0}");

                job.ProgressBody = string.Join("\n", account);

                // AND TO HISTORY, once. The row above is for a user reading this session; this is for
                // a user asking "is planner worth spawning" — a question one session cannot answer.
                ChildFinished?.Invoke(new ChildRunReport(
                    RunId: childId,
                    ParentAgentId: Id,
                    TypeName: spawned.TypeName,
                    ModelId: spawned.ModelId,
                    InputTokens: spentIn,
                    OutputTokens: spentOut,
                    Turns: childTurns,
                    // The child's own panel already holds every row it drew — that is what keeps them
                    // out of the parent's transcript — so this is a read, not new bookkeeping.
                    ToolCalls: spawned.Jobs.Jobs.Count,
                    // THE ENVELOPE'S OWN WORD, not a two-way failed/completed guess. A capped run —
                    // seen live: an explore child burned all 30 turns hunting a JSON schema that is
                    // not published anywhere — is neither. Recording it as "completed" would put a
                    // wasted run in the success column, which is exactly the run worth finding later.
                    Outcome: SubAgentEnvelope.StateOf(result) ?? (failed ? "failed" : "completed"),
                    StartedAt: started,
                    DurationMs: (long)took.TotalMilliseconds)
                {
                    // From the captured copy, like the row above: the child has finished and its
                    // context is gone. Null rather than "" when nothing was loaded, so a later query
                    // can tell "loaded nothing" from "ran before skills existed".
                    Skills = childSkills.Count > 0 ? string.Join(", ", childSkills) : null,
                });
            }
        }
        // FROM THE PLUGIN'S OWN RESULT, overriding only what this method genuinely owns. Building a
        // NEW JobResult from the returned string — which is what this did — split every field in
        // two: the ones Agent can re-derive survived, and the ones only the executor knows (Output,
        // DecidedBy, LogFile) were silently dropped. That is not a hypothetical: TWO side channels
        // existed solely to smuggle values back past it. Seen live: a tool row displayed the
        // model's one-line confirmation where the tool's own rendered output belonged, and a
        // classifier-approved `du -sh . 2>&1 | tail -1` reached the DB and /stats correctly while
        // its row rendered plain "done". Starting from the object ends the category rather than
        // adding a third channel for the next field.
        //
        // THE OVERRIDES ARE THE FIELDS AGENT REALLY DOES OWN. Success/ExitCode come from
        // LooksLikeFailure over the model-facing TEXT, which is broader than an executor's own verdict —
        // a spawn envelope or an MCP refusal is a failure with no JobResult behind it at all. And
        // Duration is Agent's stopwatch, rebased by ctx.WorkStarted so it measures the WORK rather
        // than how long a user took to approve it; ShellJobExecutor's own copy would not be.
        //
        // The null branch is the text-only sources — spawn, skills, todos, ask_user, MCP — which
        // never had a result to start from. Output stays the returned text there, exactly as before.
        job.Result = (outcome.Result ?? new JobResult
        {
            Success = !failed,
            Output = new Dictionary<string, object?> { ["content"] = result },
        }) with
        {
            Success = !failed,
            ExitCode = failed ? -1 : 0,
            Duration = DateTimeOffset.UtcNow - started,
            ErrorMessage = failed ? result : null,
        };
        _jobs.ToolUpdated(job);

        // EVERY TOOL CALL, at the single exit both spawns and ordinary tools pass through. Raised
        // after the row is closed so a subscriber that throws cannot leave a row open — and the
        // result's LENGTH rather than its content, because this is a measurement, not an archive:
        // storing tool output would duplicate the transcript and the logs at once.
        ToolCallFinished?.Invoke(new ToolCallReport(
            CallId: jobId,
            AgentId: Id,
            ToolName: call.Name,
            JobType: job.JobType,
            // DENIED IS NOT FAILED. A refusal never RAN, and a reader looking at a list of calls
            // needs the two apart: a run full of denials means the worker was fighting the user's
            // permission settings, a run full of failures means its commands were broken. Reported
            // as one word, they send someone debugging the wrong thing.
            //
            // Read off outcome.Result, which is the executor's own verdict and the same object
            // job.Result is built from a few lines above — so the word here and the row on screen
            // cannot disagree.
            Outcome: outcome.Result?.PermissionDenied == true ? "denied"
                : failed ? "failed" : "succeeded",
            DurationMs: (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
            ResultChars: result.Length,
            StartedAt: started)
        {
            // The row's own label, so the list of calls names what each one acted on rather than
            // repeating a tool name nine times.
            Target = job.DisplayName,
        });

        return result;
    }

    /// <summary>
    /// A text-only dispatch answer as a <see cref="ToolOutcome"/>, preserving null.
    ///
    /// <para>NULL IN, NULL OUT, and that is the whole contract: the dispatch chain reads null as
    /// "this source does not own the name, try the next link". An empty string is a source that
    /// answered with nothing, which is a different fact.</para>
    /// </summary>
    private static ToolOutcome? Text(string? text) => text is null ? null : new ToolOutcome(text);

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
                    _sink.AssistantTextAppended(turnId, visible[shown..]);
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
                // THE AGENT SAYS WHAT KIND OF TEXT THIS IS; the sink decides how it looks. Building
                // the markup here would put a colour decision inside the turn loop, and would leave
                // the sink unable to tell styled reasoning from unstyled body text arriving on the
                // same method — so only one of the two would ever get escaped.
                //
                // The semantic decision that DOES belong here: reasoning is worth showing at all.
                // ChatTranscriptControl clears a message's thinking flag as soon as body content
                // arrives, so this costs the spinner — the right trade, because the reasoning text
                // is better evidence the model is alive than a spinner is: it says WHAT it is doing.
                var reasoning = ModelOutput.ExtractReasoning(accumulated);
                if (reasoning.Length > shownReasoning)
                {
                    _sink.AssistantReasoningAppended(turnId, reasoning[shownReasoning..]);
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
    /// Puts the model's task list at the newest end of the conversation, as the one message marked
    /// <see cref="ChatMessage.IsTaskList"/>.
    ///
    /// <para>NEWEST, NOT IN THE SYSTEM MESSAGE. Everything before the first changed byte is served
    /// from the provider's prefix cache, so a plan that lives in the prefix re-processes the entire
    /// context every time a marker flips. At the end it costs nothing, and it is also the last thing
    /// the model reads before answering — which is where an instruction lands hardest.</para>
    ///
    /// <para>REPLACED, NEVER APPENDED. <c>_context.Messages</c> is the agent's persistent list, not a
    /// per-turn copy, so appending would leave a trail of stale plans for the model to reconcile.
    /// Found by PROPERTY: matching rendered text would delete a user message that quoted the plan
    /// back.</para>
    ///
    /// <para>EMPTY REMOVES IT. A cleared list must not leave a stale plan behind, and a session that
    /// never plans keeps a context with no task-list message in it at all.</para>
    /// </summary>
    private void PlaceTaskList()
    {
        var messages = _context.Messages;
        var rendered = _todos.Render(toolOffered: _offeredNames.Contains(Jobs.Tool.TodoWrite));

        // NOTHING TO DO IF NOTHING MOVED, and the reason is the prefix cache. A provider serves
        // everything up to the first changed byte from cache and reprocesses the rest, so rewriting
        // the tail costs real time even when the new tail is identical to the old one. This method is
        // called on every turn that ends without tool calls, not only when a todo changes, so without
        // this guard an unchanged plan re-writes the newest message every single turn.
        //
        // ALREADY LAST AND ALREADY EQUAL is the only case worth skipping: a list that is present but
        // NOT last still has to move, which is the whole point of the method.
        if (messages.Count > 0
            && messages[^1].IsTaskList
            && string.Equals(messages[^1].Content, rendered, StringComparison.Ordinal))
            return;

        messages.RemoveAll(m => m.IsTaskList);

        if (string.IsNullOrWhiteSpace(rendered)) return;

        // USER ROLE, NO ToolCallId. Both compaction cut paths (SessionCompressor.SafeCut and
        // .Truncate) walk on `ToolCallId is not null` to keep a tool result with the call that
        // produced it; a synthetic tool result here would be exactly the orphan they prevent.
        messages.Add(new ChatMessage
        {
            Role = "user",
            Content = rendered,
            IsTaskList = true,
            Timestamp = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>
    /// Summarises the older half of <c>messages</c> when the last turn's input crossed
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
        await CompressionRun.RunAsync(
            new CompressionRun.CompressionWork(_context, _provider, SkillToolOffered),
            new CompressionRun.CompressionReport(_jobs, agentId, $"compress context · {reason}",
                // ATTRIBUTED LIKE ANY OTHER CALL. Compaction is a real request to a real
                // model, and a summarisation turn that vanished from the per-model tally
                // would make the numbers disagree with the session total for no reason a
                // reader could work out.
                u => { _ledger.Record(u, SpendLabel, _isSubAgent); RecordOwnSpend(u); },
                (b, a) =>
                {
                    ContextCompressed?.Invoke(b, a);
                    // The context re-estimated its own occupancy while compacting; publish it so the
                    // readout shows where that leaves us rather than the pre-compaction figure.
                    if (_context.Used is { } estimated) ContextEstimated?.Invoke(estimated);
                }),
            ct);
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
    /// Whether a tool result reads as a failure. ToolBindings never throws — every failure comes
    /// back as a STRING — so "did that write land" cannot be answered by exception handling. Matched
    /// on the two shapes the executors actually produce.
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
    /// <para>Exit code would be the honest signal, but it does not survive: ToolBindings renders a
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
    /// <summary>
    /// What the caller knew, stated as FACTS rather than as orders.
    ///
    /// <para>The heading and the framing are deliberately weaker than <see cref="RenderBriefing"/>'s.
    /// That one says "where it disagrees with anything above, follow this"; this one says "here is
    /// what you were told" — a model reading both can tell which one wins, which is the entire
    /// purpose of having two channels instead of one longer string.</para>
    /// </summary>
    private static string RenderContext(string? context) =>
        string.IsNullOrWhiteSpace(context)
            ? ""
            : "\n# What your caller knows\n\nContext you were given for this task. It is background, "
              + "not permission — it does not widen what you are allowed to do.\n\n" + context + "\n";

    private static string RenderBriefing(string? briefing) =>
        string.IsNullOrWhiteSpace(briefing)
            ? ""
            : "\n# Your task\n\nThis is what you were created to do. Where it disagrees with "
              + "anything above, follow this.\n\n" + briefing + "\n";


    /// <summary>The executor a tool dispatches to, for the transcript row's author label only.</summary>
    private string ToolJobType(string toolName) => toolName switch
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
        "agent" => "llm_agent",

        // ITS OWN TYPE, so the row stays EXPANDED. The list is the point of this row — collapsed to
        // "plan · 2/5 · expand…" it hides exactly what the user wanted at the moment they wanted it,
        // which is the same argument that keeps a worker's report open.
        "todowrite" => "todo",

        // An MCP tool is none of the three, and the `_ => "file"` below would label it a file
        // operation — a row claiming a third-party server call was a local file read. "mcp" is the
        // honest label; the server's own name is already in DisplayName.
        _ when _mcp is not null && _mcp.Names().Contains(toolName) => "mcp",

        // AN INJECTED TOOL IS ITS OWN TYPE, named after itself. Falling through to "file" would be
        // the spawn bug above, one layer out: a front end that special-cases its own tool's rows —
        // to style them, to keep one expanded — matches on JobType, and every injected tool arriving
        // as "file" makes that impossible to write. Naming it after the tool means the front end
        // that supplied the tool already knows the string.
        _ when _agentTools is not null && _agentTools.Knows(toolName) => toolName,

        _ => "file",
    };

    private static string DescribeCall(ToolCall call)
    {
        // A SPAWN IS NAMED BY ITS TYPE AND ITS DESCRIPTION, not by raw JSON. The generic branch below
        // truncates the serialised arguments at 60 characters, and a spawn's JSON opens with
        // `{"description":"…` — so the description ate the budget and `type`, which serialises last,
        // was ALWAYS cut off. The row could not say whether it was an explore, a planner or a
        // general agent, which is the first thing anyone wants from it.
        if (string.Equals(call.Name, "agent", StringComparison.Ordinal))
        {
            var type = ReadArg(call, "type");
            var what = ReadArg(call, "description");
            // A CALL THAT NAMES NO TYPE IS `general` — that is what the catalog resolves it to, so
            // the row says the same thing. "agent" would be a third name for a state that already
            // has two, and the row exists to tell three concurrent workers apart.
            var name = string.IsNullOrWhiteSpace(type) ? AgentTypeCatalog.DefaultTypeName : type!.Trim();
            return string.IsNullOrWhiteSpace(what) ? name : $"{name} · {Clip(what!, 44)}";
        }

        // A SKILL LOAD IS NAMED BY THE SKILL. The generic branch would render
        // `skill {"name":"rtl-aware-development"}` — the JSON scaffolding is noise around the
        // one word that matters, and the row is the only place a user sees that a skill entered the
        // conversation at all.
        if (string.Equals(call.Name, "skill", StringComparison.Ordinal))
        {
            var skill = ReadArg(call, "name");
            return string.IsNullOrWhiteSpace(skill) ? "skill" : $"skill · {skill!.Trim()}";
        }

        // A PLAN IS NAMED BY WHERE IT STANDS. The generic branch would render the entire list as
        // JSON in a header clipped at 60 characters, which is the worst of both: too long to read
        // and too short to hold the plan. The counts go here, the list goes in the body.
        if (string.Equals(call.Name, "todowrite", StringComparison.Ordinal))
        {
            var items = TodoList.Parse(
                call.Arguments.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? call.Arguments
                    : call.Arguments.TryGetProperty("todos", out var t) ? t : default);

            if (items.Count == 0) return "plan · cleared";

            var done = items.Count(i => i.Status == TodoStatus.Completed);
            var current = items.FirstOrDefault(i => i.Status == TodoStatus.InProgress);

            // WHAT IT IS DOING NOW, when it has said. "3/7 · fix the guard" answers the question a
            // reader has mid-run; "3/7" alone only answers half of it.
            return current is null
                ? $"plan · {done}/{items.Count}"
                : $"plan · {done}/{items.Count} · {Clip(current.Text, 44)}";
        }

        var args = call.Arguments.ToString();
        return $"{call.Name} {Clip(args, 60)}";
    }

    private static string Clip(string text, int max) =>
        text.Length > max ? text[..max] + "…" : text;

    /// <summary>One string argument, or null. Tolerant by design: this feeds a display name, and a
    /// malformed call must still produce a row rather than throwing inside the turn loop.</summary>
    private static string? ReadArg(ToolCall call, string name)
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

    /// <summary>
    /// Runs an <c>todowrite</c> call and tells the UI, or returns null for someone else's tool.
    /// </summary>
    private string? TryUpdateTodos(ToolCall call)
    {
        var result = _todoTool.TryInvoke(call);
        if (result is not null) TodosChanged?.Invoke();
        return result;
    }

    /// <summary>
    /// Where this agent works — what it was given, or the process's own directory when it was given
    /// nothing.
    ///
    /// <para>THE FALLBACK IS FOR CALLERS WITHOUT AN OPINION, not a second source of truth. Anything
    /// that owns a session should pass one: two sessions in one process share a cwd and cannot each
    /// have their own by reading it.</para>
    /// </summary>
    private string? TryGetWorkingDirectory()
    {
        if (_workingDir is { Length: > 0 }) return _workingDir;

        try { return Directory.GetCurrentDirectory(); }
        catch (Exception) { return null; }
    }
}

namespace CxAgent.Core.Permissions;

/// <summary>
/// What is being asked for. <c>Mcp</c> is a call into a third-party MCP server — code we did not
/// write, running locally with the user's credentials, whose arguments follow a schema we cannot
/// interpret.
///
/// <para><c>Tool</c> is a consumer-injected <see cref="Jobs.IAgentTool"/>: may this embedder's
/// own tool run in this folder at all. It is deliberately NOT reused from the kinds above — "may
/// the deploy tool run here" is not a shell, file or http question, and a rule stored under one of
/// those names would misdescribe itself to anyone reading permissions.json. Note that answering it yes
/// never exempts the tool from its own <see cref="Jobs.IAgentTool.Gate"/>, which runs on every
/// call; the two are different questions and this kind only answers the first.</para>
///
/// <para><c>Plugin</c> is the load gate itself — see PLUGINS.md, "The load gate is the only boundary
/// Core can enforce": may this plugin's whole load set (see <see cref="Plugins.PluginIdentity"/>) run
/// in this session at all. It answers that ONE question, once, at load; it is not consulted again
/// per call the way <c>Tool</c> is — a plugin's own declared per-operation gates (PLUGINS.md, "The
/// plugin provides its own policy; Core enforces it") reuse <c>Tool</c>, because that is already the
/// vocabulary for "may this specific capability run", and a plugin's gated tool is exactly that
/// question asked by a different kind of contributor.</para>
///
/// <para>PERSISTED BY NAME (<c>JsonStringEnumConverter</c>), so adding a value is a one-way change to
/// permissions.json: an older binary reading a file containing an <c>"Mcp"</c> or <c>"Tool"</c> rule
/// throws, treats the whole file as empty — losing every rule and all folder trust — and clobbers it
/// on the next save. Acceptable, but a downgrade hazard worth knowing about.</para>
/// </summary>
public enum PermissionKind { Shell, FileRead, FileWrite, Http, Mcp, Tool, Plugin }

/// <summary>Per-folder trust-on-first-use state. Unknown (never asked) behaves as Untrusted for
/// the silent class — an unanswered question must never behave like a yes.</summary>
public enum TrustState { Unknown, Trusted, Untrusted }

/// <summary>One thing the user is being asked to allow. Display is what the prompt shows
/// (verbatim command / RESOLVED path / origin — plus env/working_dir for shell, when present);
/// AlwaysRule is EXACTLY what "Always" would persist, pre-computed here so the button text and
/// the stored rule can never diverge. NULL AlwaysRule = this request cannot be truthfully
/// generalised (e.g. a shell job carrying a custom env — Decisions §3): no Always button is
/// offered, and no stored rule ever matches it.
///
/// <para>SUBJECT IS THE THING ITSELF, when Display is not. For a shell command Display is decorated
/// for a reader — "<c>ls (in /repo)</c>" — and anything that PARSES the command needs it undecorated
/// or it sees a command called "ls (in". Defaults to Display, so every other kind is unaffected and
/// no existing construction site changes.</para></summary>
public sealed record PermissionRequest(PermissionKind Kind, string Display, string? AlwaysRule,
    string? Subject = null)
{
    /// <summary>
    /// The undecorated thing this request is about — the command, the path, the origin.
    ///
    /// <para>Falls back to <see cref="Display"/>, which is right for every kind whose display IS the
    /// subject. Only shell decorates, and only shell sets this.</para>
    /// </summary>
    public string What => Subject ?? Display;

    /// <summary>
    /// WHO IS ASKING — null for the session's own agent, a short label for a sub-agent.
    ///
    /// <para>OBSERVED ON A LIVE DRIVE. A child spawned to analyse a test failure asked to run shell
    /// commands repeatedly, and the prompt was INDISTINGUISHABLE from the parent asking: same
    /// heading, same command, nothing saying a delegated agent wanted this. With one foreground
    /// child that is merely unhelpful — the user knows what they started. It stops being cosmetic
    /// the moment two children run at once, because prompts follow the COMPOSER, not the view: you
    /// can be reading one child's transcript and approving another child's write, with nothing on
    /// screen to tell you so.</para>
    ///
    /// <para>AN INIT-ONLY PROPERTY, not a fourth positional field, so the ~15 construction sites in
    /// PermissionPolicy and the tests are untouched — the same reason McpInstructions was added that
    /// way. A caller that knows who is asking says so; nobody else changes.</para>
    /// </summary>
    public string? Requester { get; init; }

    /// <summary>What the classifier is shown about this action, or null when nothing was gathered.</summary>
    public ActionFacts? Facts { get; init; }

    /// <summary>
    /// WHICH SESSION IS ASKING — its working directory, and the edit mode it is running under.
    ///
    /// <para>THE QUESTION IS PER-SESSION; THE ANSWER IS NOT. The gate is one per process because
    /// stored rules and the prompt queue must be (a second gate would forget every rule the user
    /// granted in the other window, and two prompt queues would race for one composer). But the
    /// POLICY behind it is not process-wide at all: <see cref="PermissionPolicy"/> holds a working
    /// directory and a settable edit mode, both of which belong to one session.</para>
    ///
    /// <para>Captured in the gate, those two became shared by accident. With a second session in the
    /// process, one session's <c>/mode edits always-ask</c> would silently govern the other, and its
    /// in-boundary check would run against the wrong root — allowing a write to a checkout the user
    /// never approved, with every layer behaving correctly on the way. Invisible today only because
    /// AppBootstrap creates exactly one session.</para>
    ///
    /// <para>Carried ON THE REQUEST for the reason <see cref="Requester"/> is: an init-only property
    /// leaves every existing construction site untouched, and the one layer that sees both the
    /// request and the session it came from is the layer that sets it. Null means "use the gate's
    /// own" — the single-session path, unchanged.</para>
    /// </summary>
    public PermissionPolicy? Policy { get; init; }

    /// <summary>
    /// True when auto mode's classifier reviewed this and said no.
    ///
    /// <para>SO THE PROMPT CAN GIVE THE RIGHT REASON. Its heading was inferred from whether the path
    /// is inside the working folder, which has exactly one cause in the other modes — an untrusted
    /// folder — and so it said "(untrusted)" for every in-tree write that reached it. In auto mode
    /// there is a second cause, and the folder is usually trusted: the classifier refused. A user
    /// reading "in this (untrusted) folder" about a folder they trusted learns the wrong thing from
    /// the one line meant to explain why they are being asked.</para>
    ///
    /// <para>NOW SET FOR EVERY REVIEWED KIND, not only writes — an annotate-only request that got a
    /// verdict arrives at the prompt with this true too. PermissionPromptControl.HeadingFor still
    /// branches on it for the FILE kinds only, so an Http or Mcp heading is unchanged today. That is
    /// deliberate rather than an oversight: the flag records what happened, and deciding how an
    /// annotated egress prompt should READ is the prompt-shaping work, not this gate's.</para>
    /// </summary>
    public bool RefusedByClassifier { get; init; }

    /// <summary>
    /// True when the classifier said DENY and the effect let that denial stand — the action was
    /// refused outright, without a prompt.
    ///
    /// <para>DISTINCT FROM <see cref="RefusedByClassifier"/> BECAUSE THE OUTCOMES DIFFER. Refused
    /// means "a prompt is about to appear and the classifier is why"; denied means no prompt appears
    /// at all. Recording them under one flag would make a session where the user was asked look
    /// identical to one where they were never given the choice, and those are exactly the two facts
    /// a reader is trying to tell apart.</para>
    ///
    /// <para>ONLY UNDER <see cref="ReviewEffect.MayApprove"/>. A DENY on an annotate-only kind shapes
    /// the prompt like any other verdict; it does not get to decide.</para>
    /// </summary>
    public bool DeniedByClassifier { get; init; }

    /// <summary>
    /// Why the classifier answered as it did, when the model gave a reason — the text after the
    /// verdict in its completion, or null when it gave none.
    ///
    /// <para>THE VERDICT WITHOUT THE REASON IS THE PART THE USER CANNOT ACT ON. "auto review refused
    /// this" tells them the machinery ran; it does not tell them whether to override it. Carried on
    /// the request rather than passed alongside so the prompt, the transcript echo and anything added
    /// later all read one field instead of threading a second argument through each layer.</para>
    ///
    /// <para>MODEL-AUTHORED TEXT, so any renderer must treat it as content and never as markup — the
    /// same handling <see cref="ActionFacts"/> gives the action text it embeds.</para>
    /// </summary>
    public string? ClassifierReason { get; init; }

    /// <summary>
    /// Told true when the gate is about to consult the classifier for THIS request, and false once
    /// that consultation ends — mirrors how <see cref="Requester"/> and <see cref="Policy"/> ride
    /// the request: the wrapper (PermissionGatedExecutor/GatedAgentTool) is the one layer that sees
    /// both the request and the job context, so it is the one that stamps this from
    /// <c>context.ReportReviewing</c>. Null on the test seams that build a request with no context
    /// behind it — <see cref="PermissionDecider"/> guards every call with <c>?.</c>.
    ///
    /// <para>ON THE REQUEST, NOT A GATE-LEVEL EVENT LIKE <see cref="PermissionDecider.OnWaiting"/>,
    /// because the gate is one per process and cannot tell which row's job context a given request
    /// belongs to — the request is the one object already carrying that specific context's callback
    /// down to where <c>JudgeAsync</c> is actually awaited.</para>
    /// </summary>
    public Action<bool>? OnReviewing { get; init; }
}

/// <summary>
/// What a human answered when asked.
///
/// <para>IN CORE BESIDE THE REQUEST, not with the control that renders it. This is the gate's answer
/// vocabulary — the decision pipeline is written in terms of it — and leaving it in UI meant the
/// pipeline could not move out of UI. A front end that is not a terminal returns these same four
/// without ever building a prompt control.</para>
/// </summary>
public enum PermissionChoice { Once, Always, Deny, TrustFolder }

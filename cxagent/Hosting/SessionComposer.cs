using CxAgent.Hosting;
using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;

namespace CxAgent.Hosting;

/// <summary>Where a session's words and tool activity go: the pair one front end shows it with.</summary>
public sealed record SessionSinks(ISessionObserver Observer, IToolObserver ToolObserver);

/// <summary>
/// What wiring a session needs from whoever shows it — a terminal tab, or the web server.
///
/// <para>ONLY THE PARTS THAT DIFFER BY FRONT END. The policy, the ports, the manager and the permission history
/// are the same for every front end and live in <see cref="SessionComposer"/>; a host that had to supply them
/// would be the second place they are written, which is how two paths drift (one once wired two of eleven
/// events).</para>
/// </summary>
public interface IFrontEnd
{
    /// <summary>The observers this session reports into. Called once per wire: a re-wire builds a new pair.</summary>
    SessionSinks SinksFor(Session session);

    /// <summary>How this session asks the user a question, or null for a front end with nobody to ask.</summary>
    AskUser? AskFor(Session session);

    /// <summary>The session is wired; subscribe whatever the front end shows about it.</summary>
    void Wired(Session session, SessionSinks sinks, PermissionPolicy policy);

    /// <summary>
    /// A wire has just restored a saved conversation: draw its past into this wire's <paramref name="sinks"/>. Nothing
    /// by default, for a front end whose watchers replay it themselves.
    /// </summary>
    void ShowRestoredPast(Session session, SessionSinks sinks, AppPaths paths) { }
}

/// <summary>What every session the composer wires shares with the others.</summary>
/// <param name="Manager">Opens the session and holds it beside the others.</param>
/// <param name="Paths">Where the logs and the config folder are.</param>
/// <param name="Rules">The permission store every session's policy is scoped against.</param>
/// <param name="Gate">The process's permission gate, or null when nothing records its decisions.</param>
/// <param name="History">Where permission decisions are recorded, or null for nowhere.</param>
public sealed record ComposerServices(
    SessionManager Manager,
    AppPaths Paths,
    PermissionRulesStore Rules,
    PermissionDecider? Gate,
    UsageHistoryStore? History)
{
    /// <summary>
    /// Where the sessions' questions are raced between front ends, or null when the front end asks for itself.
    /// </summary>
    public PromptHub? Prompts { get; init; }
}

/// <summary>What asking about a folder's trust came to.</summary>
public enum TrustOutcome
{
    /// <summary>Nothing was asked: decided already, or a folder that cannot be trusted.</summary>
    Known,

    /// <summary>Trusted now.</summary>
    Trusted,

    /// <summary>Not trusted now — everything in it asks.</summary>
    NotTrusted,

    /// <summary>The question was withdrawn before anyone answered; nothing was decided.</summary>
    Withdrawn,
}

/// <summary>The outcome, and why the answer could not be saved when it could not.</summary>
public sealed record TrustResult(TrustOutcome Outcome, string? SaveError = null);

/// <summary>One wire: which session, on which configuration, in which mode.</summary>
/// <param name="Session">The conversation being wired — new, or one being re-wired.</param>
/// <param name="Config">What it runs.</param>
/// <param name="Mode">How it starts.</param>
public sealed record WireRequest(Session Session, ResolvedConfig Config, WorkingMode Mode)
{
    /// <summary>
    /// The policy to keep, or null to build one scoped to the session's folder. A rebind keeps the tab's own,
    /// with its session-scoped grants; anything else starts fresh.
    /// </summary>
    public PermissionPolicy? Policy { get; init; }
}

/// <summary>
/// Wires sessions the same way whichever front end shows them.
///
/// <para>ONE ROUTINE FOR EVERY DOOR — the terminal's first session, a tab from <c>/sessions new</c>, a re-wire
/// after <c>/model</c> or a resume, and a session a browser opens. Written per door, each copy is a chance to
/// forget a port: the question hook, the model-facing command list, the classifier, the permission history.
/// Each of those fails silently when it is missing.</para>
/// </summary>
public sealed class SessionComposer(ComposerServices services)
{
    /// <summary>The shared services this composer wires with.</summary>
    public ComposerServices Services { get; } = services;

    /// <summary>Opens a new session in <paramref name="folder"/> and wires it for <paramref name="frontEnd"/>.</summary>
    public Session Create(string folder, ResolvedConfig config, WorkingMode mode, IFrontEnd frontEnd)
    {
        var session = new Session(folder);
        Wire(new WireRequest(session, config, mode), frontEnd);
        return session;
    }

    /// <summary>Wires — or re-wires — one session, and returns the policy it now runs under.</summary>
    public PermissionPolicy Wire(WireRequest request, IFrontEnd frontEnd)
    {
        var (session, config, mode) = (request.Session, request.Config, request.Mode);

        // THIS SESSION'S OWN, scoped to its folder and carrying its id. A policy carries the root it judges against
        // and the id decisions are filed under, so wiring session B with session A's policy would judge B's file
        // operations against A's folder. AND THE LOG TREE, so a backgrounded command's output — which the app
        // writes there and then tells the model to read — does not cost a prompt.
        var policy = request.Policy ?? new PermissionPolicy(session.WorkingDirectory, Services.Rules, mode.Edits)
        {
            SessionId = session.Id,
            LogDir = Services.Paths.LogsDir,
        };

        var sinks = frontEnd.SinksFor(session);

        // THROUGH THE HUB WHEN THERE IS ONE, so a question can be answered from any front end showing the session.
        var ask = Services.Prompts?.AskUserFor(session.Id) ?? frontEnd.AskFor(session);
        Services.Manager.Open(session, config, Ports(session, config, policy, sinks, ask) with
        {
            // THE PAST INTO THIS WIRE'S OWN SINKS, the tab that is about to show the session.
            Restored = () => frontEnd.ShowRestoredPast(session, sinks, Services.Paths),
        }, mode);

        // PERMISSION DECISIONS INTO HISTORY. Set on every wire because the session id does not exist until the
        // host does. BOTH FIELDS FROM THE REPORT, NOT THE CLOSURE: one gate serves every session, so a report can
        // arrive from any of them, and taking the folder from the session that wired last filed rows naming
        // session B beside session A's directory. The fallbacks cover a request that carried no policy, the only
        // case where the report cannot say.
        if (Services.Gate is { } gate && Services.History is { } history)
            gate.OnDecision = report =>
                history.SavePermission(new PermissionRecord(
                    report.SessionId ?? session.Id, DateTimeOffset.UtcNow,
                    report.Kind.ToString(), report.Decision, report.Requester,
                    report.Root ?? session.WorkingDirectory, report.Subject, report.Flagged));

        frontEnd.Wired(session, sinks, policy);
        return policy;
    }

    /// <summary>
    /// Asks whether to trust the session's folder, when nobody has decided yet, and saves the answer.
    ///
    /// <para>EVERY SESSION, NOT ONLY THE FIRST. Trust decides whether reads and read-only commands in the folder go
    /// through silently, and whether the folder's own hooks and plugins load; a second tab in a folder nobody
    /// decided about is the same question as the first.</para>
    ///
    /// <para>BEFORE PLUGINS LOAD — the caller's order. A plugin the folder declares runs code, and that is what the
    /// answer is about.</para>
    /// </summary>
    public async Task<TrustResult> AskTrustIfUnknownAsync(Session session, CancellationToken ct)
    {
        if (Services.Prompts is not { } hub) return new(TrustOutcome.Known);
        if (Services.Rules.GetTrust(session.WorkingDirectory) != TrustState.Unknown) return new(TrustOutcome.Known);

        // A FOLDER THE POLICY SAYS CANNOT BE TRUSTED — the home directory, the filesystem root — is not offered.
        if (session.Policy?.CanBeTrusted == false) return new(TrustOutcome.Known);

        var trusted = await hub.AskTrustAsync(session.Id, session.Policy?.TrustShown ?? session.WorkingDirectory, ct);
        if (trusted is not { } yes) return new(TrustOutcome.Withdrawn);

        var outcome = yes ? TrustOutcome.Trusted : TrustOutcome.NotTrusted;
        try
        {
            Services.Rules.SetTrust(session.WorkingDirectory, yes ? TrustState.Trusted : TrustState.Untrusted);
            return new(outcome);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // DECIDED, NOT SAVED: the answer holds for this run, and the caller says it will be asked again.
            return new(outcome, ex.Message);
        }
    }

    /// <summary>
    /// Loads the session's configured plugins for its own folder, saying what happens through <paramref name="say"/>.
    ///
    /// <para>NEVER THROWS. Loading spawns processes and may ask permission, and every caller fires it and moves on;
    /// a failure is said where the session's user will see it rather than thrown at a caller that has returned.</para>
    /// </summary>
    public async Task LoadPluginsAsync(Session session, ResolvedConfig config, Action<string> say, CancellationToken ct)
    {
        if (config.Plugins.Count == 0) return;
        try
        {
            // THE SESSION'S OWN FOLDER IS SEARCHED, so a project-local plugin belongs to the project that declares it.
            var folders = PluginDiscovery.SearchFolders(config.PluginPaths, session.WorkingDirectory, Services.Paths.ConfigDir);
            await PluginDiscovery.LoadConfiguredAsync(session, config.Plugins, folders, Services.Paths.ConfigDir, say, ct);
        }
        catch (Exception ex)
        {
            say($"could not load plugins: {ex.Message}");
        }
    }

    /// <summary>
    /// The ports a session is opened with.
    ///
    /// <para>NAMED IN ONE PLACE SO EVERY SESSION GETS THE SAME SEAMS — the embedder tool slot, the question hook, and
    /// the model-facing command list are each easy to omit when a call site is written from memory, and each fails
    /// silently when it is.</para>
    /// </summary>
    private SessionPorts Ports(Session session, ResolvedConfig config, PermissionPolicy policy, SessionSinks sinks, AskUser? ask) =>
        new()
        {
            Observer = sinks.Observer,
            ToolObserver = sinks.ToolObserver,

            // THE SEAM FOR EMBEDDER TOOLS, passed explicitly even while empty. This layer is where a tool that needs a
            // transcript, colour or folding would be supplied, because Core has none of the three.
            Tools = [],

            // NAMING THE SESSION THAT ASKS — the front end's hook is built for this session. A question has no
            // carrier the way a permission request has its policy, so the wiring supplies it.
            Ask = ask,

            // WHAT THE MODEL IS TOLD IT CAN SUGGEST, read per turn from the registry as it actually stands — after the
            // app has overridden Core's declarations and added its own.
            ModelFacingCommands = () =>
                [.. Services.Manager.Commands.All
                    .Where(c => c.TellTheModel)
                    .Select(c => (c.Name, c.Summary))],

            // REVIEWED BY ITS OWN CLASSIFIER. Auto mode asks a model whether an action is safe, and which model comes
            // from THIS session's config. Bound on the gate — one slot for the process — a second session's bind
            // replaced the first's, and one configured with no classifier cleared auto-review for everyone.
            Policy = Reviewing(policy, config),
        };

    private static PermissionPolicy Reviewing(PermissionPolicy policy, ResolvedConfig config)
    {
        policy.Classifier = PermissionDecider.ClassifierFor(
            config.ClassifierInstance, config.Providers, config.ClassifierTimeoutSeconds);
        return policy;
    }
}

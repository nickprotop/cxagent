using CxAgent.Core.Llm;
using CommandTable = CxAgent.Core.Commands.SessionCommands;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;

using CxAgent.Core.Commands;

namespace CxAgent.Core.Sessions;

/// <summary>
/// Owns a process's sessions and the services they share.
///
/// <para>WHY IT OWNS THEM. The shared services — a log manager, two Sqlite stores, a permission
/// gate — are a <see cref="SharedServices"/> record. Left as locals in the UI method that uses
/// them they would be owned by nothing, which works while there is one session and stops at two,
/// exactly as <see cref="Session"/>'s own doc says: "a local is one slot, so a second session
/// would need a second copy of a 1,400-line method".</para>
///
/// <para>SO THIS IS THE SECOND SLOT. It holds the collection and builds the shared half once. A
/// caller opens a session by naming a folder and handing over the two things only it can supply:
/// how the session should be observed, and how it should be judged.</para>
///
/// <para>MENTALLY THIS IS THE KERNEL, and the naming is deliberate. Everything here is per PROCESS
/// today; if sessions ever become processes, this becomes the thing they share, and the split is a
/// move rather than a rewrite because the categories are already separated.</para>
///
/// <para>ONE PROCESS-WIDE THING IS DELIBERATELY NOT OWNED HERE:
/// <see cref="Jobs.Builtin.FileMutation"/>'s per-path lock table. It is static, and that is the
/// point — a manager instance holding it would make "two managers" expressible, and two lock tables
/// serialise nothing. Everything this class owns is a service that could legitimately differ between
/// processes; that one is an invariant that must not.</para>
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly List<Session> _sessions = [];
    private readonly object _gate = new();
    private readonly bool _ownsServices;

    /// <summary>What every session in this process shares. Built once, handed to each.</summary>
    public SharedServices Shared { get; }

    /// <summary>The rules store behind the gate, for callers that read it directly — the settings
    /// page listing grants, the startup migration. Null when no store was built.</summary>
    public PermissionRulesStore? Rules { get; }

    /// <summary>
    /// Rebuilds a session's host over an armed resume. Null until a front end supplies one.
    ///
    /// <para>THE SAME LAYERING AS <c>buildGate</c>, and for the same reason: the ports a rewire needs
    /// — an observer, a tool sink, a way to ask the user — can only be built by a presentation layer,
    /// so Core takes the delegate rather than the ability. What Core owns is the SEQUENCE around it,
    /// which is the part that breaks when it is copied: arming the resume, re-wiring, retiring the
    /// row it came from and saying so are four steps that only work together.</para>
    ///
    /// <para>SET ONCE RATHER THAN PASSED PER CALL. As a parameter on <see cref="Resume"/> every
    /// caller would carry a closure over the same invariant value — the composition root passes
    /// <c>() =&gt; WireRunner(resolution)</c>, and <c>resolution</c> is single-assignment for the
    /// life of the process. A per-call parameter implies a variation that does not exist, and it
    /// would strand <c>/sessions resume</c> in the UI: a command cannot supply a callback only the
    /// root can build.</para>
    /// </summary>
    public Action? Rewire { get; set; }

    /// <summary>An empty environment, for the default config read — see Create's `config` param.</summary>
    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment =
        new Dictionary<string, string>();

    /// <summary>
    /// What this process runs unless a session says otherwise.
    ///
    /// <para>A VALUE, INCLUDING WHEN IT FAILED. ConfigResolver never throws — an unreadable or
    /// absent config.json comes back with HasProvider false and Errors filled in — so a caller that
    /// cares reads those and one that does not carries on. Hiding this inside Open would have taken
    /// the errors with it, and "no provider" without "why" is worse than no answer.</para>
    /// </summary>
    public ResolvedConfig Config { get; }

    private SessionManager(SharedServices shared, PermissionRulesStore? rules, bool ownsServices,
        ResolvedConfig? config = null)
    {
        Shared = shared;
        Rules = rules;
        _ownsServices = ownsServices;
        Config = config ?? new ResolvedConfig(null, ProviderCatalog.Empty, ["no configuration was resolved"]);

        SeedCommands();
    }

    /// <summary>
    /// Registers what Core can service on its own.
    ///
    /// <para>EACH ONE IS A SESSION METHOD THAT DOES, SAYS AND ANNOUNCES — see Session.ClearContext
    /// for the shape. The handler here is the lookup, not the logic: anything longer than a single
    /// call is a sign the work has not finished moving.</para>
    ///
    /// <para>A COMMAND WHOSE HANDLER IS NOT HERE YET STILL APPEARS IN THE TABLE, because the table
    /// is what a palette reads and a half-migrated command must not vanish from it. TryRun simply
    /// finds no entry and the caller falls through to its own dispatch.</para>
    /// </summary>
    private void SeedCommands()
    {
        foreach (var command in CommandTable.All)
        {
            switch (command.Name)
            {
                case "/clear":
                    Commands.Register(command, (session, _) => { session.ClearContext(); return true; });
                    break;

                // THE COMMANDS TABLE, WHICH IS CORE'S. A front end with keys of its own registers
                // over this and prepends them — last registration wins — but one with nothing to
                // add needs no help command at all.
                case "/help":
                    Commands.Register(command, (session, _) => session.ShowHelp().Handled());
                    break;

                case "/init":
                    Commands.Register(command, (session, _) =>
                        session.Initialise() is not Session.SubmitOutcome.NoAgent);
                    break;

                // REPORTING ONLY. Clearing needs a confirmation and a confirmation needs somebody to
                // ask, so a front end registers over this one — last registration wins.
                case "/stats":
                    Commands.Register(command, (session, arguments) => session.SayUsage(arguments).Handled());
                    break;

                case "/agents":
                    Commands.Register(command, (session, arguments) => session.ListAgentTypes(arguments).Handled());
                    break;

                case "/skills":
                    Commands.Register(command, (session, _) => session.ListSkills().Handled());
                    break;

                case "/diff":
                    Commands.Register(command, (session, arguments) => session.ShowDiff(arguments).Handled());
                    break;

                // LIST AND SHOW ONLY. reload and login are the host's — see SharedServices.McpStatuses
                // — and a process that registers neither still gets this half for free.
                case "/mcp":
                    Commands.Register(command, (session, arguments) => session.DescribeMcp(arguments).Handled());
                    break;

                // THE RESUME STORE IS THIS MANAGER'S, which is the whole argument for registering
                // here. A front end listing rows and restoring one through Shared.Resume does the
                // manager's job from outside it, with SessionsCommand already sitting in
                // Core/Commands and DefaultRetention already referenced above.
                //
                // GATED ON THE STORE, exactly as /stats is on History: a command that cannot work is
                // worse than one that is absent, because the user reads its silence as an answer.
                case "/sessions":
                    Commands.Register(command, (session, arguments) => session.ListSessions(arguments).Handled());
                    break;

                // EVERY INPUT IS THE SESSION'S OWN — see Session.SetMode(string). The session
                // reaches the rules store and the classifier flag itself: the policy for trust,
                // NoteCatalog for the flag, so the composition root has nothing to hand it.
                // BOTH INPUTS ARE THE SESSION'S — the catalog it was wired with and the instance it
                // is on. Registering this in the composition root instead would mean nine lines
                // reaching across for two values the session already holds.
                case "/model":
                    Commands.Register(command, (session, arguments) => session.UseFromInput(arguments).Handled());
                    break;

                case "/mode":
                    Commands.Register(command, (session, arguments) => session.SetMode(arguments).Handled());
                    break;

                // THE POLICY IS THE SESSION'S, exactly as /mode's is — the folder, the store and
                // the root all hang off it, so a front end registering this would be reaching
                // across for three values the session already holds.
                case "/trust":
                    Commands.Register(command, (session, arguments) => session.SetTrust(arguments).Handled());
                    break;

                case "/compress":
                    Commands.Register(command, (session, arguments) =>
                    {
                        // FIRE AND FORGET: compaction is a turn of its own and the caller does not
                        // wait for it. The session refuses and says so if one is already running.
                        session.CompressNow(CancellationToken.None);
                        return true;
                    });
                    break;
            }
        }
    }

    /// <summary>
    /// Builds the process's shared services from its config directory.
    ///
    /// <para>THE GATE IS A PARAMETER, not built here, and that is the layering. Logs and stores are
    /// Core's business; asking a human is not — the interactive gate needs a window, so only the
    /// presentation layer can construct one. A caller with no way to ask passes null and gets a
    /// session with no gating at all, which is an ordinary headless arrangement rather than a
    /// degraded one.</para>
    ///
    /// <para>THE RESUME BUFFER IS PRUNED ONCE, here, rather than on a timer: startup is the only
    /// moment nothing is mid-turn, and finished sessions are the only rows old enough to drop. The
    /// usage archive beside it is deliberately NOT pruned — it is the answer to "where did last
    /// month go", and pruning it on startup would delete that every time the app opened.</para>
    /// </summary>
    /// <param name="buildGate">
    /// Turns the rules store this manager owns into a gate. NOT a gate instance, because the gate
    /// needs the store and the store is built here — and not built internally either, because
    /// asking a human needs a window and Core has none.
    ///
    /// <para>A HOOK IS THE WHOLE UI DEPENDENCY. The interactive gate reduces to a store plus a
    /// prompt function; once it stopped holding a session's policy it stopped needing anything else,
    /// which is what makes this callable before any session exists. Null gives an ungated manager —
    /// ordinary for a headless host, and the reason DenyAll is not the default here: a caller that
    /// wants refusal can say so, and one that genuinely has nobody to ask should not have every
    /// operation silently fail.</para>
    /// </param>
    /// <param name="config">
    /// What this process runs unless a session says otherwise. Null reads config.json from
    /// <paramref name="paths"/>, which is the answer a caller would assume: the manager holds the
    /// config directory, so it can say what that directory contains without being told twice.
    ///
    /// <para>ENVIRONMENT VARIABLES ARE THE CALLER'S. The default read expands none — a config using
    /// <c>${VAR}</c> needs somebody to say which environment, and a manager reaching for the ambient
    /// one would make a test's result depend on the machine it ran on. A caller that wants expansion
    /// resolves explicitly and passes the result, which is also how --mock and --model arrive.</para>
    ///
    /// <para>RESOLVED EAGERLY, not on first read. A property that does file IO is a property that
    /// throws somewhere nobody expects, and the cost here is one File.Exists against a directory the
    /// caller just named.</para>
    /// </param>
    /// <param name="paths">Which directory config, the stores and the logs live in.</param>
    /// <param name="mcp">MCP servers to connect, or null for none.</param>
    public static SessionManager Create(AppPaths paths,
        Func<PermissionRulesStore, IPermissionGate>? buildGate = null,
        Mcp.McpToolset? mcp = null,
        ResolvedConfig? config = null) =>
        Create(new ProcessSetup
        {
            Paths = paths,
            BuildGate = buildGate,
            Mcp = mcp,
            Config = config,
        });

    /// <summary>
    /// Builds the process's shared services from its <see cref="ProcessSetup"/>.
    ///
    /// <para>THE NAMED FORM, and the one to prefer. The overload above takes the same four things
    /// positionally, which is how they arrived — one per feature — and is kept because thirty-odd
    /// callers pass only the first. Two of the four are nullable and two are delegates, so a caller
    /// passing them in the wrong order gets agreement from the compiler rather than an error.</para>
    /// </summary>
    public static SessionManager Create(ProcessSetup setup)
    {
        var paths = setup.Paths;
        var buildGate = setup.BuildGate;
        var mcp = setup.Mcp;
        var config = setup.Config;

        var resume = new SqliteSessionStore(paths);
        resume.Prune(SqliteSessionStore.DefaultRetention);

        var rules = new PermissionRulesStore(paths);

        return new SessionManager(
            new SharedServices
            {
                Logs = new LogFileManager(paths),
                Resume = resume,
                History = new UsageHistoryStore(paths),
                Gate = buildGate?.Invoke(rules),
                Mcp = mcp,
                GlobalInstructionsDir = paths.ConfigDir,
            },
            rules,
            ownsServices: true,
            config ?? ConfigResolver.Resolve(paths, EmptyEnvironment, useMock: false));
    }

    /// <summary>
    /// A manager over services somebody else built and owns.
    ///
    /// <para>For a caller that already assembled them — the composition root during the move to this
    /// type, and tests that want a manager over fakes. Disposing this one leaves them alone, because
    /// disposing what you did not create is how a store closes underneath its second user.</para>
    /// </summary>
    public static SessionManager Over(SharedServices shared, PermissionRulesStore? rules = null,
        ResolvedConfig? config = null) =>
        new(shared, rules, ownsServices: false, config);

    /// <summary>
    /// What this manager can offer for a named set — see <see cref="CompletionSets"/>.
    ///
    /// <para>THE MANAGER ANSWERS FOR WHAT IT OWNS: the resume store behind <c>/sessions resume</c>
    /// and the live MCP toolset behind <c>/mcp</c>. A session answers for its own — see
    /// <see cref="Session.Values"/> — and both return empty for a set they do not own, so a caller
    /// can ask each in turn without knowing which is which.</para>
    ///
    /// <para>NEVER THROWS. This runs on a keystroke inside layout, where an exception from a locked
    /// database would take down the composer rather than produce an empty menu.</para>
    /// </summary>
    public IReadOnlyList<CompletionValue> Values(string set, string? workingDirectory = null)
    {
        try
        {
            return set switch
            {
                CompletionSets.Sessions => SessionValues(workingDirectory),
                CompletionSets.McpServers => McpValues(),
                _ => [],
            };
        }
        catch
        {
            return [];
        }
    }

    /// <summary>How many rows the palette shows — enough to choose from, few enough to read.</summary>
    private const int MaxSessionRows = 9;

    private IReadOnlyList<CompletionValue> SessionValues(string? workingDirectory)
    {
        if (Shared.Resume is not { } store) return [];

        // SCOPED TO THIS FOLDER and re-read every time: a session that ended in another window a
        // minute ago has to appear, and a cached list is a list that lies about exactly that.
        var rows = store.List(workingDirectory, all: false);

        return [.. rows.Take(MaxSessionRows).Select((s, i) =>
            new CompletionValue((i + 1).ToString(), $"{Short(s.Uid)}  {s.Title ?? "(no messages yet)"}"))];
    }

    // THE LIVE SERVERS, not the names in a config file. A server listed in config that failed to
    // connect offers no tools, and completing to it would send the user somewhere empty.
    private IReadOnlyList<CompletionValue> McpValues()
    {
        if (Shared.Mcp is not { } toolset) return [];

        return [.. toolset.InstructionsByServer().Keys
            .Select(name => new CompletionValue(name, "connected MCP server"))];
    }

    private static string Short(string uid) => uid.Length <= 8 ? uid : uid[..8];

    /// <summary>
    /// Every command this process can run — see <see cref="Commands.CommandRegistry"/>.
    ///
    /// <para>SEEDED WITH WHAT CORE CAN SERVICE and handed to the composition root, which adds the
    /// ones needing a window. The manager owns it because it outlives any one session and because
    /// the commands that are not a session's are its own: the resume store, the usage archive, the
    /// MCP toolset.</para>
    /// </summary>
    public Commands.CommandRegistry Commands { get; } = new();

    /// <summary>Every open session, newest last.</summary>
    public IReadOnlyList<Session> Sessions
    {
        get { lock (_gate) return _sessions.ToList(); }
    }

    /// <summary>
    /// Opens a session on a folder and wires it.
    ///
    /// <para>The two per-session things a manager cannot invent are the caller's: <paramref
    /// name="ports"/> says how this session is observed, and its <see cref="SessionPorts.Policy"/>
    /// says how its permission questions are judged. A manager supplying either would be guessing —
    /// one is a rendering decision, the other is a folder and an edit mode belonging to this
    /// conversation.</para>
    /// </summary>
    public Session Open(string workingDirectory, ResolvedConfig? config,
        SessionPorts ports, WorkingMode? mode = null) =>
        Open(new Session(workingDirectory), config, ports, mode);

    /// <summary>Opens a session on this process's configuration — see <see cref="Config"/>.</summary>
    public Session Open(string workingDirectory, SessionPorts ports, WorkingMode? mode = null) =>
        Open(new Session(workingDirectory), null, ports, mode);

    /// <summary>
    /// Wires a session this manager did not construct, and owns it from then on.
    ///
    /// <para>FOR A ROOT WHOSE ORDERING ITS UI FIXES. cxagent's composition root must construct its
    /// Session early — the startup banner naming the edit mode is a chat message that cannot be
    /// revised, so the mode has to be resolved before the window exists — while the permission gate
    /// this manager holds cannot exist until there IS a window to prompt in. The folder-only
    /// <c>Open</c> demands an ordering
    /// that constraint forbids.</para>
    ///
    /// <para>WHY THIS RATHER THAN THE ROOT CALLING SessionFactory ITSELF: a session wired outside
    /// the manager is not in its collection, so the collection would not contain the one session
    /// actually running. Papering over that means adding the session afterwards and hoping the
    /// wiring matched. Two ways to wire is one too many; this is the single routine, and the
    /// folder overload above is a thin call into it.</para>
    /// </summary>
    /// <param name="mode">
    /// How the session starts. Null takes <see cref="WorkingMode.Default"/>, which is what an agent
    /// picks anyway when nobody sets one — so a caller with no opinion says nothing rather than
    /// repeating the default back.
    /// </param>
    /// <param name="config">
    /// What THIS session runs. Null takes the process's — see <see cref="Config"/>.
    ///
    /// <para>PER SESSION BECAUSE SESSIONS DIFFER. ConfigResolver.ResolveInstance exists precisely so
    /// one session can run a model the process default is not; with tabs, two sessions on two models
    /// is the ordinary case rather than the exception. A caller that wants that resolves the instance
    /// and passes it here; one that does not says nothing.</para>
    /// </param>
    /// <param name="session">The session to wire — already constructed with its working directory.</param>
    /// <param name="ports">Where this session's words, tool activity and questions go.</param>
    public Session Open(Session session, ResolvedConfig? config,
        SessionPorts ports, WorkingMode? mode = null)
    {
        SessionFactory.Wire(session, config ?? Config, Shared, ports, mode ?? WorkingMode.Default);

        lock (_gate)
            if (!_sessions.Contains(session)) _sessions.Add(session);

        // SO A COMMAND CAN REACH BACK. /sessions resume restores THROUGH the manager, because the
        // four steps of a resume only work together — see Resume.
        session.NoteManager(this);

        return session;
    }

    /// <summary>
    /// Restores an earlier conversation into a session, and retires the row it came from.
    ///
    /// <para>THREE STEPS THAT ONLY WORK TOGETHER — arm the resume, re-wire over it, retire the old
    /// row. Doing all three by hand at the call site is exactly where a sequence like that gets
    /// copied with one step quietly missing. The re-wire is the caller's because building the
    /// ports needs a window; everything else is the manager's, because the resume store is.</para>
    ///
    /// <para>SUPERSEDED, NOT FINISHED. The resumed session is a NEW agent with a new id writing its
    /// own rows, so leaving the old one open would offer the same context again at every launch —
    /// and accepting it twice would fork the conversation into two sessions claiming one history.
    /// Superseded rows survive pruning; see MarkSuperseded.</para>
    /// </summary>
    /// <param name="rewire">
    /// Rebuilds the session's host over the armed resume. A delegate because the ports it needs — an
    /// observer, a tool sink, a way to ask the user — can only be built by a presentation layer.
    /// </param>
    /// <param name="session">The session to restore into.</param>
    /// <param name="snapshot">The saved conversation and its spend.</param>
    public void Resume(Session session, Storage.SessionSnapshot snapshot, Action? rewire = null)
    {
        if (session.RefuseIfBusy()) return;

        // THE STORED HOOK WHEN NOTHING IS PASSED. A caller that has one uses it; /sessions resume has
        // no way to build one, which is what Rewire exists for.
        var apply = rewire ?? Rewire;

        // NO REWIRE, NO RESUME. Arming a resume nothing applies would leave the session claiming a
        // context its host does not have — worse than refusing, because the user is told it worked.
        if (apply is null)
        {
            session.SayCannotResume();
            return;
        }

        session.PendResume(snapshot);
        apply();
        Shared.Resume?.MarkSuperseded(snapshot.AgentId);

        // SAID AFTER THE REWIRE, so it reaches the observer the restored session is now wired to
        // rather than the one it replaced. The restored turns are not rendered — they are the
        // model's memory, not this session's scrollback — so without a line here the user faces an
        // empty screen and an agent that mysteriously already knows things.
        session.SayResumed(snapshot.Context.Count);
    }

    /// <summary>
    /// Closes one session: disposes its agent host and forgets it.
    ///
    /// <para>THE HOST IS THE ONLY DISPOSABLE THING. Session itself is not IDisposable and the two
    /// Sqlite stores are not either — they open a connection per call rather than holding one, which
    /// is what lets two sessions share them safely. So closing is exactly this, and a Dispose on
    /// Session would be ceremony over one line.</para>
    ///
    /// <para>THE SHARED SERVICES SURVIVE: a session ending is not the process ending, and the next
    /// session wants the same rules, the same logs and the same history.</para>
    /// </summary>
    public void Close(Session session)
    {
        lock (_gate) _sessions.Remove(session);

        // BOTH, and in this order: the session owns the turn's cancellation scope now, and the host
        // owns the agent and its MCP servers. Closing one without the other leaks whichever was
        // missed for the life of the process.
        session.DisposeTurnScope();
        session.Host?.Dispose();
    }

    /// <summary>
    /// Closes every session.
    ///
    /// <para>NOTHING ELSE TO RELEASE. The stores hold no connection between calls, and the log
    /// manager is immutable — so "the services this manager built" need no teardown, and
    /// <see cref="_ownsServices"/> exists to record ownership rather than to gate a Dispose that has
    /// nothing to do. If a future shared service does hold a handle, this is where it is released
    /// and that flag is what stops <see cref="Over"/> closing somebody else's.</para>
    /// </summary>
    public void Dispose()
    {
        foreach (var session in Sessions) Close(session);
    }
}

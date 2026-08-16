using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Agent;

/// <summary>
/// Owns a process's sessions and the services they share.
///
/// <para>WHAT IT REPLACES. The shared services were four locals in a 1,400-line UI method — a log
/// manager, two Sqlite stores, a permission gate — assembled into a <see cref="SharedServices"/>
/// record at the point of use and owned by nothing. That works while there is one session, which is
/// exactly the shape <see cref="Session"/>'s own doc says stops working at two: "a local is one
/// slot, so a second session would need a second copy of a 1,400-line method".</para>
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
/// <see cref="Plugins.Builtin.FileMutation"/>'s per-path lock table. It is static, and that is the
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

    private SessionManager(SharedServices shared, PermissionRulesStore? rules, bool ownsServices)
    {
        Shared = shared;
        Rules = rules;
        _ownsServices = ownsServices;
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
    public static SessionManager Create(AppPaths paths, IPermissionGate? gate = null,
        PermissionRulesStore? rules = null, Mcp.McpToolset? mcp = null)
    {
        var resume = new SqliteSessionStore(paths);
        resume.Prune(SqliteSessionStore.DefaultRetention);

        return new SessionManager(
            new SharedServices
            {
                Logs = new LogFileManager(paths),
                Resume = resume,
                History = new UsageHistoryStore(paths),
                Gate = gate,
                Mcp = mcp,
                GlobalInstructionsDir = paths.ConfigDir,
            },
            rules,
            ownsServices: true);
    }

    /// <summary>
    /// A manager over services somebody else built and owns.
    ///
    /// <para>For a caller that already assembled them — the composition root during the move to this
    /// type, and tests that want a manager over fakes. Disposing this one leaves them alone, because
    /// disposing what you did not create is how a store closes underneath its second user.</para>
    /// </summary>
    public static SessionManager Over(SharedServices shared, PermissionRulesStore? rules = null) =>
        new(shared, rules, ownsServices: false);

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
    public Session Open(string workingDirectory, ProviderResolution resolution,
        SessionPorts ports, WorkingMode mode)
    {
        var session = new Session(workingDirectory);
        SessionFactory.Wire(session, resolution, Shared, ports, mode);

        lock (_gate) _sessions.Add(session);
        return session;
    }

    /// <summary>
    /// Takes ownership of a session somebody else created and wired.
    ///
    /// <para>FOR A ROOT WHOSE ORDERING IS FIXED BY ITS UI. cxagent's composition root builds its
    /// session before the window, because the startup banner naming the edit mode is a chat message
    /// that cannot be revised — and it builds the permission gate after, because a gate needs a
    /// window to prompt in. So the session exists before this manager can, and <see cref="Open"/>
    /// would require an ordering the UI forbids.</para>
    ///
    /// <para>The alternative was leaving that session unowned, which is the state this type exists
    /// to end: a collection that does not contain the one session actually running is worse than no
    /// collection, because it reads as authoritative.</para>
    /// </summary>
    public Session Adopt(Session session)
    {
        lock (_gate)
            if (!_sessions.Contains(session)) _sessions.Add(session);
        return session;
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

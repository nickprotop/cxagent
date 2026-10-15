using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;

namespace CxAgent.UI;

/// <summary>The stores every front end opens.</summary>
/// <param name="Migration">What the permission-scope migration did, or null when the store could not
/// be rewritten.</param>
public sealed record StartupStores(FolderSessionStore Sessions, UsageHistoryStore History,
    PermissionRulesStore Rules, PermissionScopeMigration.Result? Migration);

/// <summary>What <see cref="Startup.Services"/> builds from.</summary>
/// <param name="Gate">The permission decider — a front end's own, since only it knows how to ask.</param>
public sealed record StartupServicesRequest(AppPaths Paths, StartupStores Stores,
    PermissionDecider Gate, LogFileManager Logs, ResolvedConfig Resolution);

/// <summary>MCP, its token store, and the session manager over the shared services.</summary>
public sealed record StartupServices(Core.Mcp.McpManager Mcp, Core.Mcp.Auth.TokenStore McpTokens,
    SessionManager Manager);

/// <summary>Which session a resume request names, or why there is none.</summary>
public sealed record ResumeTarget(SessionSnapshot? Snapshot, string? Problem);

/// <summary>
/// Startup that both front ends need — the TUI and a headless run.
///
/// <para>ONE COPY. A second front end that rebuilt these would drift from the first, and a session
/// made by one must open cleanly in the other.</para>
/// </summary>
public static partial class Startup
{
    /// <summary>The CXAGENT_* environment, which config resolution reads.</summary>
    public static IReadOnlyDictionary<string, string> Env() =>
        Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(e => e.Key is string k && k.StartsWith("CXAGENT_"))
            .ToDictionary(e => (string)e.Key, e => (string)(e.Value ?? ""));

    /// <summary>
    /// The catalog this process runs on, resolved once.
    ///
    /// <para>--model OVERRIDES defaultProvider FOR THIS RUN ONLY, without touching config. Same rule
    /// /model follows: naming an instance that is not configured returns null so the caller can stop,
    /// rather than falling back — silently starting on the model the user was trying to avoid is the
    /// worst of the available outcomes.</para>
    ///
    /// <para>A METHOD RATHER THAN TWO BRANCHES AT THE CALL SITE, so the result can be bound by an
    /// initialiser and never assigned again.</para>
    /// </summary>
    public static ResolvedConfig? Resolve(AppPaths paths, IReadOnlyDictionary<string, string> env,
        bool useMock, string? instance) =>
        instance is { } wanted && !useMock
            ? ConfigResolver.ResolveInstance(paths, env, wanted)
            : ConfigResolver.Resolve(paths, env, useMock);

    /// <summary>The stores every front end opens: resume rows, usage history, permission rules.</summary>
    public static StartupStores Stores(AppPaths paths)
    {
        // THE RESUME BUFFER, pruned once here rather than on a timer: startup is the only moment
        // nothing is mid-turn, and finished sessions are the only rows old enough to be worth dropping.
        var sessions = new FolderSessionStore(paths);
        sessions.Prune(FolderSessionStore.DefaultRetention);

        // USAGE HISTORY — a different file, and NOT pruned. The resume database above is a buffer
        // whose rows are worthless once a session ends cleanly; this is the archive, and pruning an
        // archive on startup would delete the answer to "where did last month go" every time the app
        // opened.
        var history = new UsageHistoryStore(paths);

        var rules = new PermissionRulesStore(paths);

        // FOLD THE SCOPES TWO OLD BUGS LEFT BEHIND, before anything reads trust or rules. Pre-identity
        // scopes were bare paths; ctime-era scopes moved every time the agent wrote a file. Both
        // stranded the grants and trust recorded under them, which is why a folder already trusted
        // asked again. Idempotent, so it costs one no-op pass on every later launch.
        //
        // The file is copied to permissions.json.premigration first — this rewrites decisions the
        // user made, and "it deleted my grants" must be answerable with a file rather than a claim.
        PermissionScopeMigration.Result? migration = null;
        try
        {
            migration = PermissionScopeMigration.Run(rules);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A store that cannot be rewritten still works, it just keeps asking. Never fatal.
        }

        return new StartupStores(sessions, history, rules, migration);
    }

    /// <summary>MCP, its token store, and the session manager over the shared services.</summary>
    public static StartupServices Services(StartupServicesRequest r)
    {
        // Tokens live beside config at 0600, never IN it.
        var tokens = new Core.Mcp.Auth.TokenStore(r.Paths);

        // The token is read per REQUEST through this delegate, so a login mid-session reaches a
        // client that was built before it.
        var mcp = new Core.Mcp.McpManager(r.Gate, accessToken: name => tokens.Get(name)?.AccessToken);

        // THE PROCESS'S SESSIONS AND WHAT THEY SHARE, built ONCE. The TUI re-wires on setup and
        // /model; constructing a fresh SharedServices record each time from the same locals is
        // harmless while the members are identical, and exactly the shape that stops being harmless
        // when a second session exists and one re-wire hands it a different record.
        //
        // Over() rather than Create(): the stores are built before the gate (the resume prune has to
        // happen at startup, and the TUI's gate cannot exist until there is a window), so the caller
        // assembles them in the order its front end forces and hands the result over.
        var manager = SessionManager.Over(
            new SharedServices
            {
                Logs = r.Logs,
                Resume = r.Stores.Sessions,
                History = r.Stores.History,
                Mcp = mcp.Toolset,
                McpStatuses = mcp.Statuses,
                Gate = r.Gate,

                // WHAT EACH SESSION WAS SHOWN, so a front end attaching later can be shown the same.
                // LAZY: constructing the store creates its database, and a run that records nothing
                // should leave no file behind.
                Transcripts = new Lazy<FolderTranscriptStore>(() => new FolderTranscriptStore(r.Paths)),

                GlobalInstructionsDir = r.Paths.ConfigDir,
            },
            r.Stores.Rules,
            // THE MANAGER OWNS THE PLUGIN ENTRIES ITS MUTATORS CHANGE, so it needs the resolution
            // rather than only the sessions having one. Without it Config is a default with no
            // plugins, and `/plugin disable x` answers "'x' is not a configured plugin" for a
            // plugin that just loaded from that very entry.
            r.Resolution);
        return new StartupServices(mcp, tokens, manager);
    }

    /// <summary>
    /// The session a request names. A PICK finds nothing and reports no problem: the TUI answers it
    /// by listing, and a headless run never asks it.
    /// </summary>
    public static ResumeTarget FindResumeTarget(FolderSessionStore store, string workingDir, ResumeRequest request)
    {
        switch (request.Kind)
        {
            case ResumeKind.None or ResumeKind.Pick:
                return new(null, null);

            // SCOPED TO THIS FOLDER: restoring another project's conversation fills this one with
            // its files and decisions.
            case ResumeKind.Continue:
                return store.LoadLatestUncontinued(workingDir) is { } latest
                    ? new(latest, null)
                    : new(null, "Nothing to continue in this folder.");
        }

        var uid = request.Uid!;
        var found = store.LoadByUid(uid);
        if (found.IsAmbiguous)
            return new(null, $"'{uid}' matches {found.Ambiguous.Count} sessions "
                + $"({string.Join(", ", found.Ambiguous.Take(4).Select(SessionsCommand.Short))}"
                + $"{(found.Ambiguous.Count > 4 ? ", …" : "")}) — use more characters.");

        // FOUND BY ID REGARDLESS OF FOLDER, unlike -c. Naming a specific session is an explicit act:
        // someone who copied an id out of `--sessions all` meant that session, and the folder scope
        // exists to stop an unasked-for session appearing, not to stop a named one being opened.
        return found.Session is null ? new(null, $"No session matches '{uid}'.") : new(found.Session, null);
    }
}

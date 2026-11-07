using CxAgent.Core.Agents;
using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.Hosting;

namespace CxAgent.Web;

/// <summary>
/// How a browser's "new session" or "resume" becomes a live session — and how one ends. Standalone, the web server
/// wires it itself; under <c>/web</c>, the terminal opens it as a tab.
/// </summary>
public interface ISessionOpener
{
    /// <summary>A new session in <paramref name="folder"/>, wired and running.</summary>
    Session Create(string folder);

    /// <summary>The conversation <paramref name="snapshot"/> saved, carried on in a new live session.</summary>
    Session Resume(SessionSnapshot snapshot);

    /// <summary>Ends a session: false when it would not close (a turn still running).</summary>
    Task<bool> CloseAsync(Session session);

    /// <summary>Whether a session nobody is looking at is unloaded after a while — standalone yes, a terminal's no.</summary>
    bool UnloadsIdle { get; }
}

/// <summary>
/// The standalone server's opener: every session wired through the shared composer, shown by nothing but the browser
/// tabs that attach to it, asked about its folder's trust through the prompt hub, and given its plugins after.
/// </summary>
public sealed class ComposerSessionOpener : ISessionOpener
{
    private readonly SessionComposer _composer;
    private ResolvedConfig _config;
    private readonly WorkingMode _mode;
    private readonly IFrontEnd _front = new HeadlessFrontEnd();

    /// <summary>
    /// A config saved since — from a browser's config page — for the sessions opened after it, as the terminal's new
    /// tabs take the config F8 last saved.
    /// </summary>
    public void Adopt(ResolvedConfig config) => _config = config;

    /// <summary>Opens sessions on <paramref name="config"/> in <paramref name="mode"/>, and installs the re-wire a
    /// <c>/model</c> switch needs when the process has no other front end to do it.</summary>
    public ComposerSessionOpener(SessionComposer composer, ResolvedConfig config, WorkingMode mode)
    {
        _composer = composer;
        _config = config;
        _mode = mode;

        // /model RE-WIRES THE SESSION, and the manager refuses unless a front end says how. Here there is no tab to
        // rebuild — only the same composer call with the session's own mode and, when its folder is unchanged, its
        // own policy and grants.
        composer.Services.Manager.RebindOne ??= (session, fresh) =>
        {
            var keepPolicy = session.Policy is { } own && string.Equals(own.Root, session.WorkingDirectory, StringComparison.Ordinal);
            _composer.Wire(new WireRequest(session, fresh, session.Provider is null ? _mode : session.Mode)
            {
                Policy = keepPolicy ? session.Policy : null,
            }, _front);
            return true;
        };
    }

    /// <inheritdoc/>
    public bool UnloadsIdle => true;

    /// <inheritdoc/>
    public Session Create(string folder)
    {
        var session = _composer.Create(folder, _config, _mode, _front);
        Settle(session);
        return session;
    }

    /// <inheritdoc/>
    public Session Resume(SessionSnapshot snapshot)
    {
        var session = new Session(snapshot.WorkingDir ?? throw new InvalidOperationException("That session has no folder."));
        session.PendResume(snapshot);

        // THE EDIT MODE COMES BACK WITH THE CONTEXT — resuming into a wider mode than the user left would be a silent
        // widening. Absent is always-ask: a row that says nothing is not permission.
        _composer.Wire(new WireRequest(session, _config, _mode with { Edits = snapshot.Edits ?? EditMode.AlwaysAsk }), _front);
        Settle(session);
        return session;
    }

    /// <inheritdoc/>
    public Task<bool> CloseAsync(Session session) => Task.FromResult(_composer.Services.Manager.Close(session));

    // TRUST, THEN PLUGINS — in that order, because a plugin the folder declares runs code, which is what the answer is
    // about. Fired and left: the question waits for a browser to answer it, and the session is usable meanwhile.
    private void Settle(Session session) => _ = Task.Run(async () =>
    {
        try
        {
            var result = await _composer.AskTrustIfUnknownAsync(session, CancellationToken.None);
            if (result.SaveError is { } error) session.NotifyWarning($"could not save folder trust: {Md.Escape(error)}");
            if (result.Outcome == TrustOutcome.Trusted) session.Notify("trusted this folder");
            else if (result.Outcome == TrustOutcome.NotTrusted) session.Notify("not trusted — file operations in this folder will ask every time");
        }
        catch (Exception ex)
        {
            session.NotifyWarning($"could not ask about this folder's trust: {Md.Escape(ex.Message)}");
        }
        await _composer.LoadPluginsAsync(session, _config, session.Notify, CancellationToken.None);
    });

    /// <summary>
    /// A front end with nothing to draw on: the browser tabs attach to the session's observers themselves, and its
    /// questions go through the prompt hub.
    /// </summary>
    private sealed class HeadlessFrontEnd : IFrontEnd
    {
        public SessionSinks SinksFor(Session session) => new(Silent.Instance, Silent.Instance);
        public AskUser? AskFor(Session session) => null;
        public void Wired(Session session, SessionSinks sinks, PermissionPolicy policy) { }
    }

    private sealed class Silent : ISessionObserver, IToolObserver
    {
        public static readonly Silent Instance = new();
        public void UserTurnAdded(ChatMessageId id, string text) { }
        public void AssistantTurnBegan(ChatMessageId id) { }
        public void AssistantTextAppended(ChatMessageId id, string token) { }
        public void AssistantThinkingAppended(ChatMessageId id, string text) { }
        public void AssistantThinkingEnded(ChatMessageId id, ThinkingSummary summary) { }
        public void AssistantTurnEnded(ChatMessageId id) { }
        public void AssistantLabelled(ChatMessageId id, string header) { }
        public void Said(Message message) { }
        public void ToolsChanged(IReadOnlyList<Job> jobs) { }
        public void ToolUpdated(Job job) { }
        public void ToolProgressed(Job job) { }
        public void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) { }
        public void ToolOutputAppended(string jobId, string delta) { }
    }
}

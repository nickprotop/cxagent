using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.Hosting;
using SharpConsoleUI;

namespace CxAgent.UI;

/// <summary>What a terminal tab needs to show one session.</summary>
/// <param name="System">The window system, for marshalling onto the UI thread.</param>
/// <param name="Main">The window holding the tab.</param>
/// <param name="Tab">The tab this session's output belongs to.</param>
/// <param name="Manager">Holds the session beside the others.</param>
/// <param name="Resolution">The configuration the session resolves against.</param>
/// <param name="Mode">The working mode it is wired in.</param>
/// <param name="ConfigDir">Where skills are discovered alongside the session's own folder.</param>
public sealed record TuiTab(
    ConsoleWindowSystem System,
    MainWindow Main,
    SessionTab Tab,
    SessionManager Manager,
    ResolvedConfig Resolution,
    WorkingMode Mode,
    string ConfigDir)
{
    /// <summary>Run before each user turn lands — the startup session's queue removes its placeholder here.</summary>
    public Action? BeforeUserTurn { get; init; }
}

/// <summary>
/// A session shown in a terminal tab: the tab's sinks, the composer the question lands in, and every readout
/// subscribed against the tab.
///
/// <para>ONE PER WIRE. A re-wire builds a new pair of sinks and the tab takes them; this object carries the job sink
/// from <see cref="SinksFor"/> to <see cref="Wired"/>, where the subscriptions need it.</para>
/// </summary>
public sealed class TuiFrontEnd(TuiTab tab) : IFrontEnd
{
    private InlineJobSink? _jobs;

    /// <inheritdoc/>
    public SessionSinks SinksFor(Session session)
    {
        var (transcript, jobs) = SessionWiring.Sinks(tab.System, tab.Tab, tab.BeforeUserTurn);
        _jobs = jobs;
        return new SessionSinks(transcript, jobs);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// NAMING THE SESSION THAT ASKS. A question is raised in a composer, and each conversation has its own — passed
    /// as a bare method group it landed on whichever tab was in front, so a background session's question appeared
    /// above another session's transcript.
    /// </remarks>
    public AskUser? AskFor(Session session) => (questions, ct) => tab.Main.AskQuestionAsync(session, questions, ct);

    /// <inheritdoc/>
    /// <remarks>
    /// WITH THE CALL REPORTS. The tab draws a round's tool rows from those, not from the rows' own events, so without
    /// them a restored turn showed its answer and none of the calls behind it.
    /// </remarks>
    public void ShowRestoredPast(Session session, SessionSinks sinks, AppPaths paths)
    {
        if (session.SessionId is { } agent)
            SessionReplay.Play(paths, agent, sinks.Observer, sinks.ToolObserver, called: _jobs!.RecordToolCall);
    }

    /// <inheritdoc/>
    public void Wired(Session session, SessionSinks sinks, PermissionPolicy policy) =>
        SessionWiring.Subscribe(new SessionWiring.Wiring(
            System: tab.System,
            Main: tab.Main,
            Tab: tab.Tab,
            Session: session,
            Manager: tab.Manager,
            Resolution: tab.Resolution,
            Mode: tab.Mode,
            Policy: policy,
            ConfigDir: tab.ConfigDir), _jobs!);
}

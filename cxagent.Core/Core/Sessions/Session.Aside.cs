using CxAgent.Core.Agents;
using CxAgent.Core.Commands;
using CxAgent.Core.Models;

namespace CxAgent.Core.Sessions;

/// <summary>
/// btw mode: a side question about this conversation, answered by a read-only fork that never
/// writes back into it.
/// </summary>
public sealed partial class Session
{
    private readonly object _asideLock = new();
    private AsideRunner? _aside;
    private int _asides;
    private bool _inAside;

    /// <summary>Where asides draw. Null draws nowhere, which a headless host is content with.</summary>
    public IAsideObserver? AsideObserver { get; set; }

    private readonly List<IAsideObserver> _asideWatchers = [];

    /// <summary>
    /// Another place asides draw, beside <see cref="AsideObserver"/>, until the returned handle is disposed.
    ///
    /// <para>A SECOND FRONT END, NOT A REPLACEMENT. A browser following a terminal's session has to hear its asides,
    /// and the terminal sets <see cref="AsideObserver"/> again every time it re-wires the session; a browser that
    /// replaced it would silence the terminal, and one it replaced would go deaf after a model switch.</para>
    /// </summary>
    public IDisposable WatchAsides(IAsideObserver watcher)
    {
        lock (_asideWatchers) _asideWatchers.Add(watcher);
        return new Unwatch(() => { lock (_asideWatchers) _asideWatchers.Remove(watcher); });
    }

    private sealed class Unwatch(Action undo) : IDisposable
    {
        private Action? _undo = undo;
        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }

    /// <summary>
    /// What an aside opened now draws to: the front end's observer and every watcher, as each stands WHEN AN EVENT
    /// ARRIVES — a browser that opens the session while an aside answers hears the rest of it.
    /// </summary>
    private sealed class AsideFanOut(Session session) : IAsideObserver
    {
        private void Each(Action<IAsideObserver> raise)
        {
            if (session.AsideObserver is { } front) raise(front);
            IAsideObserver[] watchers;
            lock (session._asideWatchers) watchers = [.. session._asideWatchers];
            foreach (var w in watchers) raise(w);
        }

        public void Asked(int aside, string question) => Each(o => o.Asked(aside, question));
        public void Waiting(int aside, bool waiting) => Each(o => o.Waiting(aside, waiting));
        public void AnswerAppended(int aside, string text) => Each(o => o.AnswerAppended(aside, text));
        public void LookedUp(int aside, string row) => Each(o => o.LookedUp(aside, row));
        public void ThinkingAppended(int aside, string text) => Each(o => o.ThinkingAppended(aside, text));
        public void ThinkingEnded(int aside, Llm.ThinkingSummary summary) => Each(o => o.ThinkingEnded(aside, summary));
        public void Finished(int aside, AsideEnd end, string? detail) => Each(o => o.Finished(aside, end, detail));
    }

    public bool InAside => Volatile.Read(ref _inAside);

    public bool AsideAnswering => Volatile.Read(ref _aside)?.IsAnswering ?? false;

    /// <summary>
    /// <c>/btw</c>: forks what the agent last sent and enters btw mode, asking at once when there is
    /// a question.
    /// </summary>
    /// <param name="question">What the aside reads.</param>
    /// <param name="shown">
    /// The question as its block shows it, when that differs — a pasted block as its label. The command
    /// registry passes only the text, so a front end that has the line as typed calls this itself.
    /// </param>
    /// <param name="attachments">What the question carries; its images reach the aside's model.</param>
    public CommandStatus Btw(string question, string? shown = null, Models.Attachments? attachments = null)
    {
        if (Host is not { } host)
        {
            Say(new Message("No model is configured. Run /model to pick one first.", Severity.Warning));
            return CommandStatus.Reported;
        }

        // FORKED NOW, not at the first question: the user asked "by the way" about what is on screen,
        // and a turn that moves on in the meantime should not change what the aside sees.
        var runner = host.OpenAside(Interlocked.Increment(ref _asides), new AsideFanOut(this));
        if (runner is null)
        {
            Say(new Message("nothing to ask about yet — send a message first", Severity.Warning));
            return CommandStatus.Reported;
        }

        // THE OLD ONE STOPS FIRST, so two answers never stream at once and the user's Esc has one
        // thing to stop.
        lock (_asideLock)
        {
            _aside?.Stop();
            _aside = runner;
        }

        Volatile.Write(ref _inAside, true);
        Announce(SessionChangeKind.Aside);

        if (!string.IsNullOrWhiteSpace(question)) runner.Ask(question.Trim(), shown?.Trim(), attachments);
        return CommandStatus.Reported;
    }

    /// <summary>Sends what the user typed in btw mode to the aside.</summary>
    /// <param name="text">What the aside reads.</param>
    /// <param name="echo">What the aside's block shows, when that differs — a pasted block as its label.</param>
    /// <param name="attachments">What the line carries; its images reach the aside's model.</param>
    public SubmitOutcome SendAside(string text, string? echo = null, Models.Attachments? attachments = null)
    {
        var runner = Volatile.Read(ref _aside);
        if (runner is null) return new SubmitOutcome.Refused("not in btw mode");

        var answering = runner.Ask(text, echo, attachments);
        if (answering is null)
            Say(new Message("the aside is still answering — wait for it, or Esc to stop it", Severity.Warning));
        return new SubmitOutcome.Aside(answering);
    }

    /// <summary>Esc while the aside answers: stops that answer, and nothing of the main turn.</summary>
    public void StopAside() => Volatile.Read(ref _aside)?.Stop();

    /// <summary>Esc while the aside is idle: the composer is the main agent's again.</summary>
    public void LeaveAside()
    {
        lock (_asideLock)
        {
            _aside?.Stop();
            _aside = null;
        }

        if (!Volatile.Read(ref _inAside)) return;
        Volatile.Write(ref _inAside, false);
        Announce(SessionChangeKind.Aside);
    }
}

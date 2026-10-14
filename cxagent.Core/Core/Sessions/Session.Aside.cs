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

    public bool InAside => Volatile.Read(ref _inAside);

    public bool AsideAnswering => Volatile.Read(ref _aside)?.IsAnswering ?? false;

    /// <summary>
    /// <c>/btw</c>: forks what the agent last sent and enters btw mode, asking at once when there is
    /// a question.
    /// </summary>
    public CommandStatus Btw(string question)
    {
        if (Host is not { } host)
        {
            Say(new Message("No model is configured. Run /model to pick one first.", Severity.Warning));
            return CommandStatus.Reported;
        }

        // FORKED NOW, not at the first question: the user asked "by the way" about what is on screen,
        // and a turn that moves on in the meantime should not change what the aside sees.
        var runner = host.OpenAside(Interlocked.Increment(ref _asides), AsideObserver ?? NullAside.Instance);
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

        if (!string.IsNullOrWhiteSpace(question)) runner.Ask(question.Trim());
        return CommandStatus.Reported;
    }

    /// <summary>Sends what the user typed in btw mode to the aside.</summary>
    public SubmitOutcome SendAside(string text)
    {
        var runner = Volatile.Read(ref _aside);
        if (runner is null) return new SubmitOutcome.Refused("not in btw mode");

        var answering = runner.Ask(text);
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

    private sealed class NullAside : IAsideObserver
    {
        public static readonly NullAside Instance = new();
        public void Asked(int aside, string question) { }
        public void Waiting(int aside, bool waiting) { }
        public void AnswerAppended(int aside, string text) { }
        public void LookedUp(int aside, string row) { }
        public void Finished(int aside, AsideEnd end, string? detail) { }
    }
}

using CxAgent.Core.Agents;
using CxAgent.Core.Permissions;

namespace CxAgent.Hosting;

/// <summary>Something the agent is waiting on a person for, addressed to one session.</summary>
/// <param name="Id">This prompt's own id — what an answer names.</param>
/// <param name="SessionId">The session asking (<c>Session.Id</c>).</param>
public abstract record PendingPrompt(string Id, string SessionId);

/// <summary>May this run? A command, a file, a fetch.</summary>
public sealed record PermissionPending(string Id, string SessionId, PermissionRequest Request, bool OfferTrust)
    : PendingPrompt(Id, SessionId);

/// <summary>The model's own question — <c>ask_user</c>.</summary>
public sealed record QuestionPending(string Id, string SessionId, IReadOnlyList<UserQuestion> Questions)
    : PendingPrompt(Id, SessionId);

/// <summary>Trust this folder? Asked when a session opens in one nobody has decided about.</summary>
public sealed record TrustPending(string Id, string SessionId, string Folder)
    : PendingPrompt(Id, SessionId);

/// <summary>An answer to a <see cref="PendingPrompt"/>; its type matches the prompt's.</summary>
public abstract record PromptAnswer;

/// <summary>The answer to a <see cref="PermissionPending"/>.</summary>
public sealed record PermissionAnswer(PermissionChoice Choice) : PromptAnswer;

/// <summary>The answer to a <see cref="QuestionPending"/>.</summary>
public sealed record QuestionAnswer(QuestionAnswers Answers) : PromptAnswer;

/// <summary>The answer to a <see cref="TrustPending"/>.</summary>
public sealed record TrustAnswer(bool Trusted) : PromptAnswer;

/// <summary>How a prompt ended: the answer (null when it was withdrawn) and who gave it.</summary>
public sealed record Resolution(PromptAnswer? Answer, string AnsweredBy);

/// <summary>
/// A front end that shows prompts — the terminal, or a browser tab.
///
/// <para>ANSWERS GO BACK THROUGH <see cref="PromptHub.Answer"/>, not through this interface: the hub decides which
/// answer counts, and a responder that answered late learns it lost from <see cref="Resolved"/>.</para>
/// </summary>
public interface IPromptResponder
{
    /// <summary>Whether this responder shows prompts for <paramref name="sessionId"/>.</summary>
    bool Serves(string sessionId);

    /// <summary>
    /// Shows a prompt. <paramref name="resolved"/> fires when the prompt is answered or withdrawn — by anyone,
    /// this responder included — so whatever it put on screen can go.
    /// </summary>
    void Show(PendingPrompt prompt, CancellationToken resolved);

    /// <summary>The prompt ended: who answered, and with what.</summary>
    void Resolved(string promptId, Resolution resolution);
}

/// <summary>
/// Every question an agent waits on, raced between every front end showing its session: the first answer wins,
/// and everyone else's copy goes away.
///
/// <para>PERMISSION PROMPTS ONE AT A TIME PER SESSION, oldest first: they share the session's composer cell, and two
/// at once let an answer meant for one land on the other. A trust question queues only behind other trust questions,
/// and a model's question behind nothing — a session's sub-agents and background agents share its id, and a
/// background agent's permission prompt must not stall behind a question the host asked while the user was
/// away.</para>
///
/// <para>A PROMPT NOBODY SERVES STILL WAITS. A browser user who closed the tab comes back to find it — the
/// session list shows the session as waiting — rather than a turn that silently gave up.</para>
/// </summary>
public sealed class PromptHub
{
    private sealed class Entry(PendingPrompt prompt)
    {
        public PendingPrompt Prompt { get; } = prompt;

        /// <summary>Which line this prompt waits in — see the class remarks.</summary>
        public string Queue { get; } = prompt switch
        {
            PermissionPending => prompt.SessionId,
            TrustPending => prompt.SessionId + "\0trust",
            _ => prompt.Id,
        };
        public TaskCompletionSource<Resolution> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Gone { get; } = new();
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, List<Entry>> _queues = [];
    private readonly List<IPromptResponder> _responders = [];

    /// <summary>A fresh prompt id.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Adds a responder, shows it what is already waiting, and returns the handle that removes it.</summary>
    public IDisposable Add(IPromptResponder responder)
    {
        List<Entry> heads;
        lock (_gate)
        {
            _responders.Add(responder);
            heads = [.. _queues.Values.Where(q => q.Count > 0).Select(q => q[0])];
        }
        foreach (var head in heads)
            if (!head.Gone.IsCancellationRequested && responder.Serves(head.Prompt.SessionId))
                responder.Show(head.Prompt, head.Gone.Token);
        return new Removal(this, responder);
    }

    /// <summary>What is waiting for <paramref name="sessionId"/>, oldest first.</summary>
    public IReadOnlyList<PendingPrompt> PendingFor(string sessionId)
    {
        lock (_gate)
            return [.. _queues.Values.SelectMany(q => q).Select(e => e.Prompt).Where(p => p.SessionId == sessionId)];
    }

    /// <summary>Whether anything is waiting for <paramref name="sessionId"/>.</summary>
    public bool IsWaiting(string sessionId)
    {
        lock (_gate) return _queues.Values.Any(q => q.Any(e => e.Prompt.SessionId == sessionId));
    }

    /// <summary>
    /// Answers a prompt. False when it was already answered or withdrawn — the caller lost the race, and the
    /// <see cref="IPromptResponder.Resolved"/> it receives says who won.
    /// </summary>
    public bool Answer(string promptId, PromptAnswer answer, string answeredBy) =>
        Finish(promptId, new Resolution(answer, answeredBy));

    /// <summary>Raises a prompt and waits for its answer; null when <paramref name="ct"/> withdrew it first.</summary>
    public async Task<PromptAnswer?> AskAsync(PendingPrompt prompt, CancellationToken ct)
    {
        var entry = new Entry(prompt);
        bool head;
        lock (_gate)
        {
            if (!_queues.TryGetValue(entry.Queue, out var q)) _queues[entry.Queue] = q = [];
            q.Add(entry);
            head = q.Count == 1;
        }
        if (head) Present(entry);

        using var withdraw = ct.Register(() => Finish(prompt.Id, new Resolution(null, "cancelled")));
        return (await entry.Done.Task.ConfigureAwait(false)).Answer;
    }

    /// <summary>The permission gate's prompt hook. Withdrawn is a deny: nobody said yes.</summary>
    public async Task<PermissionChoice> AskPermissionAsync(PermissionRequest request, bool offerTrust, CancellationToken ct) =>
        await AskAsync(new PermissionPending(NewId(), request.Policy?.SessionId ?? "", request, offerTrust), ct)
            is PermissionAnswer { Choice: var choice } ? choice : PermissionChoice.Deny;

    /// <summary>The <c>ask_user</c> hook for one session. Withdrawn is a cancelled answer.</summary>
    public AskUser AskUserFor(string sessionId) => async (questions, ct) =>
        await AskAsync(new QuestionPending(NewId(), sessionId, questions), ct)
            is QuestionAnswer { Answers: var answers } ? answers : QuestionAnswers.Cancel;

    /// <summary>Asks whether to trust <paramref name="folder"/>; null when the question was withdrawn.</summary>
    public async Task<bool?> AskTrustAsync(string sessionId, string folder, CancellationToken ct) =>
        await AskAsync(new TrustPending(NewId(), sessionId, folder), ct) is TrustAnswer { Trusted: var trusted } ? trusted : null;

    private bool Finish(string promptId, Resolution resolution)
    {
        Entry? done = null;
        Entry? next = null;
        IPromptResponder[] responders;
        lock (_gate)
        {
            foreach (var (session, q) in _queues)
            {
                var i = q.FindIndex(e => e.Prompt.Id == promptId);
                if (i < 0) continue;
                done = q[i];
                q.RemoveAt(i);
                if (i == 0 && q.Count > 0) next = q[0];
                if (q.Count == 0) _queues.Remove(session);
                break;
            }
            if (done is null) return false;
            responders = [.. _responders];
        }

        // THE SCREENS FIRST, then the waiter. A front end takes its copy down when told (it only queues the work), and
        // a turn resumed before that can raise its next prompt into a cell the old one still holds — where it is
        // never placed, and the turn waits on a prompt nobody can see.
        done.Gone.Cancel();
        foreach (var r in responders)
            if (r.Serves(done.Prompt.SessionId)) r.Resolved(promptId, resolution);
        done.Done.TrySetResult(resolution);
        if (next is not null) Present(next);
        return true;
    }

    private void Present(Entry entry)
    {
        IPromptResponder[] responders;
        lock (_gate)
        {
            // STILL WAITING AND STILL FIRST IN ITS LINE. Two answers racing can each hand on a "next"; only the one
            // that is actually at the head now is shown, so a stale prompt never takes a cell after its successor.
            if (entry.Gone.IsCancellationRequested
                || !_queues.TryGetValue(entry.Queue, out var q) || q.Count == 0 || !ReferenceEquals(q[0], entry))
                return;
            responders = [.. _responders];
        }
        foreach (var r in responders)
            if (r.Serves(entry.Prompt.SessionId)) r.Show(entry.Prompt, entry.Gone.Token);
    }

    private sealed class Removal(PromptHub hub, IPromptResponder responder) : IDisposable
    {
        public void Dispose()
        {
            lock (hub._gate) hub._responders.Remove(responder);
        }
    }
}

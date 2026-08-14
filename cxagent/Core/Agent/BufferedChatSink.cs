using System.Text;

namespace CxAgent.Core.Agent;

/// <summary>
/// A sub-agent's transcript, captured rather than rendered.
///
/// <para>WHY A CHILD DOES NOT RENDER. Its work streams token by token exactly as a parent's does, and
/// interleaving two streams into one transcript is what makes fan-out illegible — with several
/// children it is not merely noisy, it is unreadable, because nothing marks which agent said what.
/// The child gets a row instead (D22), and its words are held here.</para>
///
/// <para>NOT DISCARDED, EITHER. "Inspectable afterwards" is a stated goal of the sub-agent work, and a
/// child whose reasoning went nowhere cannot be reviewed when its answer turns out to be wrong. This
/// is also what step 3's expand-the-row swaps in.</para>
///
/// <para>THREAD SAFETY IS NOT OPTIONAL HERE even though step 1 runs one child at a time: the agent
/// loop appends from whatever thread the provider's continuation resumed on, and the UI reads
/// <see cref="Transcript"/> when someone expands the row. A lock rather than a concurrent collection
/// because the reads are rare and the writes must stay ordered — a transcript out of order is worse
/// than no transcript.</para>
/// </summary>
public sealed class BufferedChatSink : ISessionObserver
{
    private readonly object _gate = new();
    private readonly StringBuilder _transcript = new();
    private readonly List<string> _errors = [];
    private long _nextId;

    /// <summary>Everything the child said, in order — body and reasoning both.</summary>
    public string Transcript { get { lock (_gate) return _transcript.ToString(); } }

    /// <summary>
    /// What the child reported as an error.
    ///
    /// <para>SEPARATE FROM THE TRANSCRIPT because the spawn branch reads it. A cap or a stuck run
    /// announces itself only through <see cref="Failed"/>, and for a child that is a buffer nobody
    /// is watching — so the caller needs it addressable rather than buried in prose. <see
    /// cref="SendOutcome"/> now carries the same information structurally; this stays because an
    /// error raised mid-run (a provider fault the loop recovered from) never reaches the outcome.</para>
    /// </summary>
    public IReadOnlyList<string> Errors { get { lock (_gate) return [.. _errors]; } }

    /// <summary>Body text the child produced, without reasoning or system lines — what it actually
    /// said, for a caller that wants the words rather than the record.</summary>
    public string Body { get { lock (_gate) return _body.ToString(); } }
    private readonly StringBuilder _body = new();

    public ChatMessageId UserTurnAdded(string text)
    {
        lock (_gate) _transcript.Append("> ").Append(text).AppendLine();
        return NextId();
    }

    public ChatMessageId AssistantTurnBegan() => NextId();

    public void AssistantTextAppended(ChatMessageId id, string token)
    {
        lock (_gate)
        {
            _transcript.Append(token);
            _body.Append(token);
        }
    }

    // KEPT, NOT DROPPED. A child's reasoning is the most useful thing in the buffer when its answer
    // turns out to be wrong — it is the record of how it got there. It stays out of Body because the
    // parent's tool result must be the answer, not the thinking.
    public void AssistantReasoningAppended(ChatMessageId id, string text)
    {
        lock (_gate) _transcript.Append(text);
    }

    public void AssistantTurnEnded(ChatMessageId id)
    {
        lock (_gate) _transcript.AppendLine();
    }

    // A header is a LIVE indicator — a spinner and a status that overwrite in place. There is nothing
    // to overwrite in a buffer, and appending each one would fill the transcript with the successive
    // states of a progress line nobody watched.
    public void AssistantLabelled(ChatMessageId id, string header) { }

    public void Failed(string message)
    {
        lock (_gate)
        {
            _errors.Add(message);
            _transcript.Append("error: ").Append(message).AppendLine();
        }
    }

    // Ids need only be unique WITHIN a sink: nothing compares them across sinks, and each sink mints
    // its own. A child's ids colliding with its parent's is therefore not a hazard.
    private ChatMessageId NextId()
    {
        lock (_gate) return new ChatMessageId(Interlocked.Increment(ref _nextId));
    }
}

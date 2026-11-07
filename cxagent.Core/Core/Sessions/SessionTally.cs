namespace CxAgent.Core.Sessions;

/// <summary>
/// What one conversation has done: turns taken, tools called, and how long it has been open.
///
/// <para>ON THE SESSION, IN CORE, so every front end reads one count. A count kept by each front end starts at its
/// own moment and counts its own events, and the terminal and a browser on one session would show different turns.</para>
///
/// <para>NOT IN THE PANEL THAT RENDERS IT, because one panel serves every tab: the right-hand column belongs to the
/// window and shows whichever session is in front, so counters kept there would sum every conversation and time the
/// window's age rather than the session's.</para>
///
/// <para>A CLASS, NOT A RECORD, AND MUTABLE ON PURPOSE. Every reader holds the session's one instance, so a turn
/// recorded on the session's thread is what each panel reads next; a record would hand each reader a copy that the
/// increments never reach.</para>
/// </summary>
public sealed class SessionTally
{
    /// <summary>When this conversation opened. Set at construction, which is when its session is made.</summary>
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;

    public int Turns { get; set; }

    public int ToolCalls { get; set; }

    /// <summary>One model turn finished, having made <paramref name="calls"/> tool calls. From the turn's own thread, so
    /// under a lock; a reader on another thread sees a count at most one turn behind.</summary>
    internal void Record(int calls)
    {
        lock (this)
        {
            Turns++;
            ToolCalls += calls;
        }
    }
}

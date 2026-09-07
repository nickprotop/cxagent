namespace CxAgent.UI;

/// <summary>
/// What one conversation has done: turns taken, tools called, and how long it has been open.
///
/// <para>SEPARATE FROM THE PANEL THAT RENDERS IT, because one panel serves every tab. The right-hand
/// column is a property of the WINDOW — there is one of it, and it shows whichever session is in
/// front — so counters kept inside it were shared by every conversation in the process: two sessions
/// summed into a single turn count, and the elapsed clock measured the WINDOW's age rather than
/// either session's. A tab opened an hour in reported an hour of work it had not done.</para>
///
/// <para>A CLASS, NOT A RECORD, AND MUTABLE ON PURPOSE. The tab holds one and hands the same
/// instance to the panel, so a turn recorded while the tab is in front updates the tab's own tally
/// without the panel needing to write back. A record would make <c>Follow</c> a copy and the
/// increments would land on a value nobody reads again.</para>
/// </summary>
public sealed class SessionTally
{
    /// <summary>When this conversation opened. Set at construction, which is when its tab is made.</summary>
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;

    public int Turns { get; set; }

    public int ToolCalls { get; set; }
}

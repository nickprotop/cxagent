namespace CxAgent.Core.Agents;

/// <summary>
/// What one sub-agent needs to look alive while it works, held for the child's life rather than for
/// one tool call.
///
/// <para>THE CALL IS THE WRONG OWNER, AND THAT IS THE WHOLE REASON THIS TYPE EXISTS. A child is
/// started by the `agent` tool call and then resumed by agent_send, by /agents send, and by a
/// mailbox drain — none of which pass through that call's method. State scoped to the call therefore
/// survives on one path and not the others, and nothing decides which: the tick timer died on
/// resume while the turn counter lived, so a resumed row's elapsed time froze while its turn count
/// kept climbing.</para>
///
/// <para>ONE INSTANCE PER CHILD, NOT PER STRETCH OF WORK. Turns and skills accumulate over an
/// agent's whole life — that is what makes them facts about the agent rather than about the latest
/// thing asked of it — so they cannot live in something created per send.</para>
///
/// <para>NOT THREAD-SAFE BY ITSELF. Begin and End are driven by SubAgentStore's claim, which admits
/// exactly one holder at a time, so the two never overlap for one child; the counters are written
/// from the child's own event callbacks and read by a timer, which is the same single-writer shape
/// the call-scoped locals had.</para>
/// </summary>
/// <param name="child">The child this run belongs to.</param>
/// <param name="jobId">
/// The PARENT's job id — the key its row on screen is addressed by.
///
/// <para>Not the child's agent id, which the registry already keys on: the row was created by the
/// parent's turn and belongs to it, and a child's agent id addresses nothing the parent draws.</para>
/// </param>
public sealed class ChildRun(SubAgent child, string jobId)
{
    private Timer? _tick;

    public SubAgent Child { get; } = child;

    public string JobId { get; } = jobId;

    /// <summary>Turns this child has taken, over its whole life — see the type's own remarks.</summary>
    public int Turns { get; set; }

    /// <summary>
    /// The skills it has loaded, captured while its context still exists.
    ///
    /// <para>A FINISHED ROW CANNOT READ THEM. It is built once the run has returned, and this
    /// type's argument is that the child's context is gone by then — so the value has to have been
    /// taken while the row was live.</para>
    /// </summary>
    public IReadOnlyList<string> Skills { get; set; } = [];

    /// <summary>
    /// The brief this child was SPAWNED with, which is what the row's task line names.
    ///
    /// <para>NOT THE LATEST THING ASKED. It is written once, at spawn, and a resume never reaches
    /// it: SendBegan carries an agent id and nothing else, so the prompt that woke a child is not
    /// available where a run is begun. A resumed row therefore keeps naming the original task, and
    /// that is what this field means.</para>
    /// </summary>
    public string? Prompt { get; set; }

    /// <summary>
    /// What the envelope said this stretch ended as, when there was one.
    ///
    /// <para>THE ENVELOPE'S OWN WORD, not a two-way failed/completed guess. A capped run — an
    /// explore child burning all 30 turns hunting a schema nobody publishes — is neither, and
    /// recording it as completed puts a wasted run in the success column, which is exactly the run
    /// worth finding later.</para>
    ///
    /// <para>NULL FROM A RESUME, which has no envelope to read: a send answers with the child's own
    /// text. "completed" is then the honest default — the work stopped and the caller got an
    /// answer.</para>
    /// </summary>
    public string? Outcome { get; set; }

    /// <summary>
    /// When the CURRENT stretch of work started.
    ///
    /// <para>REBASED AT EACH BEGIN, so the elapsed figure reads "working for 40s" rather than
    /// "spawned twelve minutes ago" — a clock counting an idle child's whole life answers a question
    /// nobody watching a spinner is asking. The cumulative facts are carried by Turns and by the
    /// child's own spend, which is what the finished account states.</para>
    /// </summary>
    public DateTimeOffset Started { get; private set; }

    /// <summary>
    /// Which stretch of work this is, from 1.
    ///
    /// <para>WHAT MAKES A RESUMED RUN'S ARCHIVE ROW DISTINCT. Every stretch is saved to history
    /// separately, so its id must differ from the spawn's or the second write would collide with the
    /// first and one of the two runs would go missing.</para>
    /// </summary>
    public int Stretch { get; private set; }

    /// <summary>
    /// What the child had already spent and taken when THIS stretch began, so the archive can
    /// subtract it.
    ///
    /// <para>THE CHILD'S TALLIES ARE LIFETIME ONES AND THE ARCHIVE'S ROWS ARE NOT. Every stretch
    /// writes its own row under its own id, and history answers "what is this agent type worth" by
    /// SUMMING those rows — so a row carrying the running total counts the first stretch again in
    /// the second and again in the third. A child costing 30k across three sends would be archived
    /// as 60k, growing quadratically with the number of resumes.</para>
    ///
    /// <para>TAKEN IN Begin, which is the one place every path — spawn, agent_send, /agents send,
    /// mailbox drain — funnels through, and the same place the clock is rebased. A snapshot taken
    /// anywhere else would be right for whichever path took it and silently wrong for the rest.</para>
    /// </summary>
    public (int Input, int Output) SpentAtStart { get; private set; }

    /// <inheritdoc cref="SpentAtStart"/>
    public int TurnsAtStart { get; private set; }

    /// <summary>Whether the repaint is running. A TEST SEAM — nothing in the app reads it.</summary>
    public bool Working => _tick is not null;

    /// <summary>
    /// Marks work starting: rebases the clock, counts the stretch, and starts the repaint.
    ///
    /// <para>A PERIODIC TICK, because turn boundaries alone are not enough. A child spends most of a
    /// long run INSIDE one turn, waiting on a provider or a slow tool, and a row whose elapsed time
    /// only moves between turns reads exactly like a frozen one.</para>
    ///
    /// <para>Answers false when already working, so a double subscription cannot leave a timer
    /// nothing disposes.</para>
    /// </summary>
    public bool Begin(TimerCallback onTick)
    {
        if (_tick is not null) return false;
        Started = DateTimeOffset.UtcNow;
        Stretch++;
        // THE BASELINE FOR THIS STRETCH'S COST — see SpentAtStart for why a lifetime tally cannot be
        // archived directly. Child is null in the unit tests that exercise the timer alone, which
        // have no agent to read a tally from.
        SpentAtStart = Child?.Agent.Spend ?? default;
        TurnsAtStart = Turns;
        _tick = new Timer(onTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        return true;
    }

    /// <summary>
    /// Marks work stopping, and answers whether it had been working.
    ///
    /// <para>THE TICK STOPS HOWEVER THE WORK ENDS — answer, error, or cancellation. A Timer left
    /// running holds a closure over the child and keeps writing to a row that has already closed,
    /// once a second, for the rest of the session.</para>
    ///
    /// <para>FALSE ON A SECOND CALL, so a caller can use it to decide whether to write a finished
    /// account: writing one twice for a single stop would overwrite the row's settled numbers with
    /// an identical second copy and raise a duplicate archive row.</para>
    /// </summary>
    public bool End()
    {
        if (_tick is not { } t) return false;
        _tick = null;
        t.Dispose();
        return true;
    }
}

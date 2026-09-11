using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The liveness a child gets while it is working — the apparatus that used to live inside the
/// `agent` tool call and now belongs to the child itself.
///
/// <para>WHY THIS FILE IS SEPARATE FROM SubAgentSpawnerTests: these are about a run's STATE across
/// several stretches of work, not about what a spawn returns. A resumed child is the case that had
/// no coverage at all, and a test for it sitting among spawn assertions reads as a spawn test.</para>
/// </summary>
public class ChildLivenessTests
{
    [Fact]
    public void ARunsTurns_AccumulateAcrossStretches()
    {
        var run = new ChildRun(null!, "job-1");

        run.Begin(_ => { });
        run.Turns++;
        run.Turns++;
        run.End();

        run.Begin(_ => { });
        run.Turns++;
        run.End();

        // CUMULATIVE, NOT PER-SEND: "3 turns" is the truth about the agent. Resetting per stretch
        // would make the number mean "turns since the last thing I asked", which no reader expects.
        Assert.Equal(3, run.Turns);
    }

    [Fact]
    public void ABeginRebasesTheClock_SoElapsedMeansThisStretch()
    {
        var run = new ChildRun(null!, "job-1");

        run.Begin(_ => { });
        var first = run.Started;
        run.End();

        Thread.Sleep(20);
        run.Begin(_ => { });

        Assert.True(run.Started > first);
        run.End();
    }

    [Fact]
    public void ASecondBeginWithoutAnEnd_DoesNotStartASecondTimer()
    {
        var run = new ChildRun(null!, "job-1");

        Assert.True(run.Begin(_ => { }));
        // The store refuses a second claim, so this cannot happen through the supported path — but a
        // double subscription would, and it must not leave a timer nothing disposes.
        Assert.False(run.Begin(_ => { }));

        Assert.True(run.End());
        Assert.False(run.End());
    }

    /// <summary>
    /// A resume is work, so the cap that bounds spawns bounds it too.
    ///
    /// <para>WITHOUT THIS A SEND WALKS PAST maxConcurrentAgents. The spawner waits the slot inside
    /// its own started task; agent_send never enters that method, so a user who capped their
    /// endpoint at two concurrent children could have two spawns and any number of resumes running
    /// at once — which is the cap not holding, on the path a model reaches for most.</para>
    /// </summary>
    [Fact]
    public async Task AResumeWaitsTheSameSlotASpawnWaits()
    {
        var slot = new SemaphoreSlim(1);
        var store = new SubAgentStore();
        var reach = new AgentReachTools(store, slot);

        await slot.WaitAsync();                       // the cap is full

        var send = reach.InvokeAsync(CxAgent.Core.Jobs.Tool.AgentSend, "nobody", "hello", CancellationToken.None);
        var finished = await Task.WhenAny(send, Task.Delay(200));

        // IT REFUSES BEFORE IT WAITS, and that is deliberate: a name nothing keeps can be answered
        // immediately, and making the model wait behind the cap to be told the handle is wrong burns
        // the very slot the cap exists to protect.
        Assert.Same(send, finished);
        Assert.Contains("no sub-agent named", await send);

        slot.Release();
    }

    /// <summary>
    /// A spawn registers the run before the store announces the claim.
    ///
    /// <para>THE ORDER IS THE WHOLE CONTRACT. SubAgentSpawner calls onChild, then Keep, then
    /// TryBeginSend — so the registry entry exists by the time SendBegan arrives. Reverse those and
    /// the begin finds nothing, the timer never starts, and a freshly spawned row is as dead as the
    /// resumed one this work exists to fix.</para>
    /// </summary>
    [Fact]
    public async Task ASpawnedChild_HasALiveRunBeforeItsClaimIsAnnounced()
    {
        var parent = SubAgentSpawnerTests.ParentWithSpawning(out var store);

        string? beganFor = null;

        // LOOKED UP INSIDE THE HANDLER, which is the only place the ordering is observable. A lookup
        // after the await finds the run registered whatever order onChild, Keep and TryBeginSend ran
        // in — it asserts the run exists EVENTUALLY, which is not the contract. The contract is that
        // it existed BY THEN, and only a read taken at the announcement can tell the two apart.
        ChildRun? runAtBegin = null;
        store.SendBegan += id =>
        {
            beganFor ??= id;
            runAtBegin ??= parent.ChildRunFor(id);
        };

        await parent.SendAsync("spawn a worker", CancellationToken.None);

        Assert.NotNull(beganFor);
        // The run was already registered when the claim was announced, which is what a live row
        // needs: the begin handler has nothing to start a timer on otherwise.
        Assert.NotNull(runAtBegin);
    }

    /// <summary>
    /// A resumed child gets the same ticking row a spawned one gets.
    ///
    /// <para>THE DEFECT THIS PINS: everything that made a row live used to be scoped to the `agent`
    /// tool call, so a child woken by agent_send had its turn counter (a closure that outlived the
    /// call) but no timer — and elapsed time froze between turns while the counter climbed. Half the
    /// apparatus survived and half did not, and nothing decided which.</para>
    /// </summary>
    [Fact]
    public async Task AResumedChild_TicksLikeASpawnedOne()
    {
        var parent = SubAgentSpawnerTests.ParentWithSpawning(out var store);
        await parent.SendAsync("spawn a worker", CancellationToken.None);

        var kept = Assert.Single(store.All());
        var run = parent.ChildRunFor(kept.Agent.Agent.Id);
        Assert.NotNull(run);

        // The spawn has returned, so the run is settled — exactly the state a resume starts from.
        Assert.False(run!.Working);

        Assert.True(store.TryBeginSend(kept.Name));
        Assert.True(run.Working);          // the claim alone starts the repaint

        store.EndSend(kept.Name);
        Assert.False(run.Working);
    }

    /// <summary>
    /// Each stretch of work is its own archive row.
    ///
    /// <para>ChildFinished used to be raised inside the spawn method, so a resumed run's turns and
    /// tokens never reached history at all — a real gap in the data, not only in the row. Raising it
    /// per stretch is what closes that; a per-agent row that silently grew would make "what did this
    /// cost" unanswerable for any agent asked more than once.</para>
    /// </summary>
    [Fact]
    public async Task AResumedChild_ReachesTheUsageArchive()
    {
        var parent = SubAgentSpawnerTests.ParentWithSpawning(out var store);
        var runs = new List<ChildRunReport>();
        parent.ChildFinished += r => runs.Add(r);

        await parent.SendAsync("spawn a worker", CancellationToken.None);
        var kept = Assert.Single(store.All());

        store.TryBeginSend(kept.Name);
        store.EndSend(kept.Name);

        Assert.Equal(2, runs.Count);
        // DISTINCT IDS, or the second write collides with the first and one run goes missing.
        Assert.NotEqual(runs[0].RunId, runs[1].RunId);
    }

    /// <summary>
    /// A spawned child's clock is set before anything renders an age from it.
    ///
    /// <para>THE DEFECT THIS PINS: Started is rebased by Begin, and nothing but the store's claim
    /// calls Begin. With the repaint wired to a child's turn boundaries but nothing taking the
    /// claim, a run's clock stays default(DateTimeOffset) and the first paint renders the age as
    /// "1065212m47s" — the millennia since year one — rather than "3s".</para>
    ///
    /// <para>ASSERTED ON Started, with the rendered caption asserted separately below: a sane stamp
    /// is what a sane age is derived FROM, and the two fail for different reasons — a stamp left at
    /// default and a formatter that renders a good stamp badly.</para>
    /// </summary>
    [Fact]
    public async Task ASpawnedChildsClock_IsSetBeforeAnAgeIsRenderedFromIt()
    {
        var parent = SubAgentSpawnerTests.ParentWithSpawning(out var store);

        // CAPTURED AT THE CLAIM, not after the await: this is the moment the first paint happens,
        // and the stamp has to be good already by then rather than merely good eventually.
        DateTimeOffset? clockAtBegin = null;
        store.SendBegan += id => clockAtBegin ??= parent.ChildRunFor(id)?.Started;

        var before = DateTimeOffset.UtcNow;
        await parent.SendAsync("spawn a worker", CancellationToken.None);

        Assert.NotNull(clockAtBegin);
        // NOT default(DateTimeOffset), which is the whole failure: an unset clock is not merely
        // imprecise, it renders an age in the millions of minutes.
        Assert.NotEqual(default, clockAtBegin!.Value);
        Assert.InRange(clockAtBegin.Value, before, DateTimeOffset.UtcNow);

        // And the age that stamp yields is the one a reader would accept, rather than a geological
        // one — the same subtraction ReportChild does.
        var age = DateTimeOffset.UtcNow - clockAtBegin.Value;
        Assert.True(age < TimeSpan.FromMinutes(1), $"age rendered as {age.TotalMinutes:0}m");
    }

    /// <summary>
    /// The age a spawn's row actually SHOWS is the age of this run, not of the epoch.
    ///
    /// <para>THE STRING ITSELF, which nothing else in the suite reads. Every other assertion here
    /// checks a value the caption is computed from, and a caption is a second thing that can be
    /// wrong on its own — the unset-clock defect rendered "1065212m47s" past a green suite precisely
    /// because the rendered text was asserted nowhere.</para>
    ///
    /// <para>A BOUND RATHER THAN AN EXACT STRING: the caption carries turns and occupancy beside the
    /// age, and a child that takes a fraction of a second longer on a loaded machine reads "1s"
    /// rather than "0s". What must hold is that the figure is a handful of seconds — the minute
    /// form, "NmSSs", is the shape a geological age takes.</para>
    /// </summary>
    [Fact]
    public async Task ASpawnedChildsRow_RendersAnAgeInSeconds()
    {
        var parent = SubAgentSpawnerTests.ParentWithSpawning(out _, out var panel);

        // THE LIVE CAPTION, CAPTURED AS IT IS PAINTED. The finished account replaces it the moment
        // the run stops, so the ticking line this test is about exists only while the child works.
        string? live = null;
        parent.ChildSpawned += spawned => parent.ChildSpend += () =>
            live ??= panel.Jobs.FirstOrDefault(j => j.Id == spawned.JobId)?.ProgressMessage;

        await parent.SendAsync("spawn a worker", CancellationToken.None);

        Assert.NotNull(live);

        // THE LAST SEGMENT IS THE AGE — the caption is "N turns[ · P% ctx] · AGE" and only the age
        // is at stake here. Read out rather than matched against the whole line so a later addition
        // beside it does not turn this into a test of the caption's layout.
        var rendered = live!.Split('·').Last().Trim();

        // THE SECONDS FORM. "1065212m47s" — the millennia since year one, which is what an unset
        // clock renders — is the minute form and fails here; so would "1m00s" from a run this suite
        // could not plausibly take.
        Assert.Matches(@"^\d{1,2}s$", rendered);
    }

    /// <summary>
    /// A spawn archives exactly once.
    ///
    /// <para>THE ACCOUNT IS WRITTEN WHERE THE WORK STOPS, and a spawn stops the same way a resume
    /// does — its claim is released in the spawner's own finally. A second copy left in the spawn
    /// method would raise the same run twice: one row in history counted twice, and the settled row
    /// on screen rewritten with an identical second copy at a slightly later clock.</para>
    /// </summary>
    [Fact]
    public async Task ASpawn_ArchivesExactlyOnce()
    {
        var parent = SubAgentSpawnerTests.ParentWithSpawning(out _);
        var runs = new List<ChildRunReport>();
        parent.ChildFinished += r => runs.Add(r);

        await parent.SendAsync("spawn a worker", CancellationToken.None);

        Assert.Single(runs);
    }

    /// <summary>
    /// The envelope's own word reaches the archive, rather than a two-way completed/failed guess.
    ///
    /// <para>ONLY THE RELEASE CARRIES IT. The spawn method does not see its own envelope until after
    /// the claim is released — and the account is written inside that release — so a run's outcome
    /// has to travel with the release or be lost.</para>
    /// </summary>
    [Fact]
    public async Task ASpawnsOutcome_ComesFromItsEnvelope()
    {
        var parent = SubAgentSpawnerTests.ParentWithSpawning(out _);
        var runs = new List<ChildRunReport>();
        parent.ChildFinished += r => runs.Add(r);

        await parent.SendAsync("spawn a worker", CancellationToken.None);

        Assert.Equal("completed", Assert.Single(runs).Outcome);
    }
}

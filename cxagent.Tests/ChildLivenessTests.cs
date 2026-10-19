using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Jobs;
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
    /// <summary>
    /// A parent whose child can be resumed and whose every answer carries usage.
    ///
    /// <para>SubAgentSpawnerTests.ParentWithSpawning GIVES ITS CHILD ONE ANSWER AND NO USAGE, which
    /// is right for a spawn and wrong for both things tested here: a second send dequeues from an
    /// empty queue and throws, and a child that reports no tokens cannot distinguish a per-stretch
    /// figure from a lifetime one — every arithmetic would agree at zero.</para>
    /// </summary>
    /// <param name="perAnswer">Input and output tokens each of the child's answers reports.</param>
    private static Agent ParentWhoseChildSpends(out SubAgentStore store, int perAnswer = 1000)
    {
        var parent = new MockLlmProvider();
        parent.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use",
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "call-1",
                    Name = CxAgent.Core.Jobs.Tool.Agent,
                    Arguments = System.Text.Json.JsonDocument.Parse(
                        System.Text.Json.JsonSerializer.Serialize(
                            new { description = "find thing", prompt = "find the thing" })).RootElement,
                },
            ],
        });
        parent.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });

        var child = new MockLlmProvider();
        // ENOUGH FOR THE SPAWN AND SEVERAL RESUMES. A Dequeue on an empty queue throws, and that
        // throw surfaces as the send's own failure rather than as a missing answer.
        for (var i = 0; i < 6; i++)
            child.EnqueueResponse(new LlmResponse
            {
                Text = $"answer {i}", StopReason = "end_turn",
                Usage = new LlmUsage { InputTokens = perAnswer, OutputTokens = perAnswer },
            });

        store = new SubAgentStore();
        var factory = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = child,
            Executors = JobRegistry.CreateWithBuiltins(),
            Ledger = new TokenLedger(),
            MaxTurns = 50,
            CompressAbove = 40_000,
            ContextWindow = 200_000,
        });

        return new Agent(parent, JobRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(factory, store: store))
        {
            Mode = AgentMode.FanOut,
        };
    }

    /// <summary>A model's foreground send, as the agent_send tool makes it.</summary>
    private static SendRequest Ask(string to, string prompt) =>
        new(To: to, Prompt: prompt, Background: false, ParentAgentId: "parent", FromUser: false);

    /// <summary>
    /// What a parent is built with, beyond the defaults every liveness test shares.
    ///
    /// <para>A RECORD, NOT A PARAMETER LIST: the background tests each need a different one or two of
    /// these, and a list of eight optionals read positionally is where two of them get transposed.</para>
    /// </summary>
    private sealed record ParentSetup
    {
        /// <summary>maxConcurrentAgents, or null for uncapped.</summary>
        public int? Cap { get; init; }

        /// <summary>The parent's job panel, when a test reads the rows.</summary>
        public IToolObserver? Jobs { get; init; }

        /// <summary>The child's provider; a mock with a few answers by default.</summary>
        public ILlmProvider? Child { get; init; }

        /// <summary>Where background reports go; none means spawns stay in the foreground.</summary>
        public IAgentDelivery? Delivery { get; init; }

        /// <summary>Whether the scripted spawn asks for the background.</summary>
        public bool Background { get; init; }

        /// <summary>A second tool call the parent makes after the spawn, before it answers.</summary>
        public ToolCall? FollowUp { get; init; }

        /// <summary>Several calls in ONE response after the spawn — for what the walk does with them.</summary>
        public IReadOnlyList<ToolCall>? FollowUps { get; init; }
    }

    /// <summary>A parent built from a setup, and what a test reaches through it.</summary>
    private sealed record ParentRig(Agent Agent, SubAgentStore Store, SubAgentFactory Factory);

    private static ParentRig Parent(ParentSetup setup)
    {
        var parent = new MockLlmProvider();
        parent.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use",
            ToolCalls = [Calls.Spawn(id: "call-1", background: setup.Background)],
        });
        if (setup.FollowUp is { } followUp)
            parent.EnqueueResponse(new LlmResponse { Text = "", StopReason = "tool_use", ToolCalls = [followUp] });
        if (setup.FollowUps is { Count: > 0 } followUps)
            parent.EnqueueResponse(new LlmResponse { Text = "", StopReason = "tool_use", ToolCalls = [.. followUps] });
        parent.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });

        var child = setup.Child;
        if (child is null)
        {
            var mock = new MockLlmProvider();
            for (var i = 0; i < 6; i++)
                mock.EnqueueResponse(new LlmResponse { Text = $"answer {i}", StopReason = "end_turn" });
            child = mock;
        }

        var store = new SubAgentStore();
        var factory = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = child,
            Executors = JobRegistry.CreateWithBuiltins(),
            Ledger = new TokenLedger(),
            MaxTurns = 50,
            CompressAbove = 40_000,
            ContextWindow = 200_000,
            MaxConcurrentAgents = setup.Cap,
            Delivery = setup.Delivery,
        });

        var agent = new Agent(parent, JobRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), setup.Jobs ?? new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(factory, store: store))
        {
            Mode = AgentMode.FanOut,
        };
        return new ParentRig(agent, store, factory);
    }

    /// <summary>A QUEUED CHILD'S ROW SAYS SO, and its clock starts only with a slot.</summary>
    [Fact]
    public async Task AQueuedChildsRow_SaysQueued_AndStartsTickingOnlyWithASlot()
    {
        var jobs = new BufferedJobPanel();
        var rig = Parent(new ParentSetup { Cap = 1, Jobs = jobs });
        await rig.Factory.ConcurrencySlot!.WaitAsync();

        var turn = rig.Agent.SendAsync("go", CancellationToken.None);
        Assert.True(await Poll.Until(() => jobs.Jobs.Any(j => j.ProgressMessage?.Contains("queued") == true)));

        rig.Factory.ConcurrencySlot.Release();
        await turn;
        Assert.Contains(jobs.Jobs, j => j.ProgressMessage?.StartsWith("done") == true);
    }

    /// <summary>
    /// THE ROW SETTLES WITH THE RECEIPT. The turn that spawned a background child ends long before the
    /// child does, and a row ticking in scrollback above the conversation reads as work still holding that
    /// turn open.
    /// </summary>
    [Fact]
    public async Task ABackgroundSpawnsRow_SettlesAtTheReceipt_AndStopsRepainting()
    {
        var jobs = new BufferedJobPanel();
        var child = new HeldProvider("found it");
        var rig = Parent(new ParentSetup { Jobs = jobs, Child = child, Delivery = new RecordingDelivery(), Background = true });

        await rig.Agent.SendAsync("go", CancellationToken.None);
        var row = Assert.Single(jobs.Jobs, j => j.PlanLocalId == Tool.Agent);
        Assert.Equal(JobState.Succeeded, row.State);
        Assert.StartsWith("started in background", row.ProgressMessage);

        var before = row.ProgressMessage;
        Assert.True(await Poll.Until(() => child.Calls == 1));
        await Task.Delay(2200);   // two ticks of the child's repaint
        Assert.Equal(before, jobs.Jobs.Single(j => j.PlanLocalId == Tool.Agent).ProgressMessage);
        child.Release();
    }

    /// <summary>AND ITS RUN IS ARCHIVED WHEN THE CHILD ENDS, not when the receipt is written.</summary>
    [Fact]
    public async Task ABackgroundRun_IsArchivedWhenTheChildEnds_NotAtTheReceipt()
    {
        var child = new HeldProvider("found it");
        var rig = Parent(new ParentSetup { Child = child, Delivery = new RecordingDelivery(), Background = true });
        var finished = new List<ChildRunReport>();
        rig.Agent.ChildFinished += r => { lock (finished) finished.Add(r); };

        await rig.Agent.SendAsync("go", CancellationToken.None);
        lock (finished) Assert.Empty(finished);

        child.Release();
        Assert.True(await Poll.Until(() => { lock (finished) return finished.Count == 1; }));
        Assert.Equal("completed", finished[0].Outcome);
    }

    /// <summary>A TOOL RESULT THAT MERELY LOOKS LIKE A RECEIPT is not one — only a spawn's is.</summary>
    [Fact]
    public async Task AReadFileOfReceiptText_DoesNotSettleAsBackground()
    {
        var dir = Path.Combine(Path.GetTempPath(), "receipt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "receipt.txt"), SubAgentEnvelope.Receipt(new EnvelopeChild("id", null), "x", canWrite: false));
            var model = new MockLlmProvider();
            model.EnqueueResponse(new LlmResponse
            {
                Text = "", StopReason = "tool_use",
                ToolCalls = [Calls.Named(Tool.ReadFile, new { path = Path.Combine(dir, "receipt.txt") })],
            });
            model.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });
            var jobs = new BufferedJobPanel();
            var agent = new Agent(model, JobRegistry.CreateWithBuiltins(), new TokenLedger(), new RecordingSink(),
                jobs, logs: null, maxTurns: 50, workingDir: dir);

            await agent.SendAsync("read it", CancellationToken.None);

            var row = Assert.Single(jobs.Jobs);
            Assert.DoesNotContain("started in background", row.ProgressMessage ?? "");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>`name` STILL RESOLVES: a model with habits, or a conversation saved with that argument, sends it.</summary>
    [Fact]
    public async Task AgentSend_AcceptsTheOldNameArgument()
    {
        var child = new MockLlmProvider();
        child.EnqueueResponse(new LlmResponse { Text = "first", StopReason = "end_turn" });
        child.EnqueueResponse(new LlmResponse { Text = "more done", StopReason = "end_turn" });
        var rig = Parent(new ParentSetup
        {
            Child = child,
            FollowUp = Calls.Named(Tool.AgentSend, new { name = "find-thing", prompt = "now the lexer", background = false }, id: "s2"),
        });

        await rig.Agent.SendAsync("go", CancellationToken.None);

        Assert.Contains("now the lexer", child.Text());
    }

    /// <summary>
    /// A WAIT DOES NOT DELAY THE TOOLS AFTER IT. task_output with wait is held on the walk like a spawn,
    /// so the read emitted after it in the same response runs while the wait is still waiting.
    /// </summary>
    [Fact]
    public async Task ATaskOutputWait_DoesNotDelayTheToolsAfterIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wait-walk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "a note");
        var jobs = new BufferedJobPanel();
        var child = new HeldProvider("found it");
        try
        {
            var rig = Parent(new ParentSetup
            {
                Jobs = jobs, Child = child, Delivery = new RecordingDelivery(), Background = true,
                FollowUps =
                [
                    Calls.Named(Tool.TaskOutput, new { id = "find-thing", wait = true, timeout = 10 }, id: "w1"),
                    Calls.Named(Tool.ReadFile, new { path = Path.Combine(dir, "notes.txt") }, id: "r1"),
                ],
            });

            var turn = rig.Agent.SendAsync("go", CancellationToken.None);

            Assert.True(await Poll.Until(() =>
                jobs.Jobs.Any(j => j.PlanLocalId == Tool.ReadFile && j.State == JobState.Succeeded)));
            Assert.False(turn.IsCompleted);          // the wait is still waiting
            child.Release();
            await turn;
        }
        finally { Directory.Delete(dir, true); }
    }

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
    /// A handle nothing keeps is refused at once, without queueing behind the cap.
    ///
    /// <para>THIS PINS AN ORDERING, NOT THE CAP — the send never reaches the semaphore, so the wait
    /// itself is unasserted here and AResumeIsHeldByAFullCap is what proves it. Making the model
    /// wait behind a full cap to be told its handle is wrong burns the very permit the cap exists to
    /// protect, on a call that was never going to start any work.</para>
    /// </summary>
    [Fact]
    public async Task AnUnknownHandleIsRefusedWithoutWaitingTheCap()
    {
        var slot = new SemaphoreSlim(1);
        var store = new SubAgentStore();
        var reach = new AgentReachTools(store, slot);

        await slot.WaitAsync();                       // the cap is full

        var send = reach.SendAsync(Ask("nobody", "hello"), CancellationToken.None);
        var finished = await Task.WhenAny(send, Task.Delay(200));

        // IT REFUSES BEFORE IT WAITS, and that is deliberate: a name nothing keeps can be answered
        // immediately, and making the model wait behind the cap to be told the handle is wrong burns
        // the very slot the cap exists to protect.
        Assert.Same(send, finished);
        Assert.Contains("no sub-agent named", await send);

        slot.Release();
    }

    /// <summary>
    /// A resume of a child that IS kept waits the cap, and runs when a permit frees.
    ///
    /// <para>WITHOUT THIS A SEND WALKS PAST maxConcurrentAgents. The spawner waits the slot inside
    /// its own started task; agent_send never enters that method, so a user who capped their
    /// endpoint at two concurrent children could have two spawns and any number of resumes running
    /// at once — which is the cap not holding, on the path a model reaches for most.</para>
    ///
    /// <para>BOTH HALVES ARE THE TEST. That the send does not finish while the cap is full is what a
    /// no-op WaitAsync would break; that it finishes once a permit is released is what a wait
    /// nothing ever releases would break, and a test asserting only the first would pass for a send
    /// that had simply deadlocked.</para>
    /// </summary>
    [Fact]
    public async Task AResumeIsHeldByAFullCap_AndRunsWhenAPermitFrees()
    {
        var slot = new SemaphoreSlim(1);
        var parent = ParentWhoseChildSpends(out var store);
        await parent.SendAsync("spawn a worker", CancellationToken.None);
        var kept = Assert.Single(store.All());

        var began = new List<string>();
        store.SendBegan += id => began.Add(id);

        var reach = new AgentReachTools(store, slot);
        await slot.WaitAsync();                       // the cap is full

        var send = reach.SendAsync(Ask(kept.Name, "more please"), CancellationToken.None);

        // HELD. A genuinely kept child has passed every refusal, so the only thing that can stop it
        // here is the semaphore.
        Assert.NotSame(send, await Task.WhenAny(send, Task.Delay(200)));

        // AND NOT SHOWN AS WORKING WHILE IT WAITS. The announcement is what starts the row's timer,
        // so a send announcing before it holds a permit would paint a ticking row for a child
        // sitting in a queue. The claim itself IS held across the wait — it is the exclusion that
        // keeps another sender from taking the child — so busy-ness is not what this asks about.
        Assert.Empty(began);

        slot.Release();
        Assert.Same(send, await Task.WhenAny(send, Task.Delay(5000)));
        await send;
    }

    /// <summary>
    /// A resume held by a full cap archives ONE run, not a phantom beside it.
    ///
    /// <para>THE CAP AND THE ARCHIVE ARE ONE TEST, which is the gap this closes. A test that
    /// subscribes to ChildFinished but passes no slot never enters the capped branch; a test that
    /// passes a slot but watches no archive cannot see what the branch writes. Both pass while a
    /// queued resume files a row of zero turns and zero tokens — and StatsQuery counts that row in
    /// Runs and averages its zeroes into AvgTurns and AvgDurationMs, so a single capped resume drags
    /// the reported cost of every sub-agent of that type toward nothing.</para>
    ///
    /// <para>THE CAUSE IT PINS: releasing the claim is the application's one announcement that a
    /// child's work has STOPPED, so a send that hands its claim back in order to wait for a permit
    /// says the child finished before it ever started.</para>
    /// </summary>
    [Fact]
    public async Task AResumeQueuedBehindAFullCap_ArchivesNoPhantomRun()
    {
        var slot = new SemaphoreSlim(1);
        var parent = ParentWhoseChildSpends(out var store, perAnswer: 1000);
        var runs = new List<ChildRunReport>();
        parent.ChildFinished += r => runs.Add(r);

        await parent.SendAsync("spawn a worker", CancellationToken.None);
        var kept = Assert.Single(store.All());
        Assert.Single(runs);                          // the spawn's own row

        var reach = new AgentReachTools(store, slot);
        await slot.WaitAsync();                       // the cap is full

        var send = reach.SendAsync(Ask(kept.Name, "more please"), CancellationToken.None);

        // QUEUED, AND NOTHING FILED FOR IT YET. A row written here is a run that has not happened.
        Assert.NotSame(send, await Task.WhenAny(send, Task.Delay(200)));
        Assert.Single(runs);

        slot.Release();
        Assert.Same(send, await Task.WhenAny(send, Task.Delay(5000)));
        await send;

        // EXACTLY TWO: the spawn and the one resume that ran.
        Assert.Equal(2, runs.Count);

        // AND THE SECOND IS THE REAL ONE. A phantom is recognisable by its emptiness, so asserting
        // the resume's row carries its answer's cost is what distinguishes "two rows" from "two rows
        // of which one is a ghost".
        Assert.Equal(1000, runs[1].InputTokens);
        Assert.Equal(1000, runs[1].OutputTokens);
        Assert.Equal(1, runs[1].Turns);
        Assert.DoesNotContain(runs, r => r.Turns == 0 && r.InputTokens == 0 && r.OutputTokens == 0);
    }

    /// <summary>
    /// A WAKE DOES NOT STOP THE TURN'S OTHER TOOLS, exactly as a spawn does not.
    ///
    /// <para>THE DEFECT THIS PINS: the walk's held-not-awaited branch matched only the SPAWN tool's
    /// name, so an <c>agent_send</c> was awaited inline and blocked every call emitted after it. A
    /// wake hands work to a child that runs for minutes, so a read emitted beside it waited on a
    /// sibling with nothing to do with it — and on screen the send's own row sat unfinished while
    /// the child's row advanced, which reads as the send completing after the work it started.</para>
    ///
    /// <para>ASSERTED BY CONSTRUCTION, NOT BY TIMING. The woken child blocks inside its provider
    /// until this test releases it; the assertion is that the LATER tool call has already recorded
    /// its result while that block is still held. Under an inline await the result cannot be there
    /// yet, so the test fails on a deadline rather than hanging.</para>
    /// </summary>
    [Fact]
    public async Task AWakeDoesNotBlockTheToolsEmittedAfterIt()
    {
        var parent = SubAgentSpawnerTests.ParentWhoseChildBlocksOnWake(
            out var store, out var release, out var wokeUp);

        var run = parent.SendAsync("spawn, then wake it and read a file", CancellationToken.None);

        // The child must be inside its provider before anything is judged: until then a missing
        // inline result proves nothing about ordering.
        Assert.True(await wokeUp.WaitAsync(TimeSpan.FromSeconds(10)),
            "the woken child never reached its provider");

        // THE READ EMITTED AFTER THE WAKE HAS ALREADY ANSWERED, while the wake is still blocked.
        // Under an inline await this is impossible — the walk would be parked on the send.
        var answered = await SubAgentSpawnerTests.WaitForToolResult(parent, "r1",
            TimeSpan.FromSeconds(10));
        Assert.True(answered,
            "the tool emitted after agent_send did not run while the wake was still in flight — "
          + "the walk is awaiting the wake inline");

        release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        // And the wake answered too: the barrier still holds every call to a result.
        var results = parent.Context.Messages
            .Where(m => m.Role == "tool" && m.ToolCallId is not null)
            .Select(m => m.ToolCallId!)
            .ToList();
        Assert.Contains("w1", results);
        Assert.Contains("r1", results);
        Assert.NotNull(store);
    }

    /// <summary>
    /// A spawn registers the run before the store announces the claim.
    ///
    /// <para>THE ORDER IS THE WHOLE CONTRACT. SubAgentSpawner calls onChild, then Keep, then
    /// claims and announces it — so the registry entry exists by the time SendBegan arrives. Reverse those and
    /// the begin finds nothing, the timer never starts, and a freshly spawned row is as dead as the
    /// resumed one this work exists to fix.</para>
    /// </summary>
    [Fact]
    public async Task ASpawnedChild_HasALiveRunBeforeItsClaimIsAnnounced()
    {
        var parent = SubAgentSpawnerTests.ParentWithSpawning(out var store);

        string? beganFor = null;

        // LOOKED UP INSIDE THE HANDLER, which is the only place the ordering is observable. A lookup
        // after the await finds the run registered whatever order onChild, Keep and the announcement ran
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

        Assert.True(store.ClaimAndAnnounce(kept.Name));
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

        store.ClaimAndAnnounce(kept.Name);
        store.EndSend(kept.Name);

        Assert.Equal(2, runs.Count);
        // DISTINCT IDS, or the second write collides with the first and one run goes missing.
        Assert.NotEqual(runs[0].RunId, runs[1].RunId);
    }

    /// <summary>
    /// Each archive row carries what ITS stretch cost, not the child's running total.
    ///
    /// <para>THE COUNT AND THE IDS ARE NOT ENOUGH, which is why this sits beside the test above.
    /// Rows keyed apart are summed by StatsQuery — <c>Sum(InputTokens + OutputTokens)</c> per type —
    /// so a row carrying the lifetime tally counts the first stretch again in the second and again
    /// in the third. A child costing 30k across three sends is then reported as 60k, and the error
    /// grows with the number of resumes rather than staying a fixed offset.</para>
    ///
    /// <para>SUMMING THE ROWS IS THE ASSERTION, because that is what the consumer does. A test
    /// checking only the last row would pass for figures that are individually plausible and wrong
    /// in aggregate, which is exactly the shape of this defect.</para>
    /// </summary>
    [Fact]
    public async Task EachStretchsArchiveRow_CarriesThatStretchsCost()
    {
        var parent = ParentWhoseChildSpends(out var store, perAnswer: 1000);
        var runs = new List<ChildRunReport>();
        parent.ChildFinished += r => runs.Add(r);

        await parent.SendAsync("spawn a worker", CancellationToken.None);
        var kept = Assert.Single(store.All());
        var spentAfterSpawn = kept.Agent.Agent.Spend;

        var reach = new AgentReachTools(store);
        await reach.SendAsync(Ask(kept.Name, "more"), CancellationToken.None);
        await reach.SendAsync(Ask(kept.Name, "more again"), CancellationToken.None);

        Assert.Equal(3, runs.Count);

        // The spawn's row is the child's whole cost so far, because nothing preceded it.
        Assert.Equal(spentAfterSpawn.Input, runs[0].InputTokens);
        Assert.Equal(spentAfterSpawn.Output, runs[0].OutputTokens);

        // AND THE RESUMES CARRY ONLY THEIR OWN. Each is one answer of 1000/1000; a lifetime tally
        // would report 2000 and then 3000 here.
        Assert.Equal(1000, runs[1].InputTokens);
        Assert.Equal(1000, runs[1].OutputTokens);
        Assert.Equal(1000, runs[2].InputTokens);
        Assert.Equal(1000, runs[2].OutputTokens);

        // WHAT THE ARCHIVE'S CONSUMER COMPUTES: the rows sum to what the child actually cost, which
        // is the property the per-stretch split exists to preserve.
        var (finalIn, finalOut) = kept.Agent.Agent.Spend;
        Assert.Equal(finalIn + finalOut, runs.Sum(r => r.InputTokens + r.OutputTokens));

        // TURNS THE SAME WAY. StatsQuery AVERAGES them per type, so a cumulative count would make
        // "turns per run" climb with every resume of any one child — two identical resumes must
        // report the same figure rather than 1 and then 2.
        Assert.Equal(runs[1].Turns, runs[2].Turns);
        Assert.Equal(1, runs[1].Turns);
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

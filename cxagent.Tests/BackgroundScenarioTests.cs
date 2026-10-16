using CxAgent.Core.Agents;
using CxAgent.Core.Commands;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The background-agent scenario matrix, end to end: a real session, a real delivery, a real spawner.
///
/// <para>EACH ROW OVER THE WHOLE PATH, not over the piece that decides it. The unit tests beside these
/// pin each decision on its own; what they cannot see is two correct pieces composed wrongly — a report
/// built right and handed to a delivery that wakes the wrong agent, or a mailbox drained a lap late.
/// Every test here starts at <c>Session.Submit</c> and ends at what the parent's model was sent.</para>
/// </summary>
public class BackgroundScenarioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bg-scenario-" + Guid.NewGuid().ToString("N"));
    private SessionManager? _manager;

    public BackgroundScenarioTests() => Directory.CreateDirectory(_dir);

    /// <summary>
    /// Disposes the manager, then removes the temp root, tolerating a write still landing in it.
    ///
    /// <para>A WAKE IS A TURN NOBODY AWAITS — the behaviour under test — so its log may still be written
    /// while the directory goes. Cleanup of a temp folder must not fail a test that passed.</para>
    /// </summary>
    public void Dispose()
    {
        _manager?.Dispose();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }

    // ---- the rig ---------------------------------------------------------------------------------------

    /// <summary>
    /// What a scene varies. Every member defaults to the ordinary case, so a test names only what its
    /// row is about.
    /// </summary>
    private sealed record SceneSetup
    {
        /// <summary>The provider every agent type's children answer from.</summary>
        public required ILlmProvider Child { get; init; }

        /// <summary>
        /// A second child provider for the <c>explore</c> type — for the rows that need two children
        /// finishing at different moments, which one shared provider cannot arrange.
        /// </summary>
        public ILlmProvider? Explore { get; init; }

        /// <summary>maxConcurrentAgents, or null for uncapped.</summary>
        public int? Cap { get; init; }

        /// <summary>The permission gate, or null for none — every tool runs ungated.</summary>
        public IPermissionGate? Gate { get; init; }

        /// <summary>The general type's turn ceiling, for the capped-child row.</summary>
        public int? ChildMaxTurns { get; init; }

        /// <summary>A third instance, named "spare", for the row that switches the session's model.</summary>
        public ILlmProvider? Spare { get; init; }
    }

    /// <summary>A running scene, and what a test reads from it.</summary>
    private sealed record Scene(Session Session, ScriptedProvider Parent, BufferedChatSink Sink, ResolvedConfig Config);

    /// <summary>
    /// A FanOut session whose parent answers from a script and whose children answer from their own
    /// provider instance.
    ///
    /// <para>TWO INSTANCES, EVERY CHILD TYPE ROUTED TO ITS OWN — one shared provider would interleave
    /// parent and child requests, and every row here is about the ORDER the parent reads things in. This
    /// is AgentDeliveryTests.AReport_ReachesAParentThatSwitchedToSingle's wiring, extended to every
    /// built-in type so a planner or an explorer is routed too.</para>
    /// </summary>
    private Scene Open(ScriptedProvider parent, SceneSetup setup)
    {
        var providers = new Dictionary<string, ILlmProvider> { ["local"] = parent, ["kid"] = setup.Child };
        if (setup.Explore is { } explore) providers["fg"] = explore;
        if (setup.Spare is { } spare) providers["spare"] = spare;

        var types = new Dictionary<string, AgentTypeConfig>();
        foreach (var name in BuiltinAgentTypes.All.Select(t => t.Name).Append(AgentTypeCatalog.DefaultTypeName))
            types[name] = new AgentTypeConfig("", Provider: "kid");
        if (setup.Explore is not null) types["explore"] = new AgentTypeConfig("", Provider: "fg");
        if (setup.ChildMaxTurns is { } turns)
            types[AgentTypeCatalog.DefaultTypeName] = new AgentTypeConfig("", Provider: "kid", MaxTurns: turns);

        var registry = ProviderRegistry.FromProviders(providers, "local");
        var config = new ResolvedConfig(registry.Use("local"),
            new ProviderCatalog(Instances: registry, AgentTypes: types, MaxConcurrentAgents: setup.Cap), []);

        _manager = SessionManager.Create(new ProcessSetup
        {
            Paths = new AppPaths(_dir),
            BuildGate = setup.Gate is { } gate ? _ => gate : null,
        });
        var sink = new BufferedChatSink();
        var session = _manager.Open(_dir, config,
            new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel() }, AgentMode.FanOut);
        return new Scene(session, parent, sink, config);
    }

    private Scene Open(ScriptedProvider parent, ILlmProvider child) => Open(parent, new SceneSetup { Child = child });

    /// <summary>Waits until no turn is running or owned.</summary>
    private static async Task Idle(Session s) => Assert.True(await Poll.Until(() => !s.IsBusy && !s.TurnOwned),
        "the session never went idle");

    /// <summary>Starts a user turn and waits for it to end.</summary>
    private static async Task Turn(Session s, string text)
    {
        await Assert.IsType<Session.SubmitOutcome.Started>(s.Submit(text)).Turn;
        await Idle(s);
    }

    /// <summary>
    /// Lets a moment pass and answers whether the parent stayed at this many requests — for the rows
    /// whose point is that NOTHING more happens.
    ///
    /// <para>A NEGATIVE NEEDS A WINDOW, and this one is short on purpose: every wake in this file starts
    /// within a few milliseconds of its cause, so 200 ms is many times the latency being ruled out
    /// without being a sleep the suite pays for twice.</para>
    /// </summary>
    private static async Task<bool> StaysAt(ScriptedProvider p, int calls)
    {
        await Task.Delay(200);
        return p.Calls == calls;
    }

    private static LlmResponse Answer(string text) => new() { Text = text, StopReason = "end_turn" };

    private static LlmResponse Use(params ToolCall[] calls) => new() { Text = "", StopReason = "tool_use", ToolCalls = [.. calls] };

    private static BackgroundRun RunOf(Scene s, string name = "find-thing") =>
        s.Session.SubAgents.BackgroundOf(name) ?? throw new InvalidOperationException($"no run '{name}'");

    // ---- S1: idle ----------------------------------------------------------------------------------------

    /// <summary>
    /// S1. A CHILD FINISHING WHILE ITS PARENT IS IDLE WAKES ONE TURN, draws its note, and the report is
    /// what that turn reads.
    ///
    /// <para>Guards BackgroundRunner.RunAsync's <c>work.Delivery.Tell(...)</c> for an unstopped run: a
    /// Leave there would sit in the mailbox until the user typed.</para>
    /// </summary>
    [Fact]
    public async Task Idle_Finish_WakesOnce()
    {
        var parent = new ScriptedProvider(Use(Calls.Spawn(id: "s1")), Answer("started it"), Answer("it found it"));
        var child = new HeldProvider("found it");
        var s = Open(parent, child);

        await Turn(s.Session, "go");
        Assert.Equal(2, parent.Calls);

        child.Release();
        Assert.True(await Poll.Until(() => parent.Calls == 3));
        await Idle(s.Session);

        Assert.Contains("found it", parent.Last);
        Assert.Contains("You spawned it to: find thing", parent.Last);
        Assert.Contains(s.Sink.Notices, n => n.Contains("background agent 'find-thing' finished"));
        Assert.True(await StaysAt(parent, 3), "a single report started more than one turn");
        Assert.Equal(1, parent.MaxInFlight);
    }

    // ---- S2, S5, S6: busy — the report waits for the next place the loop looks ---------------------------

    /// <summary>
    /// S2. A REPORT LANDING BETWEEN LAPS JOINS THE RUNNING TURN: read by its next lap, no second loop,
    /// and its note drawn the moment it arrives.
    ///
    /// <para>Guards Agent.RunLoopAsync's top-of-lap <c>Mailbox.Drain() is { Count: &gt; 0 } arrived</c>:
    /// a drain that does not put what it took in front of the model loses the report.</para>
    /// </summary>
    [Fact]
    public async Task BetweenLaps_Finish_JoinsTheRunningTurn()
    {
        var hold = new TaskCompletionSource();
        var parent = new ScriptedProvider(Use(Calls.Spawn(id: "s1")))
            .Then(Use(Calls.Named(Tool.TaskList, new { }, id: "l1")), hold.Task)
            .Then(Answer("read it"));
        var child = new HeldProvider("found it");
        var s = Open(parent, child);

        var turn = Assert.IsType<Session.SubmitOutcome.Started>(s.Session.Submit("go")).Turn;
        Assert.True(await Poll.Until(() => parent.Calls == 2));

        child.Release();
        Assert.True(await Poll.Until(() => s.Sink.Notices.Any(n => n.Contains("background agent 'find-thing' finished"))),
            "the note was not drawn when the report arrived");

        hold.SetResult();
        await turn;
        await Idle(s.Session);

        Assert.Equal(3, parent.Calls);
        Assert.Contains("found it", parent.Last);
        Assert.True(await StaysAt(parent, 3));
        Assert.Equal(1, parent.MaxInFlight);
    }

    /// <summary>
    /// S5. A REPORT ARRIVING WHILE THE PARENT WAITS ON A FOREGROUND CHILD is read after that child
    /// answers — the barrier is not broken, and no second loop starts for it.
    ///
    /// <para>Guards the same top-of-lap drain as S2, reached from the far side of a foreground spawn.</para>
    /// </summary>
    [Fact]
    public async Task WhileWaitingOnAForegroundChild_ReadAfterTheBarrier()
    {
        var parent = new ScriptedProvider(
            Use(Calls.Spawn(id: "s1")),
            Use(Calls.Named(Tool.Agent, new { description = "check it", prompt = "check the thing", type = "explore", background = false }, id: "s2")),
            Answer("both in"));
        var background = new HeldProvider("found it");
        var foreground = new HeldProvider("checked it");
        var s = Open(parent, new SceneSetup { Child = background, Explore = foreground });

        s.Session.Submit("go");
        Assert.True(await Poll.Until(() => foreground.Calls == 1));

        background.Release();
        Assert.True(await Poll.Until(() => RunOf(s).Handoff.Ended));
        Assert.True(await StaysAt(parent, 2), "the report broke the barrier");

        foreground.Release();
        Assert.True(await Poll.Until(() => parent.Calls == 3));
        await Idle(s.Session);

        Assert.Contains("found it", parent.Last);
        Assert.Contains("checked it", parent.Last);
        Assert.True(await StaysAt(parent, 3));
        Assert.Equal(1, parent.MaxInFlight);
    }

    /// <summary>
    /// S6. A REPORT ARRIVING DURING A LONG TOOL CALL is read when the call returns — the shell here
    /// waits for a file the test writes only after the report has landed.
    ///
    /// <para>Guards the same top-of-lap drain as S2.</para>
    /// </summary>
    [Fact]
    public async Task DuringALongTool_ReadAfterIt()
    {
        var go = Path.Combine(_dir, "go");
        var parent = new ScriptedProvider(
            Use(Calls.Spawn(id: "s1")),
            Use(Calls.Named(Tool.RunShell, new { command = $"while [ ! -f '{go}' ]; do sleep 0.05; done; echo waited-out" }, id: "sh")),
            Answer("both in"));
        var child = new HeldProvider("found it");
        var s = Open(parent, child);

        s.Session.Submit("go");
        Assert.True(await Poll.Until(() => parent.Calls == 2));

        child.Release();
        Assert.True(await Poll.Until(() => RunOf(s).Handoff.Ended));
        Assert.True(await StaysAt(parent, 2), "the report was read while the tool still ran");

        File.WriteAllText(go, "");
        Assert.True(await Poll.Until(() => parent.Calls == 3));
        await Idle(s.Session);

        Assert.Contains("found it", parent.Last);
        Assert.Contains("waited-out", parent.Last);
        Assert.Equal(1, parent.MaxInFlight);
    }

    // ---- S7, S8: several at once -------------------------------------------------------------------------

    /// <summary>
    /// S7. TWO FINISHING TOGETHER ON AN IDLE PARENT: the first wakes it, the second joins that turn. Two
    /// notes, both reports read, one loop.
    ///
    /// <para>THE WAKE TURN'S REQUEST IS HELD until both reports have landed, so the second cannot slip in
    /// after the first turn ended and wake a turn of its own — which would also be one loop at a time,
    /// and would hide a second wake behind a MaxInFlight of one.</para>
    ///
    /// <para>Guards Session.Send's <c>Interlocked.CompareExchange(ref _turnOwned, 1, 0) != 0</c>: the
    /// ownership test that sends the second report into the running turn rather than a turn of its
    /// own.</para>
    /// </summary>
    [Fact]
    public async Task TwoFinishTogether_OneWakeOneJoin()
    {
        var hold = new TaskCompletionSource();
        var parent = new ScriptedProvider(
                Use(Calls.Spawn(id: "s1", description: "find one"), Calls.Spawn(id: "s2", description: "find two")),
                Answer("started both"))
            .Then(Answer("read the first"), hold.Task);
        var child = new HeldProvider("answer", "answer");
        var s = Open(parent, child);

        await Turn(s.Session, "go");
        child.Release();
        Assert.True(await Poll.Until(() => s.Sink.Notices.Count(n => n.Contains("background agent 'find-")) == 2),
            "both notes were not drawn");
        Assert.Equal(3, parent.Calls);

        hold.SetResult();
        await Idle(s.Session);

        Assert.Contains("background agent 'find-one'", parent.All);
        Assert.Contains("background agent 'find-two'", parent.All);
        Assert.InRange(parent.Calls, 3, 4);
        Assert.True(await StaysAt(parent, parent.Calls));
        Assert.Equal(1, parent.MaxInFlight);
    }

    /// <summary>
    /// S8. TEN FINISHING TOGETHER: every report read, never two requests at once.
    ///
    /// <para>Guards the same turn-ownership test as S7.</para>
    /// </summary>
    [Fact]
    public async Task TenFinishTogether_AllRead_OneLoop()
    {
        var spawns = Enumerable.Range(0, 10).Select(i => Calls.Spawn(id: $"s{i}", description: $"job {i}")).ToArray();
        var parent = new ScriptedProvider(Use(spawns), Answer("started ten"));
        var child = new HeldProvider();
        var s = Open(parent, child);

        await Turn(s.Session, "go");
        Assert.True(await Poll.Until(() => child.Calls == 10));
        child.Release();

        Assert.True(await Poll.Until(() => Enumerable.Range(0, 10).All(i => parent.All.Contains($"background agent 'job-{i}'"))),
            "not every report was read");
        await Idle(s.Session);
        Assert.Equal(1, parent.MaxInFlight);
        Assert.All(s.Session.SubAgents.Background(), r => Assert.Equal(BackgroundState.Finished, r.State));
    }

    // ---- S9, S11: when the message cannot be, or need not be, delivered -----------------------------------

    /// <summary>
    /// S9. A FULL MAILBOX REFUSES THE REPORT, and nothing is lost: task_list says it was not delivered and
    /// task_output hands it over.
    ///
    /// <para>Guards Session.Send's <c>return new SubmitOutcome.Refused(...)</c> on a full mailbox: answering
    /// Queued there tells the handoff the report landed when it was dropped.</para>
    /// </summary>
    [Fact]
    public async Task FullMailbox_EndToEnd()
    {
        var hold = new TaskCompletionSource();
        var parent = new ScriptedProvider(Use(Calls.Spawn(id: "s1")))
            .Then(Use(Calls.Named(Tool.TaskList, new { }, id: "l1")), hold.Task)
            .Then(Use(Calls.Named(Tool.TaskOutput, new { id = "find-thing" }, id: "o1")))
            .Then(Answer("read it"));
        var child = new HeldProvider("found it");
        var s = Open(parent, child);

        s.Session.Submit("go");
        Assert.True(await Poll.Until(() => parent.Calls == 2));
        for (var i = 0; i < AgentMailbox.MaxDepth; i++) Assert.True(s.Session.LeaveNote($"note {i}"));

        child.Release();
        Assert.True(await Poll.Until(() => RunOf(s).Handoff.Undelivered), "the refused report was not kept");

        hold.SetResult();
        Assert.True(await Poll.Until(() => parent.Calls == 4));
        await Idle(s.Session);

        Assert.Contains("report not delivered", parent.Requests[2]);
        Assert.Contains("found it", parent.Requests[3]);
        Assert.False(RunOf(s).Handoff.Undelivered);
    }

    /// <summary>
    /// S11. A WAIT THAT TIMES OUT SAYS IT IS STILL RUNNING, and the report then arrives as an ordinary
    /// message — the timed-out waiter did not take it.
    ///
    /// <para>Guards ResultHandoff.WaitAsync's <c>ForgetWaiterLocked()</c> on a timeout: a waiter still
    /// counted after it left takes the report to nobody.</para>
    /// </summary>
    [Fact]
    public async Task WaitTimesOut_ThenReportArrivesAsAMessage()
    {
        var parent = new ScriptedProvider(
            Use(Calls.Spawn(id: "s1")),
            Use(Calls.Named(Tool.TaskOutput, new { id = "find-thing", wait = true, timeout = 1 }, id: "o1")),
            Answer("still going"));
        var child = new HeldProvider("found it");
        var s = Open(parent, child);

        await Turn(s.Session, "go");
        Assert.Contains("Still running after 1s", parent.Last);

        child.Release();
        Assert.True(await Poll.Until(() => parent.Calls == 4));
        await Idle(s.Session);
        Assert.Contains("found it", parent.Last);
    }

    // ---- S14–S16, S18, S20: Escape and stops -------------------------------------------------------------

    /// <summary>
    /// S14. ESCAPE ON THE PARENT'S TURN LEAVES A BACKGROUND CHILD ALONE: it runs on, finishes, and
    /// reports.
    ///
    /// <para>NO LINE TO REVERT: the child runs on its own run's token, and nothing in CancelTurn reaches
    /// it. Proved red by INSERTING <c>SubAgents.StopAll();</c> into Session.CancelTurn — the regression
    /// this guards against.</para>
    /// </summary>
    [Fact]
    public async Task Escape_DoesNotTouchABackgroundChild()
    {
        var never = new TaskCompletionSource();
        var parent = new ScriptedProvider(Use(Calls.Spawn(id: "s1"))).Then(Answer("unreached"), never.Task);
        var child = new HeldProvider("found it");
        var s = Open(parent, child);

        s.Session.Submit("go");
        Assert.True(await Poll.Until(() => parent.Calls == 2 && child.Calls == 1));
        Assert.True(s.Session.CancelTurn());
        await Idle(s.Session);
        Assert.Equal(BackgroundState.Running, RunOf(s).State);

        child.Release();
        Assert.True(await Poll.Until(() => parent.Calls == 3));
        await Idle(s.Session);
        Assert.Contains("found it", parent.Last);
        Assert.Contains("state=\"completed\"", parent.Last);
    }

    /// <summary>
    /// S15. /tasks stop ON AN IDLE PARENT STARTS NOTHING; the cancelled report is read when the user next
    /// speaks.
    ///
    /// <para>Guards BackgroundRunner.RunAsync's <c>stopped ? Leave : Tell</c>: a Tell there spends a
    /// request telling the model about work the user ended.</para>
    /// </summary>
    [Fact]
    public async Task UserStop_Idle_NoWake_ReadNextTurn()
    {
        var parent = new ScriptedProvider(Use(Calls.Spawn(id: "s1")), Answer("started"), Answer("noted"));
        var child = new HeldProvider("never");
        var s = Open(parent, child);

        await Turn(s.Session, "go");
        Assert.True(await Poll.Until(() => child.Calls == 1));

        s.Session.StopTask("stop find-thing");
        Assert.True(await Poll.Until(() => RunOf(s).Handoff.Ended));
        Assert.True(await StaysAt(parent, 2), "a user's stop woke the parent");
        Assert.Contains(s.Sink.Notices, n => n.Contains("background agent 'find-thing' stopped"));

        await Turn(s.Session, "what now?");
        Assert.Contains("was stopped by the user", parent.Last);
        Assert.Contains("state=\"cancelled\"", parent.Last);
    }

    /// <summary>
    /// S16. THE PARENT'S OWN task_stop: the cancelled report is read at its next step, and — the parent
    /// having stopped it, not the user — the model may resume it.
    ///
    /// <para>Guards AgentReachTools.SendAsync's <c>{ StoppedByUser: true }</c> test: refusing every
    /// stopped child would refuse the parent its own.</para>
    /// </summary>
    [Fact]
    public async Task ParentStop_Read_Resumable()
    {
        var hold = new TaskCompletionSource();
        var parent = new ScriptedProvider(
                Use(Calls.Spawn(id: "s1")),
                Use(Calls.Named(Tool.TaskStop, new { id = "find-thing" }, id: "t1")))
            .Then(Use(Calls.Named(Tool.TaskList, new { }, id: "l1")), hold.Task)
            .Then(Use(Calls.Named(Tool.AgentSend, new { to = "find-thing", prompt = "carry on", background = false }, id: "a1")))
            .Then(Answer("done"));
        var child = new HeldProvider("resumed answer");
        var s = Open(parent, child);

        s.Session.Submit("go");
        Assert.True(await Poll.Until(() => parent.Calls == 3));
        Assert.True(await Poll.Until(() => RunOf(s).Handoff.Ended));
        Assert.False(RunOf(s).StoppedByUser);

        child.Release();
        hold.SetResult();
        Assert.True(await Poll.Until(() => parent.Calls == 5));
        await Idle(s.Session);

        Assert.Contains("was stopped before it finished.", parent.Requests[3]);
        Assert.Contains("resumed answer", parent.Last);
    }

    /// <summary>
    /// S18. A STOP RACING THE ANSWER: the user's stop lands while the child's model is returning its
    /// answer — before the runner has marked it answered, so the stop is accepted and the user is told
    /// "stopped". The parent must then be told the same thing: a cancelled report, left rather than woken.
    ///
    /// <para>Guards BackgroundRunner.RunAsync's <c>if (!run.MarkAnswered()) result = result with { Outcome =
    /// SendOutcome.Cancelled }</c>: without it the run finishes as Finished with a "completed" report that
    /// wakes the parent, while the run still says the user stopped it — so the parent's own agent_send to
    /// a child it was told completed is refused.</para>
    /// </summary>
    [Fact]
    public async Task StopRacingTheAnswer_EndToEnd()
    {
        var parent = new ScriptedProvider(Use(Calls.Spawn(id: "s1")), Answer("started"), Answer("read it"));
        Session? session = null;
        var child = new StoppingProvider("found it", () => session!.StopTask("stop find-thing"));
        var s = Open(parent, child);
        session = s.Session;

        await Turn(s.Session, "go");
        Assert.True(await Poll.Until(() => RunOf(s).Handoff.Ended));

        // THE STOP WAS ACCEPTED: the user was told so, and the run remembers the user did it.
        Assert.Contains(s.Sink.Notices, n => n.StartsWith("stopped 'find-thing'"));
        var run = RunOf(s);
        Assert.True(run.StoppedByUser);

        // SO THE PARENT IS TOLD IT WAS STOPPED — not that it completed.
        Assert.Equal(BackgroundState.Stopped, run.State);
        Assert.Contains("state=\"cancelled\"", run.Handoff.Result!);
    }

    /// <summary>
    /// S20. THE USER'S /agents send TO A CHILD THEY STOPPED resumes it, the answer comes to the user and
    /// not to the model, and the model may resume it afterwards.
    ///
    /// <para>Guards AgentReachTools.SendAsync's <c>last.ClearUserStop()</c>: without it the user's own
    /// resume leaves the child refused to the model for good.</para>
    /// </summary>
    [Fact]
    public async Task UserResume_ShowsTheAnswerToTheUser()
    {
        var parent = new ScriptedProvider(Use(Calls.Spawn(id: "s1")), Answer("started"),
            Use(Calls.Named(Tool.AgentSend, new { to = "find-thing", prompt = "and more", background = false }, id: "a1")),
            Answer("done"));
        var child = new HeldProvider("resumed by user", "the model's answer");
        var s = Open(parent, child);

        await Turn(s.Session, "go");
        Assert.True(await Poll.Until(() => child.Calls == 1));
        s.Session.StopTask("stop find-thing");
        Assert.True(await Poll.Until(() => RunOf(s).Handoff.Ended));

        s.Session.SendToSpawnedAgent("send find-thing carry on please");
        child.Release();
        Assert.True(await Poll.Until(() => s.Sink.Notices.Any(n => n.Contains("resumed by user"))),
            "the user's resume was not answered to the user");
        Assert.True(await StaysAt(parent, 2), "the user's answer went to the model");

        await Turn(s.Session, "ask it again");
        Assert.Contains("the model's answer", parent.Last);
    }

    // ---- S23–S26: resumes, and the parent changing under a running child --------------------------------

    /// <summary>
    /// S23. agent_send background:false TO A FINISHED CHILD waits, and its answer is the tool result —
    /// no receipt, and no report later.
    ///
    /// <para>Guards AgentReachTools.SendAsync's <c>request.Background &amp;&amp;</c>: taking the
    /// background branch regardless returns a receipt where the answer belongs.</para>
    /// </summary>
    [Fact]
    public async Task ForegroundResume_AnswersInline()
    {
        var parent = new ScriptedProvider(Use(Calls.Spawn(id: "s1")), Answer("started"), Answer("got the first"),
            Use(Calls.Named(Tool.AgentSend, new { to = "find-thing", prompt = "and more", background = false }, id: "a1")),
            Answer("got the second"));
        var child = new HeldProvider("first answer", "second answer");
        child.Release();
        var s = Open(parent, child);

        await Turn(s.Session, "go");
        Assert.True(await Poll.Until(() => parent.Calls == 3));
        await Idle(s.Session);

        await Turn(s.Session, "ask it more");
        Assert.Contains("second answer", parent.Last);
        Assert.DoesNotContain("is working on it in the background", parent.Last);
        Assert.True(await StaysAt(parent, 5), "a foreground resume reported again later");
    }

    /// <summary>
    /// S24. /clear WHILE A CHILD RUNS: the report reaches the cleared conversation and stands on its own —
    /// what was asked, and why, without the context that asked it.
    ///
    /// <para>Guards BackgroundRunner.ReportFor's head: a report carrying only the envelope reaches a
    /// cleared context as an answer to a question nobody there asked.</para>
    /// </summary>
    [Fact]
    public async Task Clear_ThenReport_IsSelfContained()
    {
        var parent = new ScriptedProvider(Use(Calls.Spawn(id: "s1")), Answer("started"), Answer("read it"));
        var child = new HeldProvider("found it");
        var s = Open(parent, child);

        await Turn(s.Session, "please do the secret errand");
        Assert.Equal(CommandStatus.Changed, s.Session.ClearContext());

        child.Release();
        Assert.True(await Poll.Until(() => parent.Calls == 3));
        await Idle(s.Session);

        Assert.DoesNotContain("secret errand", parent.Last);
        Assert.Contains("You spawned it to: find thing", parent.Last);
        Assert.Contains("Its brief was: find the thing", parent.Last);
        Assert.Contains("found it", parent.Last);
    }

    /// <summary>
    /// S25. COMPACTION WHILE A CHILD RUNS: the report is read by the compacted conversation.
    ///
    /// <para>A REGRESSION GUARD over the same report S24 pins: the compacted context is rebuilt, and the
    /// report lands in it through the mailbox as ever. Red under S24's revert too.</para>
    /// </summary>
    [Fact]
    public async Task Compaction_ThenReport_Read()
    {
        var parent = new ScriptedProvider(Use(Calls.Spawn(id: "s1")), Answer("started"),
            Answer("summary: a child was spawned to find the thing"), Answer("read it"));
        var child = new HeldProvider("found it");
        var s = Open(parent, child);

        await Turn(s.Session, "go");
        var compacted = s.Session.CompressNow(CancellationToken.None);
        Assert.NotNull(compacted);
        await compacted;
        var afterCompaction = parent.Calls;

        child.Release();
        Assert.True(await Poll.Until(() => parent.Calls == afterCompaction + 1));
        await Idle(s.Session);
        Assert.Contains("found it", parent.Last);
        Assert.Contains("You spawned it to: find thing", parent.Last);
    }

    /// <summary>
    /// S26. A RE-WIRE WHILE A CHILD RUNS: the report, addressed to the agent that spawned it, reaches the
    /// agent the session has now.
    ///
    /// <para>A RE-WIRE, NOT /model: /model swaps the provider in place and keeps the agent id, so it cannot
    /// lose a report. A re-wire — a config change, a resume — mints a new agent, which is the case the
    /// spec's lifetime table names.</para>
    ///
    /// <para>Guards Session.IsSessionAgent's look through the ids the session has had: matching only the
    /// current id leaves the report addressed to nobody.</para>
    /// </summary>
    [Fact]
    public async Task Rebind_ThenReport_ReachesTheNewAgent()
    {
        var parent = new ScriptedProvider(Use(Calls.Spawn(id: "s1")), Answer("started"));
        var spare = new ScriptedProvider(Answer("the new agent read it"));
        var child = new HeldProvider("found it");
        var s = Open(parent, new SceneSetup { Child = child, Spare = spare });

        await Turn(s.Session, "go");
        var before = s.Session.SessionId;
        _manager!.Open(s.Session, s.Config.WithModel(s.Config.Catalog.Instances!.Use("spare")!),
            new SessionPorts { Observer = s.Sink, ToolObserver = new BufferedJobPanel() }, s.Session.Mode);
        Assert.NotEqual(before, s.Session.SessionId);

        child.Release();
        Assert.True(await Poll.Until(() => spare.Calls == 1), "the report never reached the new agent");
        await Idle(s.Session);
        Assert.Contains("found it", spare.Last);
        Assert.Equal(2, parent.Calls);
    }

    // ---- S31, S32: the cap -------------------------------------------------------------------------------

    /// <summary>
    /// S31. CAP TWO, FIVE SPAWNS: two run, three wait queued, and all five finish and report.
    ///
    /// <para>Guards BackgroundRunner.RunAsync's <c>await work.Slot.WaitAsync(run.Token)</c>: without it
    /// all five run at once.</para>
    /// </summary>
    [Fact]
    public async Task CapTwo_FiveSpawns_AllReport()
    {
        var spawns = Enumerable.Range(0, 5).Select(i => Calls.Spawn(id: $"s{i}", description: $"job {i}")).ToArray();
        var parent = new ScriptedProvider(Use(spawns), Answer("started five"));
        var child = new HeldProvider();
        var s = Open(parent, new SceneSetup { Child = child, Cap = 2 });

        await Turn(s.Session, "go");
        Assert.True(await Poll.Until(() => child.Calls == 2));
        await Task.Delay(100);
        var states = s.Session.SubAgents.Background().Select(r => r.State).ToList();
        Assert.Equal(2, states.Count(st => st == BackgroundState.Running));
        Assert.Equal(3, states.Count(st => st == BackgroundState.Queued));

        child.Release();
        Assert.True(await Poll.Until(() => Enumerable.Range(0, 5).All(i => parent.All.Contains($"background agent 'job-{i}'"))),
            "not every queued child reported");
        await Idle(s.Session);
        Assert.Equal(2, child.MaxInFlight);
    }

    /// <summary>
    /// S32. A FOREGROUND SPAWN WHILE BACKGROUND CHILDREN HOLD EVERY SLOT queues for one, then runs.
    ///
    /// <para>Guards SubAgentSpawner.TryInvokeAsync's foreground <c>await slot.WaitAsync(ct)</c>: one cap,
    /// shared by both (spec decision 6).</para>
    /// </summary>
    [Fact]
    public async Task ForegroundQueuesBehindBackground()
    {
        var parent = new ScriptedProvider(
            Use(Calls.Spawn(id: "s1")),
            Use(Calls.Named(Tool.Agent, new { description = "check it", prompt = "check the thing", type = "explore", background = false }, id: "s2")),
            Answer("both in"));
        var background = new HeldProvider("found it");
        var foreground = new HeldProvider("checked it");
        foreground.Release();
        var s = Open(parent, new SceneSetup { Child = background, Explore = foreground, Cap = 1 });

        s.Session.Submit("go");
        Assert.True(await Poll.Until(() => parent.Calls == 2 && background.Calls == 1));
        Assert.True(await StaysAt(parent, 2));
        Assert.Equal(0, foreground.Calls);

        background.Release();
        Assert.True(await Poll.Until(() => parent.Calls == 3));
        await Idle(s.Session);
        Assert.Contains("checked it", parent.Last);
        Assert.Equal(1, foreground.Calls);
    }

    // ---- S34–S37: how a child ends -----------------------------------------------------------------------

    /// <summary>
    /// S34. A CHILD AT ITS TURN CEILING reports capped, with the note saying its answer is unfinished
    /// work.
    ///
    /// <para>Guards SubAgentEnvelope.Render's <c>SendOutcome.Capped =&gt; "capped"</c>.</para>
    /// </summary>
    [Fact]
    public async Task CappedChild_ReportsCapped()
    {
        var parent = new ScriptedProvider(Use(Calls.Spawn(id: "s1")), Answer("started"), Answer("read it"));
        var child = new ScriptedProvider(Use(Calls.Named(Tool.Glob, new { pattern = "*.md" }, id: "g1")), Answer("got halfway"));
        var s = Open(parent, new SceneSetup { Child = child, ChildMaxTurns = 1 });

        await Turn(s.Session, "go");
        Assert.True(await Poll.Until(() => parent.Calls == 3));
        await Idle(s.Session);

        Assert.Contains("state=\"capped\"", parent.Last);
        Assert.Contains("hit its turn limit", parent.Last);
        Assert.Contains("got halfway", parent.Last);
    }

    /// <summary>
    /// S35. A CHILD THAT SAYS NOTHING reports no-answer, not completed.
    ///
    /// <para>Guards Agent.RunLoopAsync's <c>string.IsNullOrWhiteSpace(text)</c> → Silent.</para>
    /// </summary>
    [Fact]
    public async Task SilentChild_ReportsNoAnswer()
    {
        var parent = new ScriptedProvider(Use(Calls.Spawn(id: "s1")), Answer("started"), Answer("read it"));
        var s = Open(parent, new ScriptedProvider(Answer("")));

        await Turn(s.Session, "go");
        Assert.True(await Poll.Until(() => parent.Calls == 3));
        await Idle(s.Session);
        Assert.Contains("state=\"no-answer\"", parent.Last);
    }

    /// <summary>
    /// S36. A BACKGROUND PLANNER'S REPORT SAYS WHETHER ITS PLAN FILE IS THERE — the spawner checks the
    /// disk at the end of the run, as it does for a foreground one.
    ///
    /// <para>Guards SubAgentSpawner.TryInvokeAsync's background Render, <c>WithPlanOutcome(result.Text,
    /// planPath)</c>: the plain text there drops the line either way.</para>
    /// </summary>
    [Theory]
    [InlineData(true, "[cxagent] plan file: plans/find-thing.md")]
    [InlineData(false, "NO PLAN FILE was written at plans/find-thing.md")]
    public async Task BackgroundPlanner_ReportsThePlanFile(bool writes, string expected)
    {
        var parent = new ScriptedProvider(
            Use(Calls.Named(Tool.Agent, new { description = "find thing", prompt = "plan the thing", type = "planner" }, id: "s1")),
            Answer("started"), Answer("read it"));
        var child = writes
            ? new ScriptedProvider(Use(Calls.Named(Tool.WriteFile, new { path = "plans/find-thing.md", content = "# the plan" }, id: "w1")),
                Answer("planned"))
            : new ScriptedProvider(Answer("planned"));
        var s = Open(parent, child);

        await Turn(s.Session, "go");
        Assert.True(await Poll.Until(() => parent.Calls == 3));
        await Idle(s.Session);
        Assert.Contains(expected, parent.Last);
    }

    /// <summary>
    /// S37. A BACKGROUND CHILD'S PERMISSION REQUEST NAMES IT, and a denial costs it that one call: it
    /// carries on and reports.
    ///
    /// <para>THE PROMPT ITSELF IS THE FRONT END'S (PermissionPromptControl, spec decision 18) and is the
    /// live drive's to see; what Core owns is that the request carries the child's label and that a
    /// denial is a tool result, not the end of the run.</para>
    ///
    /// <para>Guards PermissionGatedExecutor's <c>Requester = context.Requester</c>: without it the request
    /// is the session's, and the child is not named.</para>
    /// </summary>
    [Fact]
    public async Task BackgroundChild_PermissionDenied_Continues()
    {
        var parent = new ScriptedProvider(Use(Calls.Spawn(id: "s1")), Answer("started"), Answer("read it"));
        var child = new ScriptedProvider(
            Use(Calls.Named(Tool.WriteFile, new { path = "x.txt", content = "x" }, id: "w1")),
            Answer("carried on without it"));
        var gate = new ChildGate(blocks: false);
        var s = Open(parent, new SceneSetup { Child = child, Gate = gate });

        await Turn(s.Session, "go");
        Assert.True(await Poll.Until(() => parent.Calls == 3));
        await Idle(s.Session);

        Assert.Equal("find thing", Assert.Single(gate.Requesters));
        Assert.Contains("permission denied by the user", child.Last);
        Assert.Contains("state=\"completed\"", parent.Last);
        Assert.Contains("carried on without it", parent.Last);
        Assert.False(File.Exists(Path.Combine(_dir, "x.txt")));
    }

    // ---- S39, S44, S45, S47 ------------------------------------------------------------------------------

    /// <summary>
    /// S39. A REPORT ARRIVING WHILE THE USER IS IN btw MODE goes to the main conversation, not the aside.
    ///
    /// <para>Guards Session.Submit's <c>InAside &amp;&amp; IsTheUser(source, origin)</c>: routing every
    /// submit to the aside there sends the report into a fork that never writes back.</para>
    /// </summary>
    [Fact]
    public async Task Report_DuringAside_GoesToTheMainConversation()
    {
        var parent = new ScriptedProvider(Use(Calls.Spawn(id: "s1")), Answer("started"),
            Answer("the aside's answer"), Answer("the main turn read it"));
        var child = new HeldProvider("found it");
        var s = Open(parent, child);

        await Turn(s.Session, "go");
        s.Session.Btw("what is going on?");
        Assert.True(s.Session.InAside);
        Assert.True(await Poll.Until(() => parent.Calls == 3 && !s.Session.AsideAnswering));

        child.Release();
        Assert.True(await Poll.Until(() => parent.Calls == 4), "the report started no main turn");
        await Idle(s.Session);
        Assert.Contains("found it", parent.Last);
        Assert.True(s.Session.InAside);

        // THE MAIN CONVERSATION KEPT IT. The request above would carry the report whichever conversation
        // took it — an aside's fork sends it too — so what tells them apart is what the main
        // conversation still holds once the aside is gone.
        s.Session.LeaveAside();
        await Turn(s.Session, "and now?");
        Assert.Contains("found it", parent.Last);
    }

    /// <summary>
    /// S44. A BACKGROUND CHILD'S TOKENS ARE THE SESSION'S, and its stretch is archived exactly once.
    ///
    /// <para>Guards BackgroundRunner.RunAsync's <c>store.EndSend(kept.Name, state)</c> for an announced
    /// run — the end a listener archives — and SessionFactory's <c>Ledger = ledger</c> for the spend.</para>
    /// </summary>
    [Fact]
    public async Task BackgroundSpend_OnTheLedger_ArchivedOnce()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var parent = new ScriptedProvider(Use(Calls.Spawn(id: "s1")), Answer("started"), Answer("read it"));
        var child = new ScriptedProvider(new LlmResponse
        {
            Text = "found it", StopReason = "end_turn", Usage = new LlmUsage { InputTokens = 1000, OutputTokens = 500 },
        });
        var s = Open(parent, child);

        await Turn(s.Session, "go");
        Assert.True(await Poll.Until(() => parent.Calls == 3));
        await Idle(s.Session);

        Assert.Equal(1000, s.Session.Ledger!.InputTokens);
        Assert.Equal(500, s.Session.Ledger.OutputTokens);
        var run = Assert.Single(_manager!.Shared.History!.RunsSince(since));
        Assert.Equal("completed", run.Outcome);
        Assert.Equal(1000, run.InputTokens);
    }

    /// <summary>
    /// S45. TWO BACKGROUND RESUMES OF ONE CHILD are two archived stretches with ids of their own, beside
    /// the spawn's.
    ///
    /// <para>Guards the same <c>store.EndSend</c> as S44, reached through a resume.</para>
    /// </summary>
    [Fact]
    public async Task TwoBackgroundResumes_TwoArchiveRows()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var resume = (string id) => Use(Calls.Named(Tool.AgentSend, new { to = "find-thing", prompt = "and more" }, id: id));
        var parent = new ScriptedProvider(
            Use(Calls.Spawn(id: "s1")), Answer("started"), Answer("read one"),
            resume("a1"), Answer("sent"), Answer("read two"),
            resume("a2"), Answer("sent again"), Answer("read three"));
        var child = new ScriptedProvider(Answer("one"), Answer("two"), Answer("three"));
        var s = Open(parent, child);

        await Turn(s.Session, "go");
        Assert.True(await Poll.Until(() => parent.Calls == 3));
        await Idle(s.Session);
        await Turn(s.Session, "again");
        Assert.True(await Poll.Until(() => parent.Calls == 6));
        await Idle(s.Session);
        await Turn(s.Session, "and again");
        Assert.True(await Poll.Until(() => parent.Calls == 9));
        await Idle(s.Session);

        Assert.Contains("You asked it: and more", parent.Last);
        var runs = _manager!.Shared.History!.RunsSince(since);
        Assert.Equal(3, runs.Count);
        Assert.Equal(3, runs.Select(r => r.RunId).Distinct().Count());
    }

    /// <summary>
    /// S47. A REPORT ARRIVING WHILE ANOTHER BACKGROUND CHILD'S PERMISSION REQUEST IS OPEN: the report
    /// wakes the parent, the request stays open, and once answered that child carries on and reports.
    ///
    /// <para>A REGRESSION GUARD with no single line of its own: the request waits on the child's task and
    /// the wake runs on the session's, and nothing joins them. What would break it is a delivery or a
    /// gate that serialised the two — S1 and S37 pin each half.</para>
    /// </summary>
    [Fact]
    public async Task ReportWhilePromptOpen_BothHandled()
    {
        var parent = new ScriptedProvider(
            Use(Calls.Spawn(id: "s1"),
                Calls.Named(Tool.Agent, new { description = "check it", prompt = "check the thing", type = "explore" }, id: "s2")),
            Answer("started both"));
        var asking = new ScriptedProvider(
            Use(Calls.Named(Tool.WriteFile, new { path = "x.txt", content = "x" }, id: "w1")), Answer("wrote it"));
        var quick = new HeldProvider("checked it");
        var gate = new ChildGate(blocks: true);
        var s = Open(parent, new SceneSetup { Child = asking, Explore = quick, Gate = gate });

        await Turn(s.Session, "go");
        Assert.True(await Poll.Until(() => gate.Requesters.Count == 1), "the child never asked");

        quick.Release();
        Assert.True(await Poll.Until(() => parent.All.Contains("checked it")), "the report waited on the prompt");
        await Idle(s.Session);
        Assert.Equal(BackgroundState.Running, RunOf(s).State);

        gate.Answer(PermissionOutcome.Allow);
        Assert.True(await Poll.Until(() => parent.All.Contains("wrote it")), "the asking child never reported");
        await Idle(s.Session);
        Assert.True(File.Exists(Path.Combine(_dir, "x.txt")));
        Assert.Equal(1, parent.MaxInFlight);
    }
}

/// <summary>
/// A gate for children's requests: records who asked, and denies — or, blocking, waits to be answered,
/// the way an open prompt does. The session's own requests are allowed without a record.
/// </summary>
internal sealed class ChildGate(bool blocks) : IPermissionGate
{
    private readonly TaskCompletionSource<PermissionOutcome> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _requesters = [];

    public IReadOnlyList<string> Requesters { get { lock (_requesters) return [.. _requesters]; } }

    public void Answer(PermissionOutcome outcome) => _answer.TrySetResult(outcome);

    public async Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct)
    {
        if (request.Requester is not { } who) return PermissionOutcome.Allow;
        lock (_requesters) _requesters.Add(who);
        return blocks ? await _answer.Task.WaitAsync(TimeSpan.FromSeconds(15), ct) : PermissionOutcome.ByUser;
    }
}

/// <summary>
/// Answers once, and in the instant before the answer returns, does something — a user's stop landing as
/// the child's model finishes.
/// </summary>
internal sealed class StoppingProvider(string answer, Action atTheAnswer) : ILlmProvider
{
    private int _calls;

    public string ProviderId => "stopping";
    public string ModelId => "stopping";
    public string DisplayName => "Stopping";
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => false;

    public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _calls) == 1) atTheAnswer();
        return Task.FromResult(new LlmResponse { Text = answer, StopReason = "end_turn" });
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var r = await ChatAsync(messages, tools, ct);
        yield return new LlmStreamChunk(r.Text, null, true);
    }
}

/// <summary>
/// A parent model that answers from a script, records every request, and counts the most in flight.
///
/// <para>NOT MockLlmProvider: that one throws on an empty queue, is not safe across the threads a wake
/// arrives on, and keeps only the last request — while these rows ask what the parent read across
/// SEVERAL requests, and whether two ever overlapped. A step may be held on a task, for the rows that
/// need a report to land while a particular request is in flight.</para>
/// </summary>
internal sealed class ScriptedProvider : ILlmProvider
{
    private readonly object _gate = new();
    private readonly Queue<(LlmResponse Response, Task? Hold)> _steps = new();
    private readonly List<string> _requests = [];
    private int _inFlight, _maxInFlight;

    public ScriptedProvider(params LlmResponse[] script)
    {
        foreach (var r in script) Then(r);
    }

    /// <summary>Adds a step; with a hold, its answer waits for that task.</summary>
    public ScriptedProvider Then(LlmResponse response, Task? hold = null)
    {
        lock (_gate) _steps.Enqueue((response, hold));
        return this;
    }

    public int Calls { get { lock (_gate) return _requests.Count; } }

    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    /// <summary>Each request's messages, joined — what the model was shown, in order.</summary>
    public IReadOnlyList<string> Requests { get { lock (_gate) return [.. _requests]; } }

    public string Last { get { lock (_gate) return _requests[^1]; } }

    /// <summary>Every request, joined — for "did the model ever see this".</summary>
    public string All { get { lock (_gate) return string.Join("\n", _requests); } }

    public string ProviderId => "scripted";
    public string ModelId => "scripted";
    public string DisplayName => "Scripted";
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => false;

    public async Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxInFlight))
               && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen) { }
        try
        {
            (LlmResponse Response, Task? Hold) step;
            lock (_gate)
            {
                _requests.Add(string.Join("\n", messages.Select(m => m.Content)));
                // RUN OFF THE END OF THE SCRIPT AND THE MODEL SAYS "ok": a wake the test did not
                // script is then visible as an extra request rather than as a throw on another thread.
                step = _steps.Count > 0 ? _steps.Dequeue() : (new LlmResponse { Text = "ok", StopReason = "end_turn" }, null);
            }

            // A DEADLINE, so a test that forgets to release fails instead of hanging the suite.
            if (step.Hold is { } hold) await hold.WaitAsync(TimeSpan.FromSeconds(15), ct);
            return step.Response;
        }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var r = await ChatAsync(messages, tools, ct);
        for (var i = 0; i < r.ToolCalls.Count - 1; i++) yield return new LlmStreamChunk(null, r.ToolCalls[i], IsFinal: false);
        yield return new LlmStreamChunk(r.Text, r.ToolCalls.LastOrDefault(), IsFinal: true, Usage: r.Usage, StopReason: r.StopReason);
    }
}

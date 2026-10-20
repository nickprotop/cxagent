using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// x on a finished agent forgets it everywhere: the store, the listings, the reach tools and its memory — after
/// the report the parent is owed has been handed over, and without telling the parent model anything.
/// </summary>
public sealed class AgentForgetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "forget-" + Guid.NewGuid().ToString("N"));
    private SessionManager? _manager;
    private BufferedChatSink? _parent;

    public AgentForgetTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { _manager?.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static SubAgent Child(params string[] answers)
    {
        var provider = new MockLlmProvider();
        foreach (var a in answers) provider.EnqueueResponse(new LlmResponse { Text = a, StopReason = "end_turn" });
        return new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = provider, Ledger = new TokenLedger(),
            MaxTurns = 5, CompressAbove = 40_000, ContextWindow = 200_000,
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
        }).Create();
    }

    private static void Finished(SubAgentStore store, string name)
    {
        store.TryClaim(name);
        store.AnnounceBegin(name);
        store.EndSend(name, "completed");
    }

    private static BackgroundRun EndedRun(SubAgentStore store, string name, Func<string, bool> leave, bool delivered)
    {
        var run = store.BeginBackground(new BackgroundStart(name, "session", "b", name, IsResume: false), new ResultHandoff(leave));
        run.MarkRunning();
        run.Finish(BackgroundState.Finished);
        run.Handoff.Complete("the report", _ => delivered);
        return run;
    }

    private Session Open()
    {
        _manager = SessionManager.Create(new AppPaths(_dir));
        return _manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = _parent = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.FanOut);
    }

    [Fact]
    public async Task AFinishedAgent_IsForgottenEverywhere()
    {
        var store = new SubAgentStore();
        var child = Child();
        var name = store.Keep(child, "find thing");
        Finished(store, name);
        string? forgotten = null;
        store.Forgotten += id => forgotten = id;

        Assert.Equal(ForgetOutcome.Forgotten, store.Forget(name));

        Assert.Null(store.Find(name));
        Assert.Null(store.FindByAgentId(child.Agent.Id));
        Assert.Empty(store.All());
        Assert.Null(store.LastEnd(name));
        Assert.True(store.WasForgotten(name));
        Assert.True(store.WasForgotten(child.Agent.Id));
        Assert.Equal(child.Agent.Id, forgotten);
        var tools = new TaskTools(new TaskSources(new DetachedProcessRegistry(), new FinishedShells(), "session", store));
        Assert.DoesNotContain(name, await tools.InvokeAsync("session", Calls.Named(Tool.TaskList, new { }), CancellationToken.None));
    }

    /// <summary>Decision 13: a running row's first x stops it; only an idle agent is forgotten.</summary>
    /// <summary>
    /// A DELETED AGENT'S KEPT SHELLS ARE STILL THE SESSION'S: the panel, the F7 count and the status bar all ask
    /// Owns, and shells the user chose to keep running must not drop off them with the agent.
    /// </summary>
    [Fact]
    public void ADeletedAgentsId_IsStillOwnedByItsSession()
    {
        var s = Open();
        var child = Child();
        s.SubAgents.Keep(child, "finder");
        Finished(s.SubAgents, "finder");

        Assert.Equal(ForgetOutcome.Forgotten, s.ForgetAgent("finder", keepShells: true));

        Assert.True(s.Owns(child.Agent.Id));
    }

    [Fact]
    public void AWorkingAgent_IsNotForgotten()
    {
        var store = new SubAgentStore();
        store.Keep(Child(), "busy");
        store.TryClaim("busy");

        Assert.Equal(ForgetOutcome.Working, store.Forget("busy"));
        Assert.NotNull(store.Find("busy"));
        store.Release("busy");
    }

    /// <summary>Decision 14: the owed report is left for the parent — once, as a note, never a turn — then the agent goes.</summary>
    [Fact]
    public void AnUndeliveredReport_IsLeftForTheParentOnce_ThenTheAgentIsForgotten()
    {
        var store = new SubAgentStore();
        store.Keep(Child(), "finder");
        var left = 0;
        var run = EndedRun(store, "finder", _ => { left++; return true; }, delivered: false);
        Assert.True(run.Handoff.Undelivered);

        Assert.Equal(ForgetOutcome.Forgotten, store.Forget("finder"));

        Assert.Equal(1, left);
        Assert.True(run.Handoff.Read().AlreadySent);
    }

    /// <summary>Q10: a report the parent still refuses keeps the agent, so nothing it owes is lost.</summary>
    [Fact]
    public void AReportTheParentStillRefuses_KeepsTheAgent()
    {
        var store = new SubAgentStore();
        store.Keep(Child(), "finder");
        var run = EndedRun(store, "finder", _ => false, delivered: false);

        Assert.Equal(ForgetOutcome.ReportOwed, store.Forget("finder"));

        Assert.NotNull(store.Find("finder"));
        Assert.True(run.Handoff.Undelivered);
        Assert.False(store.IsBusy("finder"));
    }

    /// <summary>Decision 15: the parent model is told nothing when an agent is forgotten.</summary>
    [Fact]
    public void TheParentIsToldNothing()
    {
        var store = new SubAgentStore();
        store.Keep(Child(), "finder");
        var told = 0;
        EndedRun(store, "finder", _ => { told++; return true; }, delivered: true);

        store.Forget("finder");

        Assert.Equal(0, told);
    }

    /// <summary>Decision 15: the model's agent_send on a forgotten agent says the user dismissed it.</summary>
    [Fact]
    public async Task AgentSend_OnAForgottenAgent_SaysTheUserDismissedIt()
    {
        var store = new SubAgentStore();
        store.Keep(Child(), "finder");
        Finished(store, "finder");
        store.Forget("finder");

        var answer = await new AgentReachTools(store).SendAsync(
            new SendRequest("finder", "more", Background: false, ParentAgentId: null, FromUser: false), CancellationToken.None);

        Assert.Contains("the user dismissed 'finder'", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskOutput_OnAForgottenAgent_SaysTheUserDismissedIt()
    {
        var store = new SubAgentStore();
        store.Keep(Child(), "finder");
        Finished(store, "finder");
        store.Forget("finder");
        var tools = new TaskTools(new TaskSources(new DetachedProcessRegistry(), new FinishedShells(), "session", store));

        var answer = await tools.InvokeAsync("session", Calls.Named(Tool.TaskOutput, new { id = "finder" }), CancellationToken.None);

        Assert.Contains("the user dismissed 'finder'", answer, StringComparison.Ordinal);
    }

    /// <summary>The user's own commands name the forget too, rather than "no task".</summary>
    [Fact]
    public void SlashAgentsSend_OnAForgottenAgent_SaysItWasDismissed()
    {
        var s = Open();
        s.SubAgents.Keep(Child(), "finder");
        Finished(s.SubAgents, "finder");
        Assert.Equal(ForgetOutcome.Forgotten, s.ForgetAgent("finder"));

        s.SendToSpawnedAgent("send finder more");
        s.StopTask("stop finder");

        Assert.Equal(2, _parent!.Notices.Count(n => n.Contains("'finder' was dismissed", StringComparison.Ordinal)));
    }

    /// <summary>Q9: a forgotten handle is never minted again, so a stale handle can never reach a new agent.</summary>
    [Fact]
    public void AForgottenHandle_IsNotMintedAgain()
    {
        var store = new SubAgentStore();
        var first = store.Keep(Child(), "find thing");
        Finished(store, first);
        store.Forget(first);

        Assert.Equal("find-thing-2", store.Keep(Child(), "find thing"));
    }

    /// <summary>
    /// REVIEW FOCUS 4: a forget and a send racing — a forgotten agent is never resumed. Overlapping, exactly one wins:
    /// forgotten, the send is answered "dismissed"; resumed, the forget is refused as working. In sequence, the send's
    /// stretch ends before the forget.
    /// </summary>
    /// <summary>A clock that runs something the next time it is read — a seam for putting another
    /// thread's work at an exact point inside the store.</summary>
    private sealed class InterruptingClock : TimeProvider
    {
        public Action? OnNextRead { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            var interrupt = OnNextRead;
            OnNextRead = null;
            interrupt?.Invoke();
            return base.GetUtcNow();
        }
    }

    /// <summary>
    /// A SEND'S END IS ANNOUNCED EVEN WHEN A FORGET IS WAITING FOR IT. The forget claims the agent the
    /// moment its claim is free; announced after that release, the end found the agent already gone
    /// and was never said — so the stretch was never archived and its row never settled. The forget
    /// here runs at the release itself, deterministically, where the race above has to be lucky.
    /// </summary>
    [Fact]
    public void ASendsEnd_IsAnnounced_BeforeAWaitingForgetCanClaimTheAgent()
    {
        var clock = new InterruptingClock();
        var store = new SubAgentStore(DetachedProcessRegistry.Default, FinishedShells.Default, clock);
        store.Keep(Child("ok"), "finder");
        var order = new List<string>();
        store.SendEnded += (_, _) => order.Add("send ended");
        store.Forgotten += _ => order.Add("forgotten");

        store.TryClaim("finder");
        store.AnnounceBegin("finder");
        clock.OnNextRead = () => store.Forget("finder");
        store.EndSend("finder");

        Assert.Equal("send ended", order.FirstOrDefault());
    }

    [Fact]
    public async Task AForget_AndASend_NeverBothWin()
    {
        for (var i = 0; i < 200; i++)
        {
            var store = new SubAgentStore();
            store.Keep(Child("ok"), "finder");
            Finished(store, "finder");
            var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
            store.SendEnded += (_, _) => order.Enqueue("send ended");
            store.Forgotten += _ => order.Enqueue("forgotten");
            using var go = new ManualResetEventSlim();

            var forget = Task.Run(() => { go.Wait(); return store.Forget("finder"); });
            var send = Task.Run(() => { go.Wait(); return new AgentReachTools(store).SendAsync(
                new SendRequest("finder", "more", Background: false, ParentAgentId: null, FromUser: true),
                CancellationToken.None); });
            go.Set();
            var (outcome, answer) = (await forget, await send);

            // BOTH CAN HAPPEN ONE AFTER THE OTHER: the send resumes the idle agent and finishes, and only then does
            // the forget claim it. What must never happen is a resume of an agent already forgotten — so a send that
            // answered normally must have ended before the forget.
            if (outcome == ForgetOutcome.Forgotten && !answer.Contains("dismissed", StringComparison.Ordinal))
                Assert.Equal(["send ended", "forgotten"], order.ToArray());
            else if (outcome == ForgetOutcome.Forgotten)
                Assert.Contains("dismissed", answer, StringComparison.Ordinal);
            else
            {
                Assert.Equal(ForgetOutcome.Working, outcome);
                Assert.NotNull(store.Find("finder"));
                Assert.DoesNotContain("dismissed", answer, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// A FORGET THAT FINISHES BETWEEN A SEND'S LOOKUP AND ITS CLAIM leaves the claim free, so the send would take it
    /// and resume an agent that is gone. The tombstone, written before the forget released its claim, stops it.
    /// </summary>
    [Fact]
    public async Task AForgetBetweenLookupAndClaim_IsCaught()
    {
        var store = new SubAgentStore();
        store.Keep(Child("should not run"), "finder");
        Finished(store, "finder");
        var reach = new AgentReachTools(store) { AfterLookupForTest = () => store.Forget("finder") };

        var answer = await reach.SendAsync(
            new SendRequest("finder", "more", Background: false, ParentAgentId: null, FromUser: false), CancellationToken.None);

        Assert.Contains("the user dismissed 'finder'", answer, StringComparison.Ordinal);
        Assert.False(store.IsBusy("finder"));
    }
}

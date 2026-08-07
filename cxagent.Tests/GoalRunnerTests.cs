using System.Collections.Concurrent;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using CxAgent.Core.Plugins;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class GoalRunnerTests
{
    // RecordingSink is now in TestProviders.cs (shared with OrchestratorLoopTests — one copy only).
    // Its Result/Error members are last-value views over the Errors/Messages collections the consult
    // loop's tests need, so every assertion below reads exactly as it did before the promotion.

    private sealed class RecordingJobPanel : IJobPanel
    {
        public readonly List<int> SetJobsCounts = new();
        public readonly List<string> Updates = new();   // "id:state"
        public readonly List<(string JobId, ResourceSnapshot Snapshot)> Resources = new();
        public readonly List<bool> DraftModeChanges = new();
        public bool IsDraftMode { get; private set; }
        public void SetJobs(IReadOnlyList<Job> jobs) => SetJobsCounts.Add(jobs.Count);
        public void UpdateJob(Job job) => Updates.Add($"{job.Id}:{job.State}");
        public void UpdateResources(string jobId, ResourceSnapshot snapshot) => Resources.Add((jobId, snapshot));

        /// <summary>Recorded so a test can assert a worker's text reached the panel AS IT STREAMED,
        /// rather than only arriving whole when the job finished.</summary>
        public readonly List<(string JobId, string Delta)> TextDeltas = new();
        public void AppendText(string jobId, string delta) => TextDeltas.Add((jobId, delta));
        public void SetDraftMode(bool isDraft) { IsDraftMode = isDraft; DraftModeChanges.Add(isDraft); }
    }

    private sealed class ThrowingJobPanel : IJobPanel
    {
        public void SetJobs(IReadOnlyList<Job> jobs) => throw new InvalidOperationException("boom");
        public void UpdateJob(Job job) { }
        public void UpdateResources(string jobId, ResourceSnapshot snapshot) { }
        public void AppendText(string jobId, string delta) { }
        public void SetDraftMode(bool isDraft) { }
    }

    // FakePlanProvider is now in TestProviders.cs (shared with AppShellE2ETests — one copy only).

    private static GoalRunner NewRunner(ILlmProvider provider) =>
        new(provider, new RecordingSink(), new RecordingJobPanel(), PluginRegistry.CreateWithBuiltins());

    private static GoalRunner NewRunner(ILlmProvider provider,
        Func<Job, JobDag, DagScheduler, CancellationToken, Task>? onJobFailed) =>
        new(provider, new RecordingSink(), new RecordingJobPanel(), PluginRegistry.CreateWithBuiltins(),
            onJobFailed: onJobFailed);

    [Fact]
    public async Task RunAsync_RecordsTokenUsage_IntoTheLedger()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(LlmResponse.WithToolCall("create_plan", new
        {
            jobs = new object[] { new { id = "a", name = "A", type = "wait", @params = new { seconds = 0 } } }
        }) with { Usage = new LlmUsage { InputTokens = 30, OutputTokens = 12 } });

        var runner = NewRunner(mock);
        await runner.RunAsync("goal", new List<ChatMessage>(), CancellationToken.None);

        Assert.Equal(42, runner.Ledger.TotalTokens);
    }

    /// <summary>
    /// AppBootstrap's status-bar cost readout has no per-Record event on TokenLedger to subscribe to
    /// (only Breached, which fires once) — so GoalRunner raises TokensUpdated itself at the same point
    /// it calls Ledger.Record, giving AppBootstrap a live hook without adding a public event to the
    /// ledger's own object model.
    /// </summary>
    [Fact]
    public async Task RunAsync_RaisesTokensUpdated_MatchingLedgerTotal()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(LlmResponse.WithToolCall("create_plan", new
        {
            jobs = new object[] { new { id = "a", name = "A", type = "wait", @params = new { seconds = 0 } } }
        }) with { Usage = new LlmUsage { InputTokens = 30, OutputTokens = 12 } });

        var runner = NewRunner(mock);
        var seen = new List<int>();
        runner.TokensUpdated += (_, total) => seen.Add(total);

        await runner.RunAsync("goal", new List<ChatMessage>(), CancellationToken.None);

        Assert.Contains(42, seen);
    }

    [Fact]
    public async Task RunAsync_ARejectedPlanIsRepairedNotFatal()
    {
        // A compile guard is correct to reject an unrunnable plan — here, a write depending on two
        // jobs with no content, which has no single output to write. But PlanCompiler THROWS, and
        // GoalRunner's catch turned that into a dead goal.
        //
        // MEASURED, which is why this test exists: a live fan-out trial that previously produced a
        // 19-byte placeholder file began producing NOTHING AT ALL the moment the guard started
        // firing. Rejecting-and-dying is worse than the bug the rejection prevents.
        //
        // ConsultJobCompiler has always RETURNED its error so the orchestrator re-plans against it —
        // that self-correction is the point of P8's loop. This gives the initial plan the same
        // chance, once.
        // Delete first: the assertion below is "this file was never written", which a leftover from
        // an earlier run satisfies falsely -- and did, hiding a real failure until the fixture changed.
        File.Delete("/tmp/cx-repair-test.md");

        var provider = new RejectThenRepairProvider();
        var sink = new RecordingSink();
        var jobs = new RecordingJobPanel();
        var runner = new GoalRunner(provider, sink, jobs, PluginRegistry.CreateWithBuiltins());

        var state = await runner.RunAsync("do two steps", new List<ChatMessage>(), CancellationToken.None);

        Assert.Equal(GoalState.Completed, state);
        Assert.Equal(2, provider.PlanTurns);   // it actually re-planned, rather than failing outright

        // And the goal ran the REPAIRED plan, not the rejected one.
        Assert.False(File.Exists("/tmp/cx-repair-test.md"));
    }

    [Fact]
    public async Task RunAsync_Streams_Compiles_Runs_FeedsJobPanel()
    {
        var sink = new RecordingSink();
        var jobs = new RecordingJobPanel();
        var runner = new GoalRunner(new FakePlanProvider(), sink, jobs, PluginRegistry.CreateWithBuiltins());
        var state = await runner.RunAsync("do two steps", new List<ChatMessage>(), CancellationToken.None);

        Assert.Equal(GoalState.Completed, state);
        Assert.Equal(GoalState.Completed, sink.Result);
        Assert.Contains("do two steps", sink.Users);
        // The PLANNING turn's streamed text, in order. StartsWith, not Equal (P8 Task 5): the goal now
        // runs through OrchestratorLoop, which appends the consult's rationale and summary to the same
        // sink afterwards. What this test pins is that the decomposition stream arrived intact and in
        // order — the consult's own output is OrchestratorLoopTests' subject, not this one's.
        Assert.StartsWith("Plan: two steps.", string.Concat(sink.AssistantTokens));
        Assert.Single(jobs.SetJobsCounts);           // SetJobs called exactly once
        Assert.Equal(2, jobs.SetJobsCounts[0]);       // with the full 2-job DAG
        // PlanCompiler maps plan-local ids (s1/s2) to ULIDs; assert by state suffix, not id.
        Assert.Equal(2, jobs.Updates.Count(u => u.EndsWith(":Succeeded")));
    }

    [Fact]
    public async Task RunAsync_WhenTheContextGrowsPastTheThreshold_ItCompressesBeforeTheNextGoal()
    {
        // The provider reports InputTokens on every response — that IS the live context size. When it
        // crosses the threshold the conversation must shrink, or the session grows until the provider
        // rejects it outright.
        var conversation = new List<ChatMessage>();
        for (int i = 0; i < 30; i++) conversation.Add(new ChatMessage
            { Role = "user", Content = $"old goal {i}", Timestamp = DateTimeOffset.UtcNow });

        var provider = new HighInputTokenProvider(inputTokens: 50_000);   // over the threshold
        var runner = new GoalRunner(provider, new RecordingSink(), new RecordingJobPanel(),
            PluginRegistry.CreateWithBuiltins(),
            orchestrator: new OrchestratorSettings(null, null, ContextCompressThreshold: 40_000));

        await runner.RunAsync("new goal", conversation, CancellationToken.None);

        Assert.True(conversation.Count < 30, "the conversation should have been compressed");
    }

    [Fact]
    public async Task RunAsync_DerivesTheThresholdFromTheProvidersContextWindow_WhenNoExplicitThresholdIsSet()
    {
        // No ContextCompressThreshold configured, but the active provider's window is known (100,000):
        // the effective trigger is 80% of that (80,000), not the 40,000 constant. 82,000 reported
        // tokens is over the derived trigger and under the constant, so this only compresses if
        // GoalRunner actually derives from the window rather than falling back to the fixed default.
        var conversation = new List<ChatMessage>();
        for (int i = 0; i < 30; i++) conversation.Add(new ChatMessage
            { Role = "user", Content = $"old goal {i}", Timestamp = DateTimeOffset.UtcNow });

        var provider = new HighInputTokenProvider(inputTokens: 82_000);
        var runner = new GoalRunner(provider, new RecordingSink(), new RecordingJobPanel(),
            PluginRegistry.CreateWithBuiltins(),
            orchestrator: new OrchestratorSettings(null, null),
            contextWindow: 100_000);

        await runner.RunAsync("new goal", conversation, CancellationToken.None);

        Assert.True(conversation.Count < 30, "the conversation should have been compressed");
    }

    [Fact]
    public async Task RunAsync_BelowTheThreshold_NothingIsDropped()
    {
        // Compression is lossy. It must not fire on a session that is comfortably within budget.
        var conversation = new List<ChatMessage>();
        for (int i = 0; i < 5; i++) conversation.Add(new ChatMessage
            { Role = "user", Content = $"goal {i}", Timestamp = DateTimeOffset.UtcNow });
        var before = conversation.Count;

        var provider = new HighInputTokenProvider(inputTokens: 1_000);
        var runner = new GoalRunner(provider, new RecordingSink(), new RecordingJobPanel(),
            PluginRegistry.CreateWithBuiltins(),
            orchestrator: new OrchestratorSettings(null, null, ContextCompressThreshold: 40_000));

        await runner.RunAsync("new goal", conversation, CancellationToken.None);

        Assert.True(conversation.Count >= before);
    }

    // ThrowingProvider promoted to TestProviders.cs (P11 Task 3) — reused by SessionCompressorTests.

    [Fact]
    public async Task RunAsync_ProviderThrows_ShowsError_DoesNotLeakVendorBody()
    {
        var sink = new RecordingSink();
        var runner = new GoalRunner(new ThrowingProvider(), sink, new RecordingJobPanel(), PluginRegistry.CreateWithBuiltins());
        var state = await runner.RunAsync("x", new List<ChatMessage>(), CancellationToken.None);

        Assert.NotEqual(GoalState.Completed, state);
        Assert.NotNull(sink.Error);
        Assert.Contains("auth failed", sink.Error!);
        Assert.DoesNotContain("secret-vendor-body", sink.Error!);  // VendorBody never surfaced
    }

    [Fact]
    public async Task RunAsync_TextWithoutAPlan_IsAnAnswer_NotAnError()
    {
        // WAS RunAsync_NoToolCall_ShowsInvalidPlanError, which pinned the opposite. Reported from real
        // use: "it ran, said hello, answered me, and below, a system message of error." TextOnlyProvider
        // streams "no plan here" — prose, no create_plan call. That is a conversational answer, and a
        // red "invalid plan" stamped under it told the user something had failed when nothing had.
        //
        // The genuinely broken case — no plan AND no text — is covered by
        // RunAsync_NeitherPlanNorAnswer_IsStillAnError below.
        var sink = new RecordingSink();
        var textOnly = new TextOnlyProvider();
        var runner = new GoalRunner(textOnly, sink, new RecordingJobPanel(), PluginRegistry.CreateWithBuiltins());
        var state = await runner.RunAsync("x", new List<ChatMessage>(), CancellationToken.None);
        Assert.Equal(GoalState.Completed, state);
        Assert.Empty(sink.Errors);
    }

    [Fact]
    public async Task RunAsync_NeitherPlanNorAnswer_IsStillAnError()
    {
        // The other half of the rule above: silence IS a failure. No plan and no text means the turn
        // produced nothing usable, and without a message the app just sits there looking idle.
        var sink = new RecordingSink();
        var runner = new GoalRunner(new SilentProvider(), sink,
            new RecordingJobPanel(), PluginRegistry.CreateWithBuiltins());

        var state = await runner.RunAsync("x", new List<ChatMessage>(), CancellationToken.None);

        Assert.NotEqual(GoalState.Completed, state);
        Assert.NotNull(sink.Error);
    }

    private sealed class SilentProvider : ILlmProvider
    {
        public string ProviderId => "silent";
        public string DisplayName => "Silent";
        public string ModelId => "test-model";
        public ILlmProvider WithModel(string model) => this;
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => true;
        public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct)
            => Task.FromResult(new LlmResponse());
        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
            List<ChatMessage> m, List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new LlmStreamChunk(null, null, true);
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_UnexpectedFaultAfterCompile_IsRoutedToShowError_NotUnobserved()
    {
        // A job panel that throws inside SetJobs simulates an unexpected fault AFTER the stream/compile
        // catches — the new outer try/catch must route it to ShowError, not let it escape as a faulted task.
        var sink = new RecordingSink();
        var runner = new GoalRunner(new FakePlanProvider(), sink, new ThrowingJobPanel(),
            PluginRegistry.CreateWithBuiltins());
        var state = await runner.RunAsync("x", new List<ChatMessage>(), CancellationToken.None);
        Assert.NotEqual(GoalState.Completed, state);
        Assert.NotNull(sink.Error);   // the fault surfaced as a visible error, not an unobserved throw
    }

    /// <summary>
    /// Spec §Diagnosis Request: automatic post-failure diagnosis is gated on RetryCount &lt; MaxRetries
    /// — a job that has already exhausted its retries must not trigger another automatic diagnosis
    /// round (manual F6 diagnosis is deliberately ungated; that path lives in AppBootstrap, not here).
    /// GoalRunner fires OnJobFailed only while headroom remains, and — the review's own test-quality
    /// finding on the ORIGINAL version of this test — must actually STOP firing once RetryCount
    /// reaches MaxRetries, not merely be observed once on a job that never got retried. The hook
    /// itself drives the retry (force: true — see GoalRunner.RetryJobAsync's own doc: force only
    /// bypasses DagScheduler's cap, never GoalRunner.ShouldAutoDiagnose's gate above it), so
    /// RetryCount genuinely climbs 0→1→2→3 (=MaxRetries) across real Failed transitions, and the 4th
    /// would-be round is the thing under test: it must NOT happen. Deleting
    /// "&amp;&amp; job.RetryCount &lt; job.MaxRetries" from GoalRunner (i.e. `ShouldAutoDiagnose`
    /// always returning true) would turn this into an infinite retry loop instead of settling at 3 —
    /// this test would hang/fail, not silently stay green.
    /// </summary>
    [Fact]
    public async Task RunAsync_FiresOnJobFailed_OnlyWhileRetryHeadroomRemains_AndStopsOnceExhausted()
    {
        var failing = new FailingPlanProvider();
        var seen = new List<(int RetryCount, int MaxRetries)>();

        var runner = new GoalRunner(failing, new RecordingSink(), new RecordingJobPanel(),
            PluginRegistry.CreateWithBuiltins(),
            onJobFailed: async (job, dag, scheduler, ct) =>
            {
                seen.Add((job.RetryCount, job.MaxRetries));
                // The hook itself is what drives RetryCount upward here — each forced retry re-fails
                // (the job's command is always invalid), producing the NEXT Failed transition GoalRunner
                // gates on ShouldAutoDiagnose before re-enqueuing. Retries through the CAPTURED
                // scheduler parameter (review round 2, N2), not runner.RetryJobAsync — this is now what
                // AppBootstrap's real automatic-diagnosis path does too.
                await scheduler.RetryAsync(job.Id, force: true);
            });

        await runner.RunAsync("goal", new List<ChatMessage>(), CancellationToken.None);

        // Default MaxRetries is 3 (Job.cs) and PlanCompiler never sets it from the wire format, so the
        // gate must stop the hook at exactly 3 rounds: RetryCount 0, 1, 2 all satisfy
        // RetryCount < MaxRetries(3); RetryCount 3 does not, so a 4th round must never be observed.
        Assert.Equal(3, seen.Count);
        Assert.Equal(new[] { (0, 3), (1, 3), (2, 3) }, seen);
    }

    /// <summary>
    /// Unit-level companion to the RunAsync-based test above: pins the extracted predicate directly,
    /// so a future refactor of the call site can't silently detach it from the gate it's supposed to
    /// implement.
    /// </summary>
    [Fact]
    public void ShouldAutoDiagnose_False_WhenRetryCountReachesMaxRetries()
    {
        var exhausted = new Job { Id = "j", GoalId = "g", PluginType = "shell", DisplayName = "j",
            State = JobState.Failed, RetryCount = 3, MaxRetries = 3 };
        var headroom = new Job { Id = "j2", GoalId = "g", PluginType = "shell", DisplayName = "j2",
            State = JobState.Failed, RetryCount = 0, MaxRetries = 3 };

        Assert.False(GoalRunner.ShouldAutoDiagnose(exhausted));
        Assert.True(GoalRunner.ShouldAutoDiagnose(headroom));
    }

    /// <summary>
    /// Task 11 review C2: the automatic hook must not fire (or must not act) while sibling jobs are
    /// still in flight — a retry issued from inside JobTransitioned while the goal's own StartAsync
    /// drive is still live races DagScheduler's "no overlapping drives" contract, and because the
    /// hook is launched fire-and-forget, the resulting InvalidOperationException was previously an
    /// unobserved faulted task (no chat error, no retry at all — see review C2). Two independent
    /// jobs: "fail" fails immediately, "slow" is still Running when that happens. The hook retries
    /// "fail" for real (force: true, so ShouldAutoDiagnose's own gate doesn't mask an overlap bug).
    /// "fail" always fails, so this also exercises the drain-queue fix (a retried job that fails
    /// again re-enqueues itself) — it retries until RetryCount reaches the default MaxRetries (3),
    /// i.e. exactly 3 automatic attempts (RetryCount 0, 1, 2), never a 4th.
    /// </summary>
    [Fact]
    public async Task RunAsync_OnJobFailedHook_RetryDuringSiblingInFlight_DoesNotThrow_AndRetriesUntilExhausted()
    {
        var provider = new TwoIndependentJobsPlanProvider();
        var sink = new RecordingSink();
        var jobPanel = new RecordingJobPanel();
        int attempts = 0;
        var retryResults = new List<bool>();

        var runner = new GoalRunner(provider, sink, jobPanel, PluginRegistry.CreateWithBuiltins(),
            onJobFailed: async (job, dag, scheduler, ct) =>
            {
                Interlocked.Increment(ref attempts);
                // force: true only bypasses DagScheduler's RetryCount>=MaxRetries guard; it does NOT
                // bypass ShouldAutoDiagnose's gate above (checked before enqueuing), so this still
                // proves the drive-overlap fix, not an infinite loop. Retries through the CAPTURED
                // scheduler parameter (review round 2, N2), matching AppBootstrap's real flow.
                retryResults.Add(await scheduler.RetryAsync(job.Id, force: true));
            });

        var state = await runner.RunAsync("goal", new List<ChatMessage>(), CancellationToken.None);

        // If the pre-fix race had fired (retry issued while "slow" was still in flight), RetryAsync
        // would have thrown InvalidOperationException from inside the fire-and-forget hook — an
        // unobserved faulted task, never reaching RunAsync's own try/catch, so sink.Error would stay
        // null EITHER WAY, and `attempts` (incremented BEFORE the retry) would still read 3 while
        // `retryResults` held fewer entries. Re-review N-note: the original version of this test never
        // asserted retryResults.Count, so `Assert.All` over a short list would have passed vacuously —
        // the count check below is what actually makes "every retry ran and returned true" load-bearing.
        Assert.Equal(3, attempts);
        Assert.Equal(3, retryResults.Count);
        Assert.All(retryResults, r => Assert.True(r, "every forced retry must have actually queued"));
        Assert.Null(sink.Error);
        Assert.NotEqual(GoalState.Active, state);
    }

    /// <summary>
    /// The neutral consult reply (P8 Task 5). GoalRunner now drives through OrchestratorLoop, so every
    /// plan fixture below is asked what to do once its jobs finish and ChatAsync can no longer throw.
    /// <c>finish_goal</c> ends the loop without adding, cancelling, or re-parameterising anything —
    /// so each fixture still runs exactly the plan it compiled, and its existing assertions still hold.
    /// </summary>
    private static Task<LlmResponse> FinishGoalConsult() =>
        Task.FromResult(LlmResponse.WithToolCall("consult",
            new { action = "finish_goal", summary = "done", rationale = "plan ran as written" }));

    private sealed class TwoIndependentJobsPlanProvider : ILlmProvider
    {
        public string ProviderId => "two-jobs"; public string DisplayName => "TwoJobs";
        public string ModelId => "test-model";
        public ILlmProvider WithModel(string model) => this;
        public bool SupportsToolCalling => true; public bool SupportsStreaming => true;
        public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct)
            => FinishGoalConsult();
        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
            List<ChatMessage> m, List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            // "fail" fails immediately; "slow" takes a little longer so it's still Running when
            // "fail" transitions to Failed — reproducing the race the review flagged.
            var args = System.Text.Json.JsonDocument.Parse("""
            {"summary":"two","jobs":[
              {"id":"fail","name":"Fail","type":"shell","params":{"command":"exit 7"}},
              {"id":"slow","name":"Slow","type":"wait","params":{"seconds":0.1}}]}
            """).RootElement.Clone();
            yield return new LlmStreamChunk(null, new ToolCall { Name = "create_plan", Id = "c1", Arguments = args }, false);
            yield return new LlmStreamChunk(null, null, true);
            await Task.CompletedTask;
        }
    }

    private sealed class FailingPlanProvider : ILlmProvider
    {
        public string ProviderId => "failing"; public string DisplayName => "Failing";
        public string ModelId => "test-model";
        public ILlmProvider WithModel(string model) => this;
        public bool SupportsToolCalling => true; public bool SupportsStreaming => true;
        public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct)
            => FinishGoalConsult();
        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
            List<ChatMessage> m, List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var args = System.Text.Json.JsonDocument.Parse("""
            {"summary":"one","jobs":[
              {"id":"s1","name":"Step 1","type":"shell","params":{"command":"exit 7"}}]}
            """).RootElement.Clone();
            yield return new LlmStreamChunk(null, new ToolCall { Name = "create_plan", Id = "c1", Arguments = args }, false);
            yield return new LlmStreamChunk(null, null, true);
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Re-review round 2, N2 regression: a recovery pending across a new goal submission must retry
    /// through the SCHEDULER IT CAPTURED, not by re-reading GoalRunner state after the fact. Simulates
    /// the exact scenario the re-review described — goal 1 finishes with a job Failed (quiescent, so
    /// WaitForQuiescenceAsync would wave a dispose straight through); a caller captures the
    /// (dag, scheduler) session via TryGetSession while a "dialog" is conceptually open; THEN goal 2
    /// runs (its own outcome doesn't matter — only that it swaps in a NEW dag/scheduler pair), which
    /// disposes goal 1's scheduler; only THEN does the caller act, driving the retry through the
    /// scheduler it captured before goal 2 ever started.
    /// Pre-fix (re-reading runner.CurrentDag + runner.RetryJobAsync at "dialog answer" time instead of
    /// capturing up front) this would retry through goal 2's scheduler — TryGet(j.Id) returns null,
    /// so the retry silently reports "could not retry", or throws ObjectDisposedException if the
    /// field read raced the dispose. This test proves the captured-session path avoids both.
    /// </summary>
    [Fact]
    public async Task CapturedSession_SurvivesASecondGoalStarting_AndRetryStillReachesGoal1sScheduler()
    {
        var jobPanel = new RecordingJobPanel();
        var runner = new GoalRunner(new FailingPlanProvider(), new RecordingSink(), jobPanel,
            PluginRegistry.CreateWithBuiltins());   // no onJobFailed — goal 1 just fails and stops

        // Goal 1: one job, always fails, no automatic diagnosis configured — ends quiescent and Failed.
        var state1 = await runner.RunAsync("goal one", new List<ChatMessage>(), CancellationToken.None);
        Assert.Equal(GoalState.Failed, state1);

        // Simulate "F6 pressed, dialog about to open": capture the session NOW, matching
        // AppBootstrap's DiagnoseJobAsync capturing (dag, scheduler) before its own await on
        // RecoveryFlow.RunAsync.
        Assert.True(runner.TryGetSession(out var capturedDag, out var capturedScheduler));
        var failedJob = capturedDag!.AllJobs.Single(j => j.State == JobState.Failed);

        // Simulate the "dialog is open" gap: a second goal starts and runs to completion in the
        // meantime — a second call through the SAME runner+provider is enough to exercise the swap;
        // its own outcome (also Failed, since FailingPlanProvider ignores the goal text and always
        // compiles the same always-failing job) isn't the point. This disposes goal 1's scheduler
        // (WaitForQuiescenceAsync sees goal 1 as already quiescent, so nothing blocks the dispose) and
        // swaps runner's CurrentDag/_currentScheduler to goal 2's — exactly the interleaving the
        // re-review described.
        await runner.RunAsync("goal two", new List<ChatMessage>(), CancellationToken.None);
        Assert.NotSame(capturedDag, runner.CurrentDag);   // goal 2's dag really did replace goal 1's

        // "Dialog answered": retry through the CAPTURED scheduler, not runner.RetryJobAsync (which by
        // now would act on goal 2's scheduler/dag instead).
        bool queued = await capturedScheduler!.RetryAsync(failedJob.Id, force: true);

        Assert.True(queued, "the captured scheduler must still be usable after a second goal started and finished");
        Assert.Equal(JobState.Failed, failedJob.State);   // FailingPlanProvider's job always fails again
        Assert.Equal(1, failedJob.RetryCount);   // but the retry genuinely ran (RetryCount incremented)
    }

    // ------------------------------------------------------------------ P8 Task 5: the loop is wired

    /// <summary>
    /// Streams a one-job create_plan (the planning turn, ChatStreamAsync) and then answers every
    /// consult (ChatAsync) with <c>finish_goal</c>. Two different transports on one provider because
    /// that is exactly how GoalRunner uses it: the plan is streamed into the chat sink, the consults
    /// are single complete tool calls.
    /// </summary>
    private class PlanThenConsultProvider : ILlmProvider
    {
        private int _consults;

        /// <summary>How many times the orchestrator was consulted. Interlocked: a consult can be
        /// issued from a continuation on a thread-pool thread when no sync context is installed.</summary>
        public int ConsultCount => Volatile.Read(ref _consults);

        /// <summary>Runs on each consult — the ordering probe for the diagnosis-before-consult test.</summary>
        protected Action? OnConsult { get; init; }

        /// <summary>The plan this provider compiles. Overridden to plan a FAILING job.</summary>
        protected virtual string PlanJson => """
        {"summary":"one","jobs":[
          {"id":"s1","name":"Step 1","type":"wait","params":{"seconds":0.0}}]}
        """;

        public string ProviderId => "plan-consult"; public string DisplayName => "PlanConsult";
        public string ModelId => "test-model";
        public ILlmProvider WithModel(string model) => this;
        public bool SupportsToolCalling => true; public bool SupportsStreaming => true;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct)
        {
            Interlocked.Increment(ref _consults);
            OnConsult?.Invoke();
            return Task.FromResult(LlmResponse.WithToolCall("consult",
                new { action = "finish_goal", summary = "done", rationale = "nothing to change" }));
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
            List<ChatMessage> m, List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var args = System.Text.Json.JsonDocument.Parse(PlanJson).RootElement.Clone();
            yield return new LlmStreamChunk(null, new ToolCall { Name = "create_plan", Id = "c1", Arguments = args }, false);
            yield return new LlmStreamChunk(null, null, true);
            await Task.CompletedTask;
        }
    }

    /// <summary>Same, but the planned job always fails — so automatic diagnosis has something to fire on.</summary>
    private sealed class FailingPlanThenConsultProvider : PlanThenConsultProvider
    {
        public FailingPlanThenConsultProvider(Action? onConsult = null) => OnConsult = onConsult;

        protected override string PlanJson => """
        {"summary":"one","jobs":[
          {"id":"s1","name":"Step 1","type":"shell","params":{"command":"exit 7"}}]}
        """;
    }

    [Fact]
    public async Task RunAsync_ConsultsTheOrchestratorAfterJobsFinish()
    {
        // End-to-end through the real GoalRunner: plan compiles, jobs run, a consult happens.
        var provider = new PlanThenConsultProvider();
        var runner = NewRunner(provider);

        await runner.RunAsync("do a thing", new List<ChatMessage>(), CancellationToken.None);

        Assert.True(provider.ConsultCount >= 1);
    }

    /// <summary>
    /// The pre-flight caught that <c>Assert.True(diagnosed >= 1)</c> — the obvious test — passes under
    /// BOTH the safe arrangement and the broken one, so it proves nothing. Assert the ORDER instead,
    /// which is what the ruling actually requires: diagnosis runs at quiescence BEFORE the consult, so
    /// the orchestrator is shown state the diagnoser has already repaired and cannot double-repair it.
    /// </summary>
    [Fact]
    public async Task RunAsync_DiagnosisRunsBEFORETheConsult_AndNeverDrivesConcurrently()
    {
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var provider = new FailingPlanThenConsultProvider(onConsult: () => order.Enqueue("consult"));
        var runner = NewRunner(provider, onJobFailed: (_, _, _, _) =>
        {
            order.Enqueue("diagnose");
            return Task.CompletedTask;
        });

        await runner.RunAsync("do a thing", new List<ChatMessage>(), CancellationToken.None);

        var seen = order.ToList();
        Assert.Contains("diagnose", seen);
        Assert.Contains("consult", seen);
        Assert.True(seen.IndexOf("diagnose") < seen.IndexOf("consult"),
            "diagnosis must run at quiescence BEFORE the consult, so the orchestrator sees repaired state");
    }

    /// <summary>
    /// DriveAsync throws InvalidOperationException on overlapping drives. In the loop's context that
    /// throw is OBSERVED (OrchestratorLoop.RunAsync's catch reports it and returns Failed) and kills
    /// the goal, unlike P6's fire-and-forget hook where it was merely swallowed. This is the
    /// regression test for the ruling: a diagnoser doing its REAL thing — driving the scheduler —
    /// must not throw, and must not surface an error.
    /// </summary>
    [Fact]
    public async Task RunAsync_DiagnoserDrivingConcurrentlyWithTheLoop_DoesNotThrow()
    {
        var provider = new FailingPlanThenConsultProvider();
        var sink = new RecordingSink();
        var runner = new GoalRunner(provider, sink, new RecordingJobPanel(),
            PluginRegistry.CreateWithBuiltins(),
            onJobFailed: async (job, dag, scheduler, ct) =>
            {
                await scheduler.RetryAsync(job.Id, force: true);   // the diagnoser's real behaviour
            });

        var ex = await Record.ExceptionAsync(() =>
            runner.RunAsync("do a thing", new List<ChatMessage>(), CancellationToken.None));

        Assert.Null(ex);
        // An overlapping drive would surface as a reported InvalidOperationException, not a throw —
        // OrchestratorLoop.RunAsync catches it. Assert on the error channel too, or the fix could
        // regress into "no throw, but the goal died with a drive-overlap error in the chat".
        Assert.DoesNotContain(sink.Errors, e => e.Contains("drive operations must not overlap"));
    }

    // ------------------------------------------------------------------ P9 Task 1: copilot approval gate

    private static GoalRunner NewCopilotRunner(ILlmProvider provider, IChatSink sink, IJobPanel jobPanel) =>
        new(provider, sink, jobPanel, PluginRegistry.CreateWithBuiltins(),
            orchestrator: new OrchestratorSettings(null, null, Copilot: true));

    [Fact]
    public async Task RunAsync_CompressesEvenWhenTheGoalNeverBuiltADag()
    {
        // MEASURED on a live drive: a session at 16,241 tokens and 10 messages, against a threshold
        // of 100, never auto-compressed. Both gates were wide open — the call was simply unreachable.
        //
        // RunCoreAsync has NINE early returns and the compression call sat after only one of them. A
        // conversational turn ("what is 2+2?") returns Completed before a DAG is ever built, so the
        // goals that grow the conversation most cheaply were exactly the ones that never triggered
        // the bound meant to contain them.
        var conversation = new List<ChatMessage>();
        for (int i = 0; i < 20; i++) conversation.Add(new ChatMessage
            { Role = "user", Content = $"old goal {i}", Timestamp = DateTimeOffset.UtcNow });
        var before = conversation.Count;

        // Answers conversationally — no create_plan, so RunCoreAsync returns at the early exit.
        // MUST report usage: _lastInputTokens is only set when InputTokens > 0 (a reported 0 is
        // deliberately never treated as a measurement), so a fake without Usage leaves it null and
        // compression correctly declines — which would make this test pass for the wrong reason.
        var runner = new GoalRunner(new AnswersWithUsageProvider(inputTokens: 5_000), new RecordingSink(),
            new RecordingJobPanel(), PluginRegistry.CreateWithBuiltins(),
            orchestrator: new OrchestratorSettings(null, null, ContextCompressThreshold: 1));

        await runner.RunAsync("hello", conversation, CancellationToken.None);

        Assert.True(conversation.Count < before,
            $"the conversation was never compressed on the no-dag path ({conversation.Count} messages)");
    }

    [Fact]
    public async Task RunAsync_AConversationalAnswerIsNotAnError()
    {
        // Reported from real use: "it ran, said hello, answered me, and below, a system message of
        // error." Any turn without a create_plan call was treated as a failed goal — so greeting the
        // agent, or asking it a question, stamped a red "invalid plan" under a perfectly good answer.
        var sink = new RecordingSink();
        var runner = new GoalRunner(new AnswersWithoutPlanningProvider(), sink,
            new RecordingJobPanel(), PluginRegistry.CreateWithBuiltins());

        var state = await runner.RunAsync("hello", new List<ChatMessage>(), CancellationToken.None);

        Assert.Equal(GoalState.Completed, state);
        Assert.Empty(sink.Errors);
    }

    [Fact]
    public async Task RunAsync_ASilentPlanningTurnSaysWhatItPlanned()
    {
        // A turn that produced only a tool call used to render as an empty bubble — a gap between the
        // user's goal and a sudden list of jobs, where the reasoning should be. The model DID do
        // something (it designed a plan), so say so.
        //
        // FakePlanProvider returns a 2-job plan whose summary is "two", and streams prose, so this
        // uses a provider that returns the tool call ALONE.
        var sink = new RecordingSink();
        var runner = new GoalRunner(new SilentPlanProvider(), sink,
            new RecordingJobPanel(), PluginRegistry.CreateWithBuiltins());

        await runner.RunAsync("do it", new List<ChatMessage>(), CancellationToken.None);

        var text = string.Concat(sink.AssistantTokens);
        Assert.Contains("Read and summarise", text);   // the plan's OWN summary, not a generic line
        Assert.Contains("2 jobs", text);               // ...and what it is about to do
    }

    private sealed class SilentPlanProvider : ILlmProvider
    {
        public string ProviderId => "silent-plan";
        public string DisplayName => "Silent plan";
        public string ModelId => "test-model";
        public ILlmProvider WithModel(string model) => this;
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => true;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct)
            => Task.FromResult(LlmResponse.WithToolCall("consult",
                new { action = "finish_goal", summary = "done", rationale = "ran as written" }));

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
            List<ChatMessage> m, List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            // NO text chunks at all — only the tool call. This is what a real model does most of the time.
            var args = System.Text.Json.JsonDocument.Parse("""
            {"summary":"Read and summarise the file","jobs":[
              {"id":"s1","name":"Step 1","type":"wait","params":{"seconds":0.0}},
              {"id":"s2","name":"Step 2","type":"wait","params":{"seconds":0.0},"depends_on":["s1"]}]}
            """).RootElement.Clone();
            yield return new LlmStreamChunk(null, new ToolCall { Name = "create_plan", Id = "c1", Arguments = args }, false);
            yield return new LlmStreamChunk(null, null, true);
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_ClosesTheAssistantTurnEvenWhenTheModelReturnsNoProse()
    {
        // The bouncing spinner. A turn is created with thinking:true and the control only clears that
        // when a message receives BODY CONTENT (ChatTranscriptControl.cs:579). A planning turn that
        // returns a create_plan call and no text — the NORMAL case — therefore span forever, reading
        // as "still working" long after the goal had finished.
        var sink = new RecordingSink();
        var runner = new GoalRunner(new FakePlanProvider(), sink,
            new RecordingJobPanel(), PluginRegistry.CreateWithBuiltins());

        await runner.RunAsync("do two steps", new List<ChatMessage>(), CancellationToken.None);

        Assert.NotEmpty(sink.EndedTurns);
    }

    [Fact]
    public async Task RunAsync_CopilotGatesJobsTheORCHESTRATORAddsMidGoal()
    {
        // P9b. P9 gated only the INITIAL plan, so a goal approved at the start could still grow jobs
        // the user never saw. They ARE validated — both compilers' guards run on them — but "valid"
        // was never copilot's promise. The promise is that nothing runs unseen.
        var provider = new AddsAJobMidGoalProvider();
        var sink = new RecordingSink();
        var jobs = new RecordingJobPanel();
        var runner = NewCopilotRunner(provider, sink, jobs);

        var runTask = runner.RunAsync("one step", new List<ChatMessage>(), CancellationToken.None);

        await WaitForAsync(() => sink.ApprovalRequests > 0);
        runner.ApproveDraft();                       // approve the INITIAL plan

        // Then the orchestrator asks to add a job, and that must gate too.
        await WaitForAsync(() => sink.ApprovalRequests > 1);

        // The request NAMES what it is asking about — "1 job(s)" alone is not reviewable.
        Assert.Contains("Extra Step", sink.ApprovalDetails[1]);

        runner.ApproveDraft();
        var state = await runTask;

        Assert.Equal(GoalState.Completed, state);

        // TWO distinct jobs reached Succeeded: the planned one and the approved addition. Asserted by
        // COUNT rather than by id — panel updates carry ULIDs, not the plan-local "extra".
        var succeeded = jobs.Updates.Where(u => u.EndsWith(":Succeeded")).Select(u => u.Split(':')[0]).Distinct();
        Assert.Equal(2, succeeded.Count());
    }

    [Fact]
    public async Task RunAsync_DecliningAMidGoalAdditionLeavesThePlanAsItStands()
    {
        // The half that proves the gate BLOCKS rather than merely pausing. A refusal must leave the
        // dag untouched — the check runs after compiling but BEFORE DagModifier.TryApply, so there
        // is nothing to unwind — and must end the goal cleanly, not as an error: the user looked and
        // said no.
        var provider = new AddsAJobMidGoalProvider();
        var sink = new RecordingSink();
        var jobs = new RecordingJobPanel();
        var runner = NewCopilotRunner(provider, sink, jobs);

        var runTask = runner.RunAsync("one step", new List<ChatMessage>(), CancellationToken.None);

        await WaitForAsync(() => sink.ApprovalRequests > 0);
        runner.ApproveDraft();                       // the initial plan is fine

        await WaitForAsync(() => sink.ApprovalRequests > 1);
        runner.DiscardDraft();                       // ...but not the addition

        await runTask;

        // The declined job NEVER ran. This is the assertion that matters: gating that still executes
        // the work is not gating.
        //
        // Counted, not matched by id: panel updates carry ULIDs, so `StartsWith("extra:")` would
        // never match and the test would pass without proving anything. ONE job succeeded — the
        // planned one — and the addition did not.
        var succeeded = jobs.Updates.Where(u => u.EndsWith(":Succeeded")).Select(u => u.Split(':')[0]).Distinct();
        Assert.Single(succeeded);
    }

    [Fact]
    public async Task RunAsync_WithCopilotOff_ExecutesWithoutWaiting()
    {
        // The default path must be untouched. Every existing GoalRunner test asserts this implicitly;
        // this one asserts it on purpose, so copilot cannot regress the normal flow.
        var sink = new RecordingSink();
        var jobs = new RecordingJobPanel();
        var runner = new GoalRunner(new FakePlanProvider(), sink, jobs, PluginRegistry.CreateWithBuiltins());

        var state = await runner.RunAsync("do two steps", new List<ChatMessage>(), CancellationToken.None);

        Assert.Equal(GoalState.Completed, state);
        Assert.Equal(0, sink.ApprovalRequests);
        Assert.Equal(2, jobs.Updates.Count(u => u.EndsWith(":Succeeded")));
    }

    [Fact]
    public async Task RunAsync_WithCopilotOn_DoesNotRunAnyJobUntilApproved()
    {
        // THE test. Plan, show, and stop. Assert that the plugin/executor saw ZERO jobs while the goal
        // sits in Draft — not merely that the state is Draft, which would pass even if a job had run.
        var sink = new RecordingSink();
        var jobs = new RecordingJobPanel();
        var runner = NewCopilotRunner(new FakePlanProvider(), sink, jobs);

        var runTask = runner.RunAsync("do two steps", new List<ChatMessage>(), CancellationToken.None);

        // Wait for the draft to actually be shown before asserting nothing ran — no arbitrary sleep.
        await WaitForAsync(() => sink.ApprovalRequests > 0);

        Assert.Single(jobs.SetJobsCounts);            // the plan WAS shown...
        Assert.Equal(2, jobs.SetJobsCounts[0]);
        Assert.Empty(jobs.Updates);                    // ...but no job transitioned — nothing executed
        Assert.True(jobs.IsDraftMode);                 // Task 2: unmistakable — panel knows it's a draft

        runner.DiscardDraft();
        await runTask;

        Assert.False(jobs.IsDraftMode);                // cleared the instant the gate resolved
    }

    /// <summary>
    /// Task 2's UI seam: MainWindow subscribes to DraftPending to show/hide the F9/Esc footer hint.
    /// Must fire true exactly when the draft is shown and false exactly when the gate resolves —
    /// mirrors TokensUpdated's "GoalRunner raises it itself at the point of truth" pattern.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithCopilotOn_RaisesDraftPending_TrueThenFalse()
    {
        var sink = new RecordingSink();
        var jobs = new RecordingJobPanel();
        var runner = NewCopilotRunner(new FakePlanProvider(), sink, jobs);
        var seen = new List<bool>();
        runner.DraftPending += (_, pending) => seen.Add(pending);

        var runTask = runner.RunAsync("do two steps", new List<ChatMessage>(), CancellationToken.None);
        await WaitForAsync(() => sink.ApprovalRequests > 0);

        runner.DiscardDraft();
        await runTask;

        Assert.Equal(new[] { true, false }, seen);
    }

    [Fact]
    public async Task RunAsync_WithCopilotOff_NeverRaisesDraftPending()
    {
        var sink = new RecordingSink();
        var jobs = new RecordingJobPanel();
        var runner = new GoalRunner(new FakePlanProvider(), sink, jobs, PluginRegistry.CreateWithBuiltins());
        var seen = new List<bool>();
        runner.DraftPending += (_, pending) => seen.Add(pending);

        await runner.RunAsync("do two steps", new List<ChatMessage>(), CancellationToken.None);

        Assert.Empty(seen);
        Assert.Empty(jobs.DraftModeChanges);
    }

    [Fact]
    public async Task RunAsync_WithCopilotOn_ApprovalRunsThePlanUnchanged()
    {
        // After approval the goal executes exactly the DAG that was shown. Assert the job ids/count
        // match what SetJobs received — a plan that is silently re-planned after approval defeats the
        // entire feature.
        var sink = new RecordingSink();
        var jobs = new RecordingJobPanel();
        var provider = new FakePlanProvider();
        var runner = NewCopilotRunner(provider, sink, jobs);

        var runTask = runner.RunAsync("do two steps", new List<ChatMessage>(), CancellationToken.None);
        await WaitForAsync(() => sink.ApprovalRequests > 0);

        Assert.True(runner.TryGetSession(out var dag, out _));
        var shownJobIds = dag!.AllJobs.Select(j => j.Id).OrderBy(x => x).ToList();

        runner.ApproveDraft();
        var state = await runTask;

        Assert.Equal(GoalState.Completed, state);
        Assert.Single(jobs.SetJobsCounts);              // SetJobs called exactly once — no re-plan
        var ranJobIds = jobs.Updates.Where(u => u.EndsWith(":Succeeded"))
            .Select(u => u.Split(':')[0]).OrderBy(x => x).ToList();
        Assert.Equal(shownJobIds, ranJobIds);            // exactly the DAG that was shown, unchanged
    }

    [Fact]
    public async Task RunAsync_WithCopilotOn_DiscardLeavesNothingRunning()
    {
        // Discard must end the goal, not leave a scheduler armed. Assert the final state and that no
        // job ran.
        var sink = new RecordingSink();
        var jobs = new RecordingJobPanel();
        var runner = NewCopilotRunner(new FakePlanProvider(), sink, jobs);

        var runTask = runner.RunAsync("do two steps", new List<ChatMessage>(), CancellationToken.None);
        await WaitForAsync(() => sink.ApprovalRequests > 0);

        runner.DiscardDraft();
        var state = await runTask;

        Assert.Equal(GoalState.Cancelled, state);
        Assert.Empty(jobs.Updates);
    }

    [Fact]
    public async Task RunAsync_WithCopilotOn_CancellationAbandonsAWaitingApproval()
    {
        // Ctrl+C during a draft must exit, not hang forever waiting on the UI.
        var sink = new RecordingSink();
        var jobs = new RecordingJobPanel();
        var runner = NewCopilotRunner(new FakePlanProvider(), sink, jobs);
        using var cts = new CancellationTokenSource();

        var runTask = runner.RunAsync("do two steps", new List<ChatMessage>(), cts.Token);
        await WaitForAsync(() => sink.ApprovalRequests > 0);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.Empty(jobs.Updates);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition never became true");
            await Task.Delay(5);
        }
    }

    private sealed class TextOnlyProvider : ILlmProvider
    {
        public string ProviderId => "t"; public string DisplayName => "T";
        public string ModelId => "test-model";
        public ILlmProvider WithModel(string model) => this;
        public bool SupportsToolCalling => true; public bool SupportsStreaming => true;
        public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct)
            => throw new NotImplementedException();
        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
            List<ChatMessage> m, List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new LlmStreamChunk("no plan here", null, false);
            yield return new LlmStreamChunk(null, null, true);
            await Task.CompletedTask;
        }
    }

    [Fact]
    public void LooksLikeAPlanAsText_CatchesARealPayloadTheModelWroteAsProse()
    {
        // The exact shape seen live against a real repo (MimeKit): the model wrote a complete,
        // valid create_plan payload -- ids, depends_on, {{list_files.stdout}} references -- straight
        // into the transcript instead of CALLING the tool. The user got a wall of JSON and no job
        // ever ran, because the code treated "no tool call but some text" as a conversational reply.
        var asText = """
        {
          "goal": "count the .cs files",
          "jobs": [
            { "id": "list_files", "name": "List files", "type": "shell",
              "params": { "command": "ls MimeKit/Encodings/*.cs | wc -l" } },
            { "id": "report", "name": "Generate response", "type": "llm_agent",
              "depends_on": [ "list_files" ],
              "params": { "prompt": "State the count.\n\n{{list_files.stdout}}" } }
          ]
        }
        """;

        Assert.True(GoalRunner.LooksLikeAPlanAsText(asText));
    }

    [Theory]
    [InlineData("MimeKit is a .NET library for parsing MIME messages.")]
    [InlineData("I ran three jobs and they all succeeded.")]        // mentions "jobs", not a plan
    [InlineData("")]
    [InlineData("ok")]
    public void LooksLikeAPlanAsText_DoesNotFireOnAConversationalAnswer(string text)
    {
        // A false positive costs one wasted repair call; a false NEGATIVE silently ends the goal
        // having run nothing. But an answer that merely says "jobs" must not be mistaken for a plan
        // -- that would turn every ordinary reply into a retry.
        Assert.False(GoalRunner.LooksLikeAPlanAsText(text));
    }
}

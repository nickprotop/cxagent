using System.Text.Json;
using CxAgent.Core.Execution;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using CxAgent.Core.Plugins;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The consult loop: drive → digest → consult → adapt → drive again.
///
/// Every fixture here builds its dag through a REAL PluginRegistry + JobExecutor + DagScheduler and
/// actually runs it. Hand-building a Job and asserting on it without running has bitten this project
/// three times (the ULID-vs-display-name bug and two dropped persistence columns all survived a green
/// suite that way), so the added-job test asserts JobState.Succeeded — it RAN — not merely that it
/// was added to the graph.
/// </summary>
public class OrchestratorLoopTests
{
    private const string GoalId = "G1";

    // ---------------------------------------------------------------- fake provider

    /// <summary>
    /// Replays a fixed script of consult replies and records the prompt text it was sent each time.
    /// Every reply carries usage, so a loop that forgets to meter a consult shows up as a zero ledger.
    /// </summary>
    private sealed class RecordingProvider : ILlmProvider
    {
        private readonly Queue<LlmResponse> _responses;

        public RecordingProvider(params LlmResponse[] responses) => _responses = new Queue<LlmResponse>(responses);

        /// <summary>The flattened text of every message sent, one entry per ChatAsync call.</summary>
        public List<string> Prompts { get; } = new();

        public string ProviderId => "recording";
        public string DisplayName => "Recording";
        public string ModelId => "test-model";
        public ILlmProvider WithModel(string model) => this;
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
        {
            Prompts.Add(string.Join("\n", messages.Select(m => m.Content)));
            // Running dry means the loop consulted more times than the test scripted — a real failure
            // to surface, not a silent "continue".
            if (_responses.Count == 0)
                throw new InvalidOperationException("RecordingProvider ran out of scripted replies.");
            return Task.FromResult(_responses.Dequeue());
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var resp = await ChatAsync(messages, tools, ct);
            yield return new LlmStreamChunk(resp.Text, resp.ToolCalls.FirstOrDefault(), IsFinal: true, Usage: resp.Usage);
        }
    }

    // ---------------------------------------------------------------- reply builders

    private static LlmResponse Usage(LlmResponse r) =>
        r with { Usage = new LlmUsage { InputTokens = 10, OutputTokens = 5 } };

    private static LlmResponse ConsultReply(string action) =>
        Usage(LlmResponse.WithToolCall("consult", new { action, summary = "done", rationale = "because" }));

    /// <summary>A modify reply adding one job of the given plan-local id and plugin type.</summary>
    private static LlmResponse ConsultReplyAddingJob(string localId, string type) =>
        Usage(LlmResponse.WithToolCall("consult", new
        {
            action = "modify",
            jobs_to_add = new object[]
            {
                new { id = localId, name = $"Added {localId}", type, @params = new { seconds = 0.0 } },
            },
        }));

    /// <summary>A modify reply re-parameterising an existing job — the shape that consumes an edit budget.</summary>
    private static LlmResponse ConsultReplyEditing(string planLocalId) =>
        Usage(LlmResponse.WithToolCall("consult", new
        {
            action = "modify",
            parameter_changes = new Dictionary<string, object>
            {
                [planLocalId] = new { seconds = 0.0 },
            },
        }));

    /// <summary>A reply ConsultTool.Parse cannot map — an unknown action.</summary>
    private static LlmResponse GarbageReply() =>
        Usage(LlmResponse.WithToolCall("consult", new { action = "wat", nonsense = 1 }));

    // ---------------------------------------------------------------- dag fixtures

    private static (JobDag Dag, DagScheduler Scheduler) Build(params Job[] jobs)
    {
        var dag = new JobDag();
        foreach (var j in jobs) dag.AddJob(j);
        var plugins = PluginRegistry.CreateWithBuiltins();
        var executor = new JobExecutor(plugins, dag);
        return (dag, new DagScheduler(dag, maxParallel: 4, executor.RunJobAsync));
    }

    private static Job WaitJob(string localId, string name, double seconds = 0.0, params string[] dependsOn) => new()
    {
        Id = $"U-{localId}",
        PlanLocalId = localId,
        GoalId = GoalId,
        PluginType = "wait",
        DisplayName = name,
        Parameters = new JobParameters(new Dictionary<string, object?> { ["seconds"] = seconds }),
        DependsOn = dependsOn.ToList(),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>A job that always fails — a shell command with a non-zero exit.</summary>
    private static Job FailingJob(string localId, string name) => new()
    {
        Id = $"U-{localId}",
        PlanLocalId = localId,
        GoalId = GoalId,
        PluginType = "shell",
        DisplayName = name,
        Parameters = new JobParameters(new Dictionary<string, object?> { ["command"] = "exit 7" }),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static (JobDag, DagScheduler) ThreeIndependentJobs() =>
        Build(WaitJob("a", "Job A"), WaitJob("b", "Job B"), WaitJob("c", "Job C"));

    private static (JobDag, DagScheduler) OneJob() => Build(WaitJob("j1", "Only job"));

    private static (JobDag, DagScheduler) OneFailingJob() => Build(FailingJob("r1", "Failing job"));

    /// <summary>One quick job and one slower one, so a consult can land while the second is still
    /// meaningfully in the drive's lifetime — the shape that would break if the loop drove twice at once.</summary>
    private static (JobDag, DagScheduler) TwoJobsOneSlow() =>
        Build(WaitJob("fast", "Fast job"), WaitJob("wait", "Slow job", seconds: 0.05));

    /// <summary>A dag whose consults never naturally end — used to prove the MaxConsults cap fires.</summary>
    private static (JobDag, DagScheduler) LoopingJob() => Build(WaitJob("spin", "Spinning job"));

    // ---------------------------------------------------------------- loop builder

    private static OrchestratorLoop NewLoop(ILlmProvider provider, IChatSink? sink = null,
        TokenLedger? ledger = null, int maxConsults = 40, int maxEditsPerJob = 3) =>
        new(provider,
            ledger ?? new TokenLedger(null),
            PluginRegistry.CreateWithBuiltins(),
            new OrchestratorSettings(null, null, maxConsults, maxEditsPerJob),
            sink ?? new RecordingSink());

    private static OrchestratorLoop NewLoop(ILlmProvider provider, int maxEditsPerJob) =>
        NewLoop(provider, sink: null, ledger: null, maxConsults: 40, maxEditsPerJob: maxEditsPerJob);

    // ---------------------------------------------------------------- tests

    [Fact]
    public async Task Loop_ConsultsWithEveryJobFinishedSinceTheLastConsult()
    {
        // The user's decision: no timer, no window. Whatever finished since last time goes in one
        // consult — self-tuning, and no magic number to get wrong.
        var provider = new RecordingProvider(ConsultReply("continue"), ConsultReply("finish_goal"));
        var (dag, scheduler) = ThreeIndependentJobs();

        await NewLoop(provider).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        var firstConsult = provider.Prompts[0];
        foreach (var name in new[] { "Job A", "Job B", "Job C" })
            Assert.Contains(name, firstConsult);
    }

    [Fact]
    public async Task Loop_ContinueDoesNotModifyTheDag()
    {
        var provider = new RecordingProvider(ConsultReply("continue"), ConsultReply("finish_goal"));
        var (dag, scheduler) = OneJob();
        var before = dag.AllJobs.Count;

        await NewLoop(provider).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        Assert.Equal(before, dag.AllJobs.Count);
    }

    /// <summary>
    /// `continue` must be genuinely cheap — it is the expected common case. If it triggered a
    /// re-drive, the whole design collapses into "consult on everything, expensively". Asserting the
    /// dag is unchanged (the test above) does not catch that: a needless re-drive of an all-succeeded
    /// dag leaves the graph identical. Counting consults does catch it — a re-drive would re-run the
    /// job, enqueue it again, and force a SECOND consult carrying the same job.
    ///
    /// This also pins the loop's exit condition: a continue over a drained dag ends the goal, because
    /// nothing finished since and nothing is left to run. That is why one consult, not two, is right
    /// here — the loop never asks a question it has no new information for.
    /// </summary>
    [Fact]
    public async Task Loop_ContinueDoesNotRedriveOrReconsultTheSameJobs()
    {
        // Two calls: the consult, then the closing answer once work runs out. What this test pins is
        // that `continue` does not RE-CONSULT the same finished jobs — so assert on the CONSULT
        // prompts specifically, not the raw call count, which the closing turn also increments.
        var provider = new RecordingProvider(
            ConsultReply("continue"),
            Usage(new LlmResponse { Text = "nothing to add." }));
        var (dag, scheduler) = OneJob();

        await NewLoop(provider).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        var consults = provider.Prompts.Count(p => p.Contains("finished since you were last consulted"));
        Assert.Equal(1, consults);
        Assert.Contains("Only job", provider.Prompts[0]);
    }

    [Fact]
    public async Task Loop_ModifyAppliesTheDagModification_AndDrivesAgain()
    {
        // The core capability: a job the orchestrator adds AFTER seeing output must actually run.
        // This is the drive that would have caught the missing file-write job.
        var provider = new RecordingProvider(
            ConsultReplyAddingJob("write", "wait"),
            ConsultReply("finish_goal"));
        var (dag, scheduler) = OneJob();

        await NewLoop(provider).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        var added = dag.AllJobs.SingleOrDefault(j => j.PlanLocalId == "write");
        Assert.NotNull(added);
        Assert.Equal(JobState.Succeeded, added!.State);   // it RAN, not merely added
    }

    [Fact]
    public async Task Loop_StopsAtMaxConsults_AndSaysSo()
    {
        // A cap that ends the goal silently is worse than no cap — the user must learn WHY it stopped.
        var provider = new RecordingProvider(Enumerable.Repeat(ConsultReplyAddingJob("x", "wait"), 50)
            .Select((r, i) => Usage(LlmResponse.WithToolCall("consult", new
            {
                action = "modify",
                jobs_to_add = new object[]
                {
                    new { id = $"x{i}", name = $"Spin {i}", type = "wait", @params = new { seconds = 0.0 } },
                },
            }))).ToArray());
        var sink = new RecordingSink();
        var (dag, scheduler) = LoopingJob();

        var state = await NewLoop(provider, sink, maxConsults: 3).RunAsync(
            dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        // GoalState is { Draft, Active, Paused, Completed, Failed, Cancelled } — there is no Succeeded
        // (that is JobState). And the loop must OVERRIDE scheduler.FinalGoalState here: it would read
        // Completed if every job happened to succeed, which would report a capped goal as a success.
        Assert.Equal(GoalState.Failed, state);
        Assert.Contains(sink.Messages, m => m.Contains("consult", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Loop_StopsEditingAJobAtMaxEditsPerJob()
    {
        var provider = new RecordingProvider(Enumerable.Repeat(ConsultReplyEditing("r1"), 20).ToArray());
        var (dag, scheduler) = OneFailingJob();

        await NewLoop(provider, maxEditsPerJob: 2).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        Assert.True(dag.AllJobs.Single().OrchestratorEditCount <= 2);
    }

    /// <summary>
    /// The other half of the cap: it must actually COUNT. `<= 2` alone stays green if the loop never
    /// increments at all, which would leave the cap unenforceable.
    /// </summary>
    [Fact]
    public async Task Loop_CountsEachAppliedEditAgainstTheJobsBudget()
    {
        var provider = new RecordingProvider(Enumerable.Repeat(ConsultReplyEditing("r1"), 20).ToArray());
        var (dag, scheduler) = OneFailingJob();

        await NewLoop(provider, maxEditsPerJob: 2).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        Assert.Equal(2, dag.AllJobs.Single().OrchestratorEditCount);
    }

    [Fact]
    public async Task Loop_AnEditChargesEVERYJobInThePromptingBatch_NotJustTheEditedOne()
    {
        // MEASURING a design choice, not asserting a bug. The cap is charged to the batch whose
        // outcome PROMPTED the edit (OrchestratorLoop.cs:302-309) — deliberate, because the cycle it
        // breaks is "job fails, gets edited, fails again".
        //
        // The consequence on a FAN-OUT had never been tested: three jobs finishing together are all
        // charged for ONE edit to ONE of them. So a job that merely finished alongside a problem job
        // spends its budget on someone else's pathology, and can later have a legitimate edit refused.
        //
        // Pinned so the trade-off is visible and deliberate rather than discovered later in a drive.
        var provider = new RecordingProvider(
            ConsultReplyEditing("a"),
            ConsultReply("finish_goal"));
        var (dag, scheduler) = ThreeIndependentJobs();

        await NewLoop(provider).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        // ONE edit was applied, to job 'a' only...
        var charged = dag.AllJobs.Where(j => j.OrchestratorEditCount > 0).ToList();

        // ...but all three finished in the same batch, so all three paid for it.
        Assert.Equal(3, charged.Count);
        Assert.All(charged, j => Assert.Equal(1, j.OrchestratorEditCount));
    }

    [Fact]
    public async Task Loop_AFanOutJobIsChargedOncePerBatchItAppearsIn_NotPerEdit()
    {
        // The bound on the collateral charging above, measured rather than assumed. I predicted the
        // budget would drain across repeated edits and refuse a later legitimate one; it does NOT.
        //
        // A job is charged only for a batch it actually APPEARS in, and a finished job stops
        // appearing. So three edits aimed at one job charge the bystanders exactly ONCE — their
        // budget is spent by how often they re-enter a batch, not by how many edits happen.
        //
        // That materially softens the design concern: collateral charging is real but self-limiting,
        // so a wide fan-out does not silently exhaust every job's budget.
        var provider = new RecordingProvider(
            ConsultReplyEditing("a"),
            ConsultReplyEditing("a"),
            ConsultReplyEditing("a"),
            ConsultReply("finish_goal"));
        var (dag, scheduler) = ThreeIndependentJobs();

        await NewLoop(provider).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        // The bystanders were in ONE batch, so they carry ONE charge each...
        Assert.Equal(1, dag.AllJobs.Single(j => j.PlanLocalId == "b").OrchestratorEditCount);
        Assert.Equal(1, dag.AllJobs.Single(j => j.PlanLocalId == "c").OrchestratorEditCount);

        // ...while the repeatedly-edited job accumulates, because a re-parameterised job is re-armed
        // and finishes again, re-entering the next batch (see the re-arm at ApplyModificationAsync).
        Assert.True(dag.AllJobs.Single(j => j.PlanLocalId == "a").OrchestratorEditCount > 1);
    }

    [Fact]
    public async Task Loop_WhenEditsAreExhausted_TheORCHESTRATORIsNeverTold()
    {
        // The gap, measured. When the cap refuses an edit the USER is told via ShowError, but the
        // refusal never reaches the ORCHESTRATOR: nothing is added to the conversation and the next
        // consult prompt says nothing about it. So it proposes an edit, the edit silently vanishes,
        // and it has no signal to try something else — it can spend the remaining consults
        // re-proposing the same rejected change.
        //
        // This is the same shape as D15 (a limit with no consequence-message), which measurably cost
        // real work. Pinned as a CHARACTERISATION test: it asserts today's behaviour so a fix has a
        // failing test to flip, not an approval of it.
        var provider = new RecordingProvider(
            ConsultReplyEditing("r1"),
            ConsultReplyEditing("r1"),
            ConsultReplyEditing("r1"),   // this one is refused — budget spent
            ConsultReply("finish_goal"));
        var sink = new RecordingSink();
        var (dag, scheduler) = OneFailingJob();

        await NewLoop(provider, sink, maxEditsPerJob: 2).RunAsync(
            dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        // The user IS told.
        Assert.Contains(sink.Errors, e => e.Contains("edit limit", StringComparison.OrdinalIgnoreCase));

        // The orchestrator is NOT: no consult prompt mentions the exhausted budget.
        Assert.DoesNotContain(provider.Prompts, p =>
            p.Contains("edit limit", StringComparison.OrdinalIgnoreCase)
            || p.Contains("exhausted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Loop_MetersEveryConsultIntoTheLedger()
    {
        // The consult IS a provider call. JobDiagnoser recorded nowhere until P6's I3 and made
        // GoalTokenBudget fiction; a loop that multiplies calls cannot repeat that.
        //
        // TWO real consults, not one: a modify that adds a job, then the consult on that added job.
        // Metering only the first call would still satisfy `> 0`, so the exact total is what pins
        // "EVERY consult" — the property the test is named for.
        // THREE calls now: two consults, then the closing answer the loop asks for when work runs
        // out after a `continue`. That third call is a paid provider call and must be metered too —
        // this test is the one that proves it, which is exactly the "EVERY consult" property.
        var provider = new RecordingProvider(
            ConsultReplyAddingJob("second", "wait"),
            ConsultReply("continue"),
            Usage(new LlmResponse { Text = "done." }));
        var ledger = new TokenLedger(null);
        var (dag, scheduler) = OneJob();

        await NewLoop(provider, ledger: ledger).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        Assert.True(ledger.TotalTokens > 0);
        Assert.Equal(3, provider.Prompts.Count);
        Assert.Equal(45, ledger.TotalTokens);   // three calls x (10 in + 5 out) — every call, not just the first
    }

    [Fact]
    public async Task Loop_UnparseableDecision_ReportsAndEndsRatherThanGuessing()
    {
        var provider = new RecordingProvider(GarbageReply());
        var sink = new RecordingSink();
        var (dag, scheduler) = OneJob();

        await NewLoop(provider, sink).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        Assert.NotEmpty(sink.Errors);
    }

    /// <summary>
    /// Never guess: an unparseable reply must not be treated as a modification. The provider is
    /// scripted with exactly ONE reply, so a loop that guessed and carried on would run it dry and
    /// throw — but assert the dag directly too, since that is the harm being prevented.
    /// </summary>
    [Fact]
    public async Task Loop_UnparseableDecision_AddsNoJobs()
    {
        var provider = new RecordingProvider(GarbageReply());
        var (dag, scheduler) = OneJob();
        var before = dag.AllJobs.Count;

        await NewLoop(provider).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        Assert.Equal(before, dag.AllJobs.Count);
    }

    [Fact]
    public async Task Loop_UnparseableDecision_FailsTheGoal_NotReportsSuccess()
    {
        // Observed live on the third chained goal of a session: the consult reply became unparseable
        // as the conversation grew, and the user was shown "orchestrator stopped: the consult reply
        // could not be understood." followed by a green "Goal completed."
        //
        // The old `break` fell through to ShowGoalResult(scheduler.FinalGoalState), which reads
        // Completed whenever every job happened to succeed. The MaxConsults cap immediately above
        // already overrides for exactly this reason; this path did not.
        var provider = new RecordingProvider(GarbageReply());
        var (dag, scheduler) = OneJob();
        var sink = new RecordingSink();

        var state = await NewLoop(provider, sink).RunAsync(
            dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        Assert.Equal(GoalState.Failed, state);

        // And the user is TOLD it failed — the error line alone left a green result underneath it.
        Assert.Equal(GoalState.Failed, sink.Result);
    }

    [Fact]
    public async Task Loop_AppliesEditsOnlyAtQuiescence()
    {
        // DriveAsync THROWS on overlapping drives (P6's C2). Every modification must land when nothing
        // is in flight — a fire-and-forget re-entry faults into an unobserved Task.
        var (dag, scheduler) = TwoJobsOneSlow();
        var provider = new RecordingProvider(ConsultReplyAddingJob("late", "wait"), ConsultReply("finish_goal"));

        var ex = await Record.ExceptionAsync(() =>
            NewLoop(provider).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None));

        Assert.Null(ex);
    }

    /// <summary>
    /// finish_goal ends the loop and shows the model's summary — the user's only account of what the
    /// goal achieved when the orchestrator, not the scheduler, decided it was done.
    /// </summary>
    [Fact]
    public async Task Loop_FinishGoal_ReportsTheSummary()
    {
        var provider = new RecordingProvider(
            Usage(LlmResponse.WithToolCall("consult", new { action = "finish_goal", summary = "wrote the report" })));
        var sink = new RecordingSink();
        var (dag, scheduler) = OneJob();

        await NewLoop(provider, sink).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        Assert.Contains(sink.Messages, m => m.Contains("wrote the report", StringComparison.Ordinal));
    }

    /// <summary>
    /// A modification the compiler rejects (here: a job naming a plugin type no registry has) must be
    /// reported and the loop must carry on — ConsultJobCompiler returns false precisely so a bad
    /// consult does not kill a goal mid-flight.
    /// </summary>
    [Fact]
    public async Task Loop_UncompilableModification_IsReported_AndTheLoopSurvives()
    {
        var provider = new RecordingProvider(
            ConsultReplyAddingJob("bad", "no_such_plugin"),
            ConsultReply("finish_goal"));
        var sink = new RecordingSink();
        var (dag, scheduler) = OneJob();
        var before = dag.AllJobs.Count;

        var state = await NewLoop(provider, sink).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        Assert.NotEmpty(sink.Errors);
        Assert.Equal(before, dag.AllJobs.Count);      // nothing half-applied
        Assert.NotEqual(GoalState.Failed, state);     // the goal itself was not killed by the bad consult
    }

    /// <summary>
    /// The digest must carry the job's RESOLVED parameters, not just its name — that is the whole
    /// point of Task 1's JobDigest, and the loop is what actually puts them in front of the model.
    /// </summary>
    [Fact]
    public async Task Loop_AnswersTheUserWhenWorkRunsOutAfterAContinue()
    {
        // A live drive of "list ~/bin. what it does?" ran its jobs, gathered the file types and the
        // script contents, and then ended having answered NOTHING: the user saw job blocks and a
        // green "Goal completed", with no reply to the question they asked.
        //
        // `continue` requires no summary by design — right mid-run, wrong as the LAST word. When the
        // work runs out after one, the loop must still close the goal with an answer.
        //
        // Two replies: the consult (continue), then the closing answer. RecordingProvider throws when
        // it runs dry, so a loop that skipped the closing turn would fail on the assertion below
        // rather than silently pass.
        var provider = new RecordingProvider(
            ConsultReply("continue"),
            Usage(new LlmResponse { Text = "~/bin holds six shell scripts." }));
        var (dag, scheduler) = OneJob();

        var sink = new RecordingSink();
        await NewLoop(provider, sink).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        Assert.Contains("~/bin holds six shell scripts.", string.Concat(sink.AssistantTokens));

        // The closing prompt must carry the finished jobs' digests — an answer written without the
        // results would be invention, not a summary.
        Assert.Contains("Only job", provider.Prompts[1]);
    }

    [Fact]
    public async Task Loop_DoesNotPayForAClosingAnswerWhenFinishGoalAlreadySpoke()
    {
        // finish_goal already reports its Summary to the user. Asking for a second closing turn
        // would spend a paid call restating what was just said.
        var provider = new RecordingProvider(ConsultReply("finish_goal"));
        var (dag, scheduler) = OneJob();

        await NewLoop(provider).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        // Exactly one call: the consult. A closing turn would have thrown (provider runs dry).
        Assert.Single(provider.Prompts);
    }

    [Fact]
    public async Task Loop_ConsultPromptCarriesTheJobsParameters()
    {
        var provider = new RecordingProvider(ConsultReply("continue"), ConsultReply("finish_goal"));
        var (dag, scheduler) = Build(WaitJob("p1", "Parameterised job", seconds: 0.0));

        await NewLoop(provider).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        Assert.Contains("seconds", provider.Prompts[0]);
    }

    /// <summary>
    /// D13: the consult must also show the params of jobs that have NOT run.
    ///
    /// <para>`parameter_changes` is documented as "the FULL replacement params object"
    /// (ConsultTool.cs), but the batch contains only jobs that FINISHED — so a job that never ran was
    /// never shown, and the contract was unsatisfiable in exactly the case where repair matters most.
    /// A live drive caught the model saying so, correctly: "I don't have the current params of the
    /// 'file' job in the prompt history. I only have the error."</para>
    /// </summary>
    [Fact]
    public async Task Loop_DoesNotSpinWhenAJobIsBlockedByAFailedDependency()
    {
        // D14. A failed job leaves its dependents Pending FOREVER: JobDag.IsSatisfied accepts only
        // Succeeded/Skipped, and DagScheduler does not cascade-skip. So HasRunnableWork stays true,
        // the batch stays empty, and `if (batch.Count == 0) continue;` spun at 100% CPU with no
        // drive, no await and no consult.
        //
        // A 2-second timeout, because the failure mode is a HANG: without a bound this test would
        // wedge the whole suite rather than reporting.
        var provider = new RecordingProvider(ConsultReply("continue"), ConsultReply("finish_goal"));
        var (dag, scheduler) = Build(
            FailingJob("boom", "Failing job"),
            WaitJob("after", "Blocked job", seconds: 0.0, dependsOn: "U-boom"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var state = await NewLoop(provider).RunAsync(dag, scheduler, new List<ChatMessage>(), cts.Token);

        Assert.False(cts.IsCancellationRequested, "the loop spun instead of terminating (D14)");
        // Skipped, not Pending. D14's ROOT CAUSE was the missing cascade this comment describes --
        // it was worked around here at the loop level while the scheduler kept stranding dependents.
        // DagScheduler.CascadeUnreachable now drains them, so HasRunnableWork goes false on its own
        // rather than the loop having to tolerate a permanently-empty batch. The spin guard above is
        // still the point of this test; this line just no longer pins the defective state.
        Assert.Equal(JobState.Skipped, dag.TryGet("U-after")!.State);
    }

    [Fact]
    public async Task Loop_ConsultPromptCarriesTheParamsOfJobsThatHaveNotRunYet()
    {
        // `later` depends on `first`, so at the FIRST consult it is still pending — the exact shape
        // of a job the orchestrator might be asked to repair before it ever executes.
        // Tested against BuildConsultPrompt DIRECTLY rather than through a loop run. Two earlier
        // attempts went the loop route and both were worse than useless: a dependsOn chain trips D14
        // (the loop hot-spins forever on a dag with dependencies — pre-existing, filed separately),
        // and a merely-slow job finishes and enters the batch, so that version passed WITHOUT the fix
        // and proved nothing. After a mid-goal add the loop re-drives immediately, so the
        // in-dag-but-not-run state is real in production yet not observable from outside.
        var dag = new JobDag();
        var ran = WaitJob("done", "Finished job", seconds: 0.0);
        ran.State = JobState.Succeeded;
        var pending = WaitJob("todo", "Pending job", seconds: 0.0);
        dag.AddJob(ran);
        dag.AddJob(pending);

        var prompt = OrchestratorLoop.BuildConsultPrompt(new[] { ran }, dag);

        Assert.Contains("Jobs still to run", prompt);

        // The PARAMS, not just the name — a name alone cannot satisfy "send these back in full".
        // Scoped after the heading, so the finished job's digest (which also renders "seconds")
        // cannot satisfy it.
        var pendingSection = prompt[prompt.IndexOf("Jobs still to run", StringComparison.Ordinal)..];
        Assert.Contains("Pending job", pendingSection);
        Assert.Contains("seconds", pendingSection);

        // And the job that already ran must NOT be repeated there — its outcome is above.
        Assert.DoesNotContain("Finished job", pendingSection);
    }

    // ---------------------------------------------------------------- P10 task 1: goal outcome memory

    [Fact]
    public async Task Loop_RecordsTheGoalsOutcomeInTheSharedConversation()
    {
        // A follow-up prompt ("now summarise those") needs to know what the last goal FOUND. Today the
        // consult loop adds NOTHING to the conversation — grep "conversation.Add" in OrchestratorLoop
        // returns zero — so the orchestrator remembers being asked and not the answer it gave.
        var provider = new RecordingProvider(
            ConsultReply("continue"),
            Usage(new LlmResponse { Text = "~/bin holds six shell scripts." }));
        var conversation = new List<ChatMessage>();
        var (dag, scheduler) = OneJob();

        await NewLoop(provider).RunAsync(dag, scheduler, conversation, CancellationToken.None);

        // The ANSWER is what a follow-up builds on.
        Assert.Contains(conversation, m => m.Content.Contains("six shell scripts"));
    }

    [Fact]
    public async Task Loop_TheRecordedOutcomeIsASummary_NotTheWholeTranscript()
    {
        // Appending full digests would blow the context in three goals. What persists is the outcome,
        // not the working. Asserted as a SIZE bound so "summary" is a measured property, not a hope.
        var provider = new RecordingProvider(
            ConsultReply("continue"),
            Usage(new LlmResponse { Text = "done." }));
        var conversation = new List<ChatMessage>();
        var (dag, scheduler) = OneJob();

        await NewLoop(provider).RunAsync(dag, scheduler, conversation, CancellationToken.None);

        var added = string.Concat(conversation.Select(m => m.Content));
        Assert.True(added.Length < 2000, $"the goal's memory footprint was {added.Length} chars");
    }

    /// <summary>A finished job of a given plugin type carrying a body — the shape whose output the
    /// consult prompt either replays or withholds.</summary>
    private static Job Finished(string localId, string pluginType, string body) => new()
    {
        Id = $"U-{localId}", PlanLocalId = localId, GoalId = GoalId,
        PluginType = pluginType, DisplayName = $"{pluginType} job",
        State = JobState.Succeeded,
        Parameters = new JobParameters(new Dictionary<string, object?>()),
        CreatedAt = DateTimeOffset.UtcNow,
        Result = new JobResult
        {
            Success = true,
            Output = new Dictionary<string, object?> { ["content"] = body },
        },
    };

    [Fact]
    public void ConsultPrompt_DoesNotReplayAFileJobsContents()
    {
        var dag = new JobDag();
        var read = Finished("f1", "file", new string('x', 9007));
        dag.AddJob(read);

        var prompt = OrchestratorLoop.BuildConsultPrompt(new[] { read }, dag);

        Assert.DoesNotContain("xxxx", prompt);
        Assert.Contains("9,007", prompt);
    }

    [Fact]
    public void ConsultPrompt_STILLCarriesAWorkersOutput()
    {
        var dag = new JobDag();
        var worker = Finished("w1", "llm_agent", new string('w', 9007));
        dag.AddJob(worker);

        Assert.Contains("wwww", OrchestratorLoop.BuildConsultPrompt(new[] { worker }, dag));
    }

    [Fact]
    public void ConsultPrompt_ListsTheIdOfAJobThatFinishedInAnEARLIERBatch()
    {
        // THE COLLISION BUG. The prompt showed only the current batch and the non-terminal pending
        // jobs -- so a job that succeeded in an earlier batch appeared in NEITHER list, while its
        // plan-local id stayed live in the dag and kept colliding. The model proposed 'read_hex' for
        // a new job with no way to know it was taken, and the whole modification was rejected.
        var dag = new JobDag();
        var early = Finished("read_hex", "file", "already done, not in this batch");
        var current = Finished("read_uu", "file", "just finished");
        dag.AddJob(early);
        dag.AddJob(current);

        var prompt = OrchestratorLoop.BuildConsultPrompt(new[] { current }, dag);

        Assert.Contains("read_hex", prompt);
    }

    [Fact]
    public void ConsultPrompt_SaysWhichJobsHaveALREADYRUN()
    {
        // The RE-PLANNING bug, and the reason the id list carries state. `batch` is drained per
        // consult, so a job appears in exactly one and is invisible after; `pending` covers only
        // non-terminal work. On a four-way fan-out the reviews land across several consults, so by
        // the last one the orchestrator saw no evidence the earlier three had run -- and re-planned
        // reviews of files it had already reviewed, at real token cost.
        var dag = new JobDag();
        var earlierSuccess = Finished("review_hex", "llm_agent", "findings for hex");
        var current = Finished("review_uu", "llm_agent", "findings for uu");
        var notRun = WaitJob("write_report", "Write the report");
        dag.AddJob(earlierSuccess); dag.AddJob(current); dag.AddJob(notRun);

        var prompt = OrchestratorLoop.BuildConsultPrompt(new[] { current }, dag);

        Assert.Contains("review_hex (done)", prompt);       // the invisible one, now visible
        Assert.Contains("write_report (not run yet)", prompt);
        Assert.Contains("ALREADY RUN", prompt);
    }

    [Fact]
    public void ConsultPrompt_MarksASkippedJobAsNOTRun()
    {
        // A skipped job is work still MISSING, not work completed. Rendering it as anything
        // done-adjacent would tell the orchestrator a gap had been filled when it had not.
        var dag = new JobDag();
        var ran = Finished("r1", "llm_agent", "output");
        var skipped = WaitJob("s1", "Skipped job");
        skipped.State = JobState.Skipped;
        dag.AddJob(ran); dag.AddJob(skipped);

        var prompt = OrchestratorLoop.BuildConsultPrompt(new[] { ran }, dag);

        Assert.Contains("s1 (not run (skipped))", prompt);
    }

    [Fact]
    public void ConsultPrompt_ListsEVERYPendingJob_NotJustTheFirstTwelve()
    {
        // D13 capped this at 12 on the premise that "the ones that matter for a repair are the ones
        // near the front". Backwards: a plan's LATER jobs depend on everything before them, so they
        // are the likeliest to need repair and were the first to be hidden -- and a job the
        // orchestrator cannot see is one it cannot fix, since parameter_changes needs the full
        // replacement params.
        var dag = new JobDag();
        var ran = Finished("r0", "llm_agent", "output");
        dag.AddJob(ran);
        for (var i = 1; i <= 20; i++) dag.AddJob(WaitJob($"p{i}", $"Pending job {i}"));

        var prompt = OrchestratorLoop.BuildConsultPrompt(new[] { ran }, dag);

        Assert.Contains("p20", prompt);   // the last one, well past the old cap of 12
        Assert.Contains("p13", prompt);   // the first one the cap used to hide
    }

    [Fact]
    public async Task Loop_ChallengesFinishGoal_WhileJobsAreStillUnrun()
    {
        // A live drive of "review X and Y, then write a combined report to AUDIT.md" reviewed both
        // files, answered in prose, and called finish_goal -- AUDIT.md was never written and the goal
        // reported success. A deliverable the user has to NOTICE is missing is worse than a visible
        // failure, so an early finish_goal is challenged once against the work still outstanding.
        var provider = new RecordingProvider(
            ConsultReply("finish_goal"),      // premature: 'blocked' cannot have run
            ConsultReply("finish_goal"));     // stands by it -- honoured the second time
        var dag = new JobDag();
        var ran = WaitJob("done", "Finished job", seconds: 0.0);
        // Pending and unreachable: it depends on a job that is not in the dag at all, so the
        // scheduler never releases it and it is still Pending when the consult happens. That is the
        // shape of the live failure -- a deliverable the plan named but never produced.
        var blocked = WaitJob("blocked", "Never ran", 0.0, "U-missing");
        dag.AddJob(ran); dag.AddJob(blocked);
        var scheduler = new DagScheduler(dag, maxParallel: 1,
            runJob: (j, ct) => Task.FromResult(new JobResult { Success = true, ExitCode = 0 }));

        await NewLoop(provider).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        Assert.Contains(provider.Prompts, p => p.Contains("Not so fast") && p.Contains("blocked"));
    }

    [Fact]
    public async Task Loop_DoesNOTChallengeFinishGoal_WhenEverythingHasSettled()
    {
        // The guard against nagging. A goal whose jobs have all finished must end on the FIRST
        // finish_goal -- a check that fires on every goal is one the model learns to dismiss.
        var provider = new RecordingProvider(ConsultReply("finish_goal"));
        var (dag, scheduler) = OneJob();

        await NewLoop(provider).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        Assert.DoesNotContain(provider.Prompts, p => p.Contains("Not so fast"));
    }

    [Fact]
    public async Task Loop_DoesNOTChallengeFinishGoal_OverAFailedJob()
    {
        // A FAILED job is not unrun work -- it had its chance. Challenging over it would nag on
        // every goal that ends with a known failure.
        var provider = new RecordingProvider(ConsultReply("finish_goal"));
        var (dag, scheduler) = OneFailingJob();

        await NewLoop(provider).RunAsync(dag, scheduler, new List<ChatMessage>(), CancellationToken.None);

        Assert.DoesNotContain(provider.Prompts, p => p.Contains("Not so fast"));
    }
}

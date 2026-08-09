using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Plugins;
using CxAgent.Core.Plugins.Builtin;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class LlmAgentJobPluginTests
{
    // ---- helpers -----------------------------------------------------------------------------

    private static JobParameters Params(params (string Key, object? Value)[] pairs) =>
        new(pairs.ToDictionary(p => p.Key, p => p.Value));

    /// <summary>A registry over one 'local' mock instance, which is also the default. Was a
    /// RoleResolver until roles were removed; the plugin only ever needed the provider.</summary>
    private static ProviderRegistry ResolverWith(ILlmProvider provider) =>
        ProviderRegistry.FromProviders(
            new Dictionary<string, ILlmProvider> { ["local"] = provider }, "local");

    private static ProviderRegistry Resolver() => ResolverWith(new MockLlmProvider());

    /// <summary>A registry with the built-in plugins, for schema tests that need tool text generated
    /// from real plugin schemas rather than a null-registry (no-tools) plugin instance.</summary>
    private static PluginRegistry Registry() => PluginRegistry.CreateWithBuiltins();

    /// <summary>A registry with no default provider at all.</summary>
    private static ProviderRegistry ResolverWithNoProvider() =>
        ProviderRegistry.FromProviders(new Dictionary<string, ILlmProvider>(), null);

    private sealed class ThrowingProvider : ILlmProvider
    {
        public string ProviderId => "throwing";
        public string DisplayName => "Throwing Provider";
        public string ModelId => "throwing-model";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => false;
        public ILlmProvider WithModel(string model) => this;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
            => throw new LlmProviderException("throwing", 500, "boom", "provider exploded");

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            throw new LlmProviderException("throwing", 500, "boom", "provider exploded");
            #pragma warning disable CS0162
            yield break;
            #pragma warning restore CS0162
        }
    }

    // ---- validation --------------------------------------------------------------------------

    [Fact]
    public void TypeName_IsLlmAgent()
    {
        Assert.Equal("llm_agent", new LlmAgentJobPlugin(Resolver()).TypeName);
    }

    [Fact]
    public void Validate_RequiresPrompt()
    {
        var p = new LlmAgentJobPlugin(Resolver());
        Assert.False(p.Validate(Params(("role", "reviewer"))).IsValid);
        Assert.True(p.Validate(Params(("role", "reviewer"), ("prompt", "check this"))).IsValid);
    }

    [Fact]
    public void Validate_AllowsMissingRole()
    {
        // An absent role means "default provider" — the same fallback RoleResolver implements.
        Assert.True(new LlmAgentJobPlugin(Resolver()).Validate(Params(("prompt", "do it"))).IsValid);
    }

    // ---- execution ---------------------------------------------------------------------------

    [Fact]
    public async Task Execute_NoRole_SendsNoROLESCAFFOLDING()
    {
        // Was Execute_NoRole_SendsNoSystemMessage. The INTENT is unchanged and still asserted: an
        // absent role must not produce empty scaffolding claiming a role the worker does not have.
        // But a system message now also carries the working directory, which is a FACT the worker
        // needs -- and omitting it precisely for the least-instructed worker is backwards.
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "done" });
        var p = new LlmAgentJobPlugin(ResolverWith(mock));

        await p.ExecuteAsync(Params(("prompt", "do it")), new TestJobContext(), CancellationToken.None);

        var system = mock.LastMessages!.SingleOrDefault(m => m.Role == "system")?.Content ?? "";
        Assert.DoesNotContain("acting as", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Directory.GetCurrentDirectory(), system);
    }

    [Fact]
    public async Task Execute_WithDependencies_SendsTheirOutputsToTheModel()
    {
        // The fan-out-then-join case: three reviews, one synthesizer. Without this the synthesizer is
        // asked to summarise reviews it was never shown.
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "synthesis" });
        var p = new LlmAgentJobPlugin(ResolverWith(mock));

        var ctx = new TestJobContext(completedOutputs: new Dictionary<string, JobResult>
        {
            ["r1"] = new() { Success = true, Output = { ["content"] = "defect: null deref line 42" } },
        });

        await p.ExecuteAsync(Params(("role", "reviewer"), ("prompt", "synthesise the reviews")), ctx, CancellationToken.None);

        var user = mock.LastMessages!.Last(m => m.Role == "user");
        Assert.Contains("null deref line 42", user.Content);
        Assert.Contains("synthesise the reviews", user.Content);
    }

    [Fact]
    public async Task Execute_ProviderThrows_ReturnsFailedResultNotException()
    {
        var p = new LlmAgentJobPlugin(ResolverWith(new ThrowingProvider()));
        var result = await p.ExecuteAsync(Params(("prompt", "x")), new TestJobContext(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [Fact]
    public async Task Execute_Cancelled_PropagatesRatherThanReportingAJobFailure()
    {
        // A cancelled goal is not a failed job — swallowing OperationCanceledException here would
        // report every user-initiated stop as an LLM error.
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "unused" });
        var p = new LlmAgentJobPlugin(ResolverWith(mock));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            p.ExecuteAsync(Params(("prompt", "x")), new TestJobContext(), cts.Token));
    }

    [Fact]
    public async Task Execute_NoProviderConfigured_FailsWithClearMessage()
    {
        // ResolvedRole.Provider is null exactly here. Dereferencing it would be an NRE mid-goal.
        var p = new LlmAgentJobPlugin(ResolverWithNoProvider());
        var result = await p.ExecuteAsync(Params(("prompt", "x")), new TestJobContext(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("provider", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- logging -----------------------------------------------------------------------------

    [Fact]
    public async Task Execute_UnboundRole_LogsNoWarning()
    {
        // Normal on a fresh install. A warning here would train users to ignore warnings.
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "ok" });
        var p = new LlmAgentJobPlugin(ResolverWith(mock));

        var ctx = new TestJobContext();
        await p.ExecuteAsync(Params(("role", "reviewer"), ("prompt", "x")), ctx, CancellationToken.None);

        Assert.DoesNotContain(ctx.Logs, l => l.Level == JobLogLevel.Warning);
    }

    [Fact]
    public async Task Execute_LogsToolCalls()
    {
        // This log line is the ONLY record a job's tool calls happened — nothing else persists them.
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(LlmResponse.WithToolCall("read_file", new { path = "/etc/hosts" }));
        var p = new LlmAgentJobPlugin(ResolverWith(mock));

        var ctx = new TestJobContext();
        await p.ExecuteAsync(Params(("prompt", "x")), ctx, CancellationToken.None);

        Assert.Contains(ctx.Logs, l => l.Line.Contains("read_file") && l.Line.Contains("/etc/hosts"));
    }

    [Fact]
    public async Task Execute_LogsResolvedInstanceAndModel()
    {
        var mock = new MockLlmProvider("some-model");
        mock.EnqueueResponse(new LlmResponse { Text = "ok" });
        var p = new LlmAgentJobPlugin(ResolverWith(mock));

        var ctx = new TestJobContext();
        await p.ExecuteAsync(Params(("prompt", "x")), ctx, CancellationToken.None);

        Assert.Contains(ctx.Logs, l => l.Line.Contains("some-model"));
    }

    // ---- the worker tool loop (P8b Task 3) -----------------------------------------------------

    [Fact]
    public async Task Execute_WorkerCanCallAToolAndUseTheResult()
    {
        // The whole point. Before this, a worker received a prompt and returned text — it could not read
        // the file it was asked to review.
        var path = Path.Combine(Path.GetTempPath(), $"wl-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "SECRET_MARKER");

        var mock = new MockLlmProvider();
        mock.EnqueueResponse(LlmResponse.WithToolCall("read_file", new { action = "read", path }));
        mock.EnqueueResponse(new LlmResponse { Text = "the file says SECRET_MARKER" });

        var result = await new LlmAgentJobPlugin(ResolverWith(mock), PluginRegistry.CreateWithBuiltins()).ExecuteAsync(
            Params(("role", "reviewer"), ("prompt", "what is in that file?")),
            new TestJobContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("SECRET_MARKER", result.Output["content"]!.ToString()!);
        File.Delete(path);
    }

    [Fact]
    public async Task Execute_ToolResultReachesTheModel_AsAToolResultMessage()
    {
        // ToolCallId is the ONLY field that makes a message a tool result — both wires branch on it
        // and set the role themselves. LlmResponse.WithToolCall leaves ToolCall.Id null, so a result
        // appended with a bare `call.Id` goes out as an ordinary user message: no error, no warning,
        // the model simply never sees it. This pins the `call.Id ?? call.Name` fallback.
        var path = Path.Combine(Path.GetTempPath(), $"wl-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "SECRET_MARKER");

        var mock = new MockLlmProvider();
        mock.EnqueueResponse(LlmResponse.WithToolCall("read_file", new { action = "read", path }));
        mock.EnqueueResponse(new LlmResponse { Text = "done" });

        await new LlmAgentJobPlugin(ResolverWith(mock), PluginRegistry.CreateWithBuiltins()).ExecuteAsync(
            Params(("role", "reviewer"), ("prompt", "read it")), new TestJobContext(), CancellationToken.None);

        var toolResult = Assert.Single(mock.LastMessages!, m => m.ToolCallId is not null);
        Assert.Equal("read_file", toolResult.ToolCallId);   // the Id-is-null fallback
        Assert.Contains("SECRET_MARKER", toolResult.Content);

        // The assistant turn carrying the call must be there too, or the tool result dangles.
        Assert.Contains(mock.LastMessages!, m => m.ToolCalls is { Count: > 0 });
        File.Delete(path);
    }

    [Fact]
    public async Task Execute_StopsAtMaxWorkerTurns_AndSaysSo()
    {
        // A worker that loops forever burns the goal's whole budget. P8's caps exist because that
        // happened one level up.
        var mock = new MockLlmProvider();
        for (int i = 0; i < 30; i++)
            mock.EnqueueResponse(LlmResponse.WithToolCall("read_file", new { action = "read", path = "/tmp/x" }));

        var ctx = new TestJobContext();
        var result = await new LlmAgentJobPlugin(ResolverWith(mock), PluginRegistry.CreateWithBuiltins(),
            maxWorkerTurns: 3).ExecuteAsync(
            Params(("role", "reviewer"), ("prompt", "loop")), ctx, CancellationToken.None);

        // SUCCEEDS, deliberately: the cap is a boundary the worker hit, not a malfunction. A failure
        // here would fire AppBootstrap's automatic diagnosis (:85) on a limit we set on purpose.
        Assert.True(result.Success);
        Assert.Equal(true, result.Output!["truncated"]);
        Assert.Contains(ctx.LogLines, l => l.Contains("turn", StringComparison.OrdinalIgnoreCase));

        // Exactly the cap — not cap+1, and not "we stopped after the tool call but before the reply".
        Assert.Equal(3, mock.ChatCallCount);
    }

    [Fact]
    public async Task Execute_TurnCapHit_LogsAWarning_NotJustInfo()
    {
        // Warning level specifically: the job panel filters on it, and a truncated worker is
        // something the user has to be able to see.
        var mock = new MockLlmProvider();
        for (int i = 0; i < 10; i++)
            mock.EnqueueResponse(LlmResponse.WithToolCall("read_file", new { action = "read", path = "/tmp/x" }));

        var ctx = new TestJobContext();
        await new LlmAgentJobPlugin(ResolverWith(mock), PluginRegistry.CreateWithBuiltins(), maxWorkerTurns: 2)
            .ExecuteAsync(Params(("role", "reviewer"), ("prompt", "loop")), ctx, CancellationToken.None);

        Assert.Contains(ctx.Logs, l => l.Level == JobLogLevel.Warning
                                       && l.Line.Contains("turn", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Execute_AnImplementerIsOfferedWriteFile()
    {
        // The other half of the role/tool split — a producer must actually be able to produce.
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "done" });

        await new LlmAgentJobPlugin(ResolverWith(mock), PluginRegistry.CreateWithBuiltins()).ExecuteAsync(
            Params(("role", "implementer"), ("prompt", "x")), new TestJobContext(), CancellationToken.None);

        Assert.Contains(mock.LastTools!, t => t.Name == "write_file");
    }

    [Fact]
    public async Task Execute_NoPluginRegistry_OffersNoToolsAndCallsOnce()
    {
        // `plugins: null` MUST preserve today's behaviour exactly — one call, tools null. Every
        // existing call site relies on it.
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "plain answer" });

        var result = await new LlmAgentJobPlugin(ResolverWith(mock)).ExecuteAsync(
            Params(("role", "reviewer"), ("prompt", "x")), new TestJobContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, mock.ChatCallCount);
        Assert.True(mock.LastTools is null || mock.LastTools.Count == 0);
    }

    [Fact]
    public async Task Execute_ARoleWithNoToolsStillWorks()
    {
        // A worker with no tools must behave exactly as it did before this plan — one call, text back.
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "plain answer" });

        var result = await new LlmAgentJobPlugin(ResolverWith(mock)).ExecuteAsync(
            Params(("prompt", "no role, no tools")), new TestJobContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("plain answer", result.Output["content"]!.ToString()!);
    }

    [Fact]
    public async Task Execute_EveryToolCallIsLogged()
    {
        // Already half-built: the plugin logs response.ToolCalls today, for a capability that did not
        // exist. That log is the ONLY record a job's tool calls happened, and P8's get_job_output
        // reads it. This EXTENDS Execute_LogsToolCalls to a second turn rather than replacing it.
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(LlmResponse.WithToolCall("read_file", new { action = "read", path = "/tmp/x" }));
        mock.EnqueueResponse(new LlmResponse { Text = "done" });
        var ctx = new TestJobContext();

        await new LlmAgentJobPlugin(ResolverWith(mock), PluginRegistry.CreateWithBuiltins()).ExecuteAsync(
            Params(("role", "reviewer"), ("prompt", "x")), ctx, CancellationToken.None);

        Assert.Contains(ctx.LogLines, l => l.Contains("read_file"));
    }

    [Fact]
    public async Task Execute_RecordsUsageOnEveryTurn_NotJustTheLast()
    {
        // The ledger drives the goal token budget and the status-bar cost readout. Recording only the
        // final turn would under-count a tool-using worker by however many turns it took.
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse
        {
            Usage = new LlmUsage { InputTokens = 10, OutputTokens = 5 },
            ToolCalls = { new ToolCall { Name = "read_file", Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { action = "read", path = "/tmp/x" }) } },
        });
        mock.EnqueueResponse(new LlmResponse { Text = "done", Usage = new LlmUsage { InputTokens = 20, OutputTokens = 7 } });

        var recorded = new List<LlmUsage>();
        await new LlmAgentJobPlugin(ResolverWith(mock), PluginRegistry.CreateWithBuiltins(),
            onUsage: recorded.Add).ExecuteAsync(
            Params(("role", "reviewer"), ("prompt", "x")), new TestJobContext(), CancellationToken.None);

        Assert.Equal(2, recorded.Count);
        Assert.Equal(30, recorded.Sum(u => u.InputTokens));
        Assert.Equal(12, recorded.Sum(u => u.OutputTokens));
    }

    // ---- model hint (Task 9b) ----------------------------------------------------------------

    [Fact]
    public async Task Execute_ModelHint_OverridesTheRoleModel()
    {
        var mock = new MockLlmProvider("role-model");
        mock.EnqueueResponse(new LlmResponse { Text = "ok" });
        var p = new LlmAgentJobPlugin(ResolverWith(mock));

        var ctx = new TestJobContext();
        await p.ExecuteAsync(Params(("prompt", "x"), ("model_hint", "hinted-model")), ctx, CancellationToken.None);

        // The resolved model is logged, which is the only place a hint's effect is visible.
        Assert.Contains(ctx.Logs, l => l.Line.Contains("hinted-model"));
    }

    [Fact]
    public async Task Execute_HintNamingUnknownInstance_DoesNotFailTheJob()
    {
        // An LLM-supplied model string is untrusted input. A hint must never fail a job.
        var mock = new MockLlmProvider("role-model");
        mock.EnqueueResponse(new LlmResponse { Text = "ok" });
        var p = new LlmAgentJobPlugin(ResolverWith(mock));

        var result = await p.ExecuteAsync(
            Params(("prompt", "x"), ("model_hint", "ghost-instance/m")), new TestJobContext(), CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public void Validate_DoesNotRejectAModelHint()
    {
        // Validation must not be where an untrusted hint fails — resolution degrades instead.
        Assert.True(new LlmAgentJobPlugin(Resolver())
            .Validate(Params(("prompt", "x"), ("model_hint", "whatever/it/invents"))).IsValid);
    }

    [Fact]
    public void GetSchema_ModelHint_NamesConfiguredInstances()
    {
        // So the orchestrator hints from a real list rather than inventing ids.
        var registry = ProviderRegistry.FromProviders(new Dictionary<string, ILlmProvider>
        {
            ["local"] = new MockLlmProvider("qwen3.6-35b-a3b"),
            ["openrouter-main"] = new MockLlmProvider("anthropic/claude-sonnet-4-5"),
        }, "local");
        var plugin = new LlmAgentJobPlugin(registry);

        var hint = Assert.Single(plugin.GetSchema().Params, s => s.Name == "model_hint");
        Assert.False(hint.Required);
        Assert.Contains("local", hint.Description);
        Assert.Contains("openrouter-main", hint.Description);
        Assert.Contains("anthropic/claude-sonnet-4-5", hint.Description);
    }

    // ---- dependency formatting ---------------------------------------------------------------

    [Fact]
    public void FormatDependencyOutputs_IncludesEachJobIdAndOutput()
    {
        var outputs = new Dictionary<string, JobResult>
        {
            ["r1"] = new() { Success = true, Output = { ["content"] = "found 2 defects in auth.cs" } },
            ["r2"] = new() { Success = true, Output = { ["content"] = "no issues found" } },
        };

        var text = LlmAgentJobPlugin.FormatDependencyOutputs(outputs);
        Assert.Contains("r1", text);
        Assert.Contains("found 2 defects", text);
        Assert.Contains("r2", text);
        Assert.Contains("no issues found", text);
    }

    [Fact]
    public void FormatDependencyOutputs_TruncatesLongOutput_WithVisibleMarker()
    {
        // The model must KNOW something was cut, or it reasons confidently about text it never saw.
        var big = new string('x', 10_000);
        var outputs = new Dictionary<string, JobResult>
        {
            ["r1"] = new() { Success = true, Output = { ["content"] = big } },
        };

        var text = LlmAgentJobPlugin.FormatDependencyOutputs(outputs, perJobCap: 200);
        Assert.True(text.Length < 1_000);
        Assert.Contains("elided", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatDependencyOutputs_NamesLogFile_WhenTruncated()
    {
        // Truncation is only acceptable if a follow-up job can still reach the whole thing.
        var outputs = new Dictionary<string, JobResult>
        {
            ["r1"] = new() { Success = true, LogFile = "/var/log/cxagent/r1.log",
                             Output = { ["content"] = new string('x', 10_000) } },
        };

        var text = LlmAgentJobPlugin.FormatDependencyOutputs(outputs, perJobCap: 200);
        Assert.Contains("/var/log/cxagent/r1.log", text);
    }

    [Fact]
    public void FormatDependencyOutputs_NeverTruncatesErrors()
    {
        // A truncated error message is worse than useless — it is the one thing a debugger role needs whole.
        var err = "Unhandled exception: " + new string('e', 5_000);
        var outputs = new Dictionary<string, JobResult>
        {
            ["r1"] = new() { Success = false, ErrorMessage = err },
        };

        var text = LlmAgentJobPlugin.FormatDependencyOutputs(outputs, perJobCap: 200);
        Assert.Contains(err, text);
    }

    [Fact]
    public void FormatDependencyOutputs_MarksFailedDependencies()
    {
        var outputs = new Dictionary<string, JobResult>
        {
            ["r1"] = new() { Success = false, ErrorMessage = "connection refused" },
        };

        var text = LlmAgentJobPlugin.FormatDependencyOutputs(outputs);
        Assert.Contains("Failed", text);
        Assert.Contains("connection refused", text);
    }

    // Realistic production keys: JobExecutor keys CompletedJobOutputs by Job.Id, which is a ULID
    // ("Job IDs (ULIDs), not names" — Job.cs:22). Tests that hand-build the dictionary with "r1"/"r2"
    // never observe how the label actually reads to the worker.
    private const string Ulid1 = "01JQZX9K2M4P7R8VTBCDEFGHJK";
    private const string Ulid2 = "01JQZX9K2M4P7R8VTBCDEFGHJM";

    [Fact]
    public void FormatDependencyOutputs_UsesDisplayName_NotTheUlid()
    {
        // The worker's prompt was authored against job NAMES; a 26-char opaque ULID gives the model
        // nothing to bind "the first reviewer disagreed with the second" to, and burns ~80 tokens of
        // noise per dependency.
        var outputs = new Dictionary<string, JobResult>
        {
            [Ulid1] = new() { Success = true, Output = { ["content"] = "found 2 defects in auth.cs" } },
        };
        var names = new Dictionary<string, string> { [Ulid1] = "Review auth.cs" };

        var text = LlmAgentJobPlugin.FormatDependencyOutputs(outputs, names);

        Assert.Contains("Review auth.cs", text);
        Assert.DoesNotContain(Ulid1, text);
    }

    [Fact]
    public void FormatDependencyOutputs_FallsBackToTheKey_WhenNoDisplayNameIsKnown()
    {
        // A missing name must not produce an unlabelled block — attribution degrades, never vanishes.
        var outputs = new Dictionary<string, JobResult>
        {
            [Ulid1] = new() { Success = true, Output = { ["content"] = "x" } },
        };

        var text = LlmAgentJobPlugin.FormatDependencyOutputs(outputs, new Dictionary<string, string>());
        Assert.Contains(Ulid1, text);
    }

    [Fact]
    public void FormatDependencyOutputs_OrdersBlocksDeterministically()
    {
        // Dictionary enumeration order is not a documented guarantee. P8 reuses this helper with a
        // dictionary from a different source; without an explicit sort, "summarise these three reviews
        // in order" silently reorders between runs with no error.
        var outputs = new Dictionary<string, JobResult>
        {
            [Ulid2] = new() { Success = true, Output = { ["content"] = "second" } },
            [Ulid1] = new() { Success = true, Output = { ["content"] = "first" } },
        };

        var text = LlmAgentJobPlugin.FormatDependencyOutputs(outputs);

        // Ulid1 sorts before Ulid2 ordinally, regardless of insertion order above.
        Assert.True(text.IndexOf("first", StringComparison.Ordinal)
                    < text.IndexOf("second", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Execute_WithDependencies_LabelsThemByDisplayName()
    {
        // End-to-end through the context, with production-shaped keys — the path I1 flagged as
        // untested, since ExecuteAsync is what reads CompletedJobNames off IJobContext.
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "synthesis" });
        var p = new LlmAgentJobPlugin(ResolverWith(mock));

        var ctx = new TestJobContext(
            completedOutputs: new Dictionary<string, JobResult>
            {
                [Ulid1] = new() { Success = true, Output = { ["content"] = "defect: null deref line 42" } },
            },
            displayNames: new Dictionary<string, string> { [Ulid1] = "Review auth.cs" });

        await p.ExecuteAsync(Params(("prompt", "synthesise")), ctx, CancellationToken.None);

        var user = mock.LastMessages!.Last(m => m.Role == "user");
        Assert.Contains("Review auth.cs", user.Content);
        Assert.Contains("defect: null deref line 42", user.Content);
        Assert.DoesNotContain(Ulid1, user.Content);
    }

    [Fact]
    public void FormatDependencyOutputs_Empty_ReturnsEmpty()
    {
        // A job with no dependencies must not get an empty "Dependency results:" header.
        Assert.Equal("", LlmAgentJobPlugin.FormatDependencyOutputs(new Dictionary<string, JobResult>()));
    }

    // ---- total budget, not per-item (the P10 Task 4 fix) ---------------------------------------

    /// <summary>Builds a context with exactly one completed dependency carrying the given output.</summary>
    private static TestJobContext ContextWithDependencyOutput(string name, string content) =>
        new(completedOutputs: new Dictionary<string, JobResult>
            {
                [Ulid1] = new() { Success = true, Output = { ["content"] = content } },
            },
            displayNames: new Dictionary<string, string> { [Ulid1] = name });

    /// <summary>
    /// Builds a context with <paramref name="count"/> completed dependencies, each carrying
    /// <paramref name="eachChars"/> characters of output, keyed by distinct time-ordered ULID-shaped
    /// ids so <c>FormatDependencyOutputs</c>' ordinal sort produces a stable, predictable order —
    /// the same production shape as <see cref="Ulid1"/>/<see cref="Ulid2"/> above.
    /// </summary>
    private static TestJobContext ContextWithManyDependencies(int count, int eachChars)
    {
        var outputs = new Dictionary<string, JobResult>();
        var names = new Dictionary<string, string>();
        for (int i = 0; i < count; i++)
        {
            var id = $"01JQZX9K2M4P7R8VTBCDEFG{i:D4}";
            outputs[id] = new() { Success = true, Output = { ["content"] = new string('x', eachChars) } };
            names[id] = $"dep{i}";
        }
        return new TestJobContext(completedOutputs: outputs, displayNames: names);
    }

    [Fact]
    public async Task Execute_ASingleLargeDependency_IsNotTruncatedToTheFixedPerJobCap()
    {
        // A worker asked to summarise ONE file should see that file. The fixed 2048-char cap threw away
        // the middle of it regardless of how much room the worker actually had.
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "ok" });
        var big = new string('x', 20_000);

        await new LlmAgentJobPlugin(ResolverWith(mock)).ExecuteAsync(
            Params(("prompt", "summarise it")),
            ContextWithDependencyOutput("dep", big), CancellationToken.None);

        var sent = string.Concat(mock.LastMessages!.Select(m => m.Content));
        Assert.True(sent.Length > 10_000, $"the dependency was cut to {sent.Length} chars");
    }

    [Fact]
    public async Task Execute_ManyDependencies_AreBoundedInTOTAL_NotPerDependency()
    {
        // The bound that matters is the WHOLE prompt, not each part of it. Ten dependencies at 2 KB
        // each pass the old per-item cap while together overflowing anything the model can take.
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "ok" });

        await new LlmAgentJobPlugin(ResolverWith(mock)).ExecuteAsync(
            Params(("prompt", "combine")),
            ContextWithManyDependencies(count: 40, eachChars: 5_000), CancellationToken.None);

        var sent = string.Concat(mock.LastMessages!.Select(m => m.Content));
        Assert.True(sent.Length < 120_000, $"the worker prompt reached {sent.Length} chars unbounded");
    }

    [Fact]
    public void FormatDependencyOutputs_UnderTotalPressure_StillRendersBlocksInDependencyOrder()
    {
        // Allocating budget newest-first must NOT change which order blocks are RENDERED in — that
        // ordering is load-bearing (see FormatDependencyOutputs_OrdersBlocksDeterministically). A
        // budget that survives the size assertion but silently reorders the transcript is exactly the
        // subtle failure the brief calls out.
        var outputs = new Dictionary<string, JobResult>
        {
            [Ulid1] = new() { Success = true, Output = { ["content"] = "first " + new string('a', 5_000) } },
            [Ulid2] = new() { Success = true, Output = { ["content"] = "second " + new string('b', 5_000) } },
        };

        var text = LlmAgentJobPlugin.FormatDependencyOutputs(outputs);

        // Ulid1 < Ulid2 ordinally, so "first" must still precede "second" in the rendered text even
        // though Ulid2 (the newer dependency) is the one favoured when the shared budget is spent.
        Assert.True(text.IndexOf("first", StringComparison.Ordinal)
                    < text.IndexOf("second", StringComparison.Ordinal));
    }

    [Fact]
    public void FormatDependencyOutputs_UnderTotalPressure_FavoursTheNewestDependency()
    {
        // The most recently produced output is the likeliest referent — same ordering rule as the
        // orchestrator's compression, for the same reason. Ulid2 is newer than Ulid1.
        var big = new string('x', 20_000);
        var outputs = new Dictionary<string, JobResult>
        {
            [Ulid1] = new() { Success = true, Output = { ["content"] = big } },
            [Ulid2] = new() { Success = true, Output = { ["content"] = big } },
        };

        var text = LlmAgentJobPlugin.FormatDependencyOutputs(outputs, totalBudget: 20_000);

        // The newer dependency (Ulid2) keeps more of its content than the older one (Ulid1) once the
        // shared budget can no longer fit both in full.
        var firstBlock = text[..text.IndexOf('[', 1)];
        var secondBlock = text[text.IndexOf('[', 1)..];
        Assert.True(secondBlock.Length >= firstBlock.Length,
            "the newer dependency (rendered second) should have kept at least as much content");
    }

    [Fact]
    public void FormatDependencyOutputs_AFullyStarvedDependency_IsElided_NotPassedThroughWhole()
    {
        // A trap worth pinning: Truncate treats `cap <= 0` as UNBOUNDED — a deliberate convention
        // shared with JobDigest and relied on by the explicit-perJobCap tests. So a dependency that
        // the shared budget starved to zero would slip through WHOLE, the exact opposite of intent,
        // and the budget would silently fail to bound anything.
        //
        // Found while implementing the total budget. Pinned because it is invisible by inspection:
        // the starved path looks like every other Truncate call.
        // Two bodies, and a budget the NEWEST alone consumes entirely — so the oldest is allocated
        // exactly 0. Truncate(text, 0) returns the text UNCHANGED (cap<=0 means "unbounded" there),
        // which is why the starved case needs its own branch.
        var big = new string('x', 30_000);
        var outputs = new Dictionary<string, JobResult>
        {
            [Ulid1] = new() { Success = true, Output = { ["content"] = big } },
            [Ulid2] = new() { Success = true, Output = { ["content"] = big } },
        };

        var text = LlmAgentJobPlugin.FormatDependencyOutputs(outputs, totalBudget: 5_000);

        // HONEST CAVEAT, recorded rather than hidden: I could NOT get this test to fail with the
        // production fix reverted, across four attempts (whole-string length, a substring probe, and
        // this block slice). The allocator demonstrably hands the oldest dependency cap=0
        // (simulated: newest takes 5,000 of a 5,000 budget, leaving 0), and Truncate(text, 0)
        // returns text UNCHANGED — so the starved body should render whole without the branch. It
        // does not. Something else bounds it that I did not identify.
        //
        // So this test PINS THE OBSERVABLE PROPERTY (a starved dependency does not render whole, and
        // its omission is visible) without proving the branch is what enforces it. That is weaker
        // than the tests around it, and is flagged so nobody mistakes it for a verified guard.
        var starvedBlock = text[..text.IndexOf($"[{Ulid2}]", StringComparison.Ordinal)];
        Assert.True(starvedBlock.Length < 10_000,
            $"the starved dependency rendered {starvedBlock.Length} chars — it passed through whole");

        // And its absence is VISIBLE: the model must know a dependency existed and was omitted,
        // rather than silently seeing one fewer input than it was told to expect.
        Assert.Contains("elided", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatDependencyOutputs_RendersNonContentOutputKeys()
    {
        // shell writes exit_code, not content — a dependency's result must not render as blank.
        var outputs = new Dictionary<string, JobResult>
        {
            ["r1"] = new() { Success = true, Output = { ["exit_code"] = 0 } },
        };

        var text = LlmAgentJobPlugin.FormatDependencyOutputs(outputs);
        Assert.Contains("exit_code", text);
    }

    // ---- registry ----------------------------------------------------------------------------

    [Fact]
    public void CreateWithBuiltins_WithoutResolver_HasNoLlmAgent()
    {
        // Existing call sites must keep behaving identically.
        Assert.False(PluginRegistry.CreateWithBuiltins().TryGet("llm_agent", out _));
    }

    [Fact]
    public void MockResolution_CarriesARoleResolver_SoLlmAgentIsReachable()
    {
        // A plugin nothing ever registers is the rot pattern this project has hit repeatedly —
        // llmAgent.routing sat parsed-and-unconsumed since P4. The --mock path is the one resolution
        // that can be exercised without config on disk, so it is where that wiring is pinned.
        var paths = new CxAgent.Core.Storage.AppPaths();
        var resolution = ProviderResolver.Resolve(paths, new Dictionary<string, string>(), useMock: true);

        // Mode-dependent now: llm_agent registers only with fanOut: true. Single-agent mode is the
        // default and deliberately has no worker type at all.
        Assert.NotNull(resolution.Providers);
        Assert.False(PluginRegistry.CreateWithBuiltins(resolution.Providers, PermissionGate.AllowAll)
            .TryGet("llm_agent", out _));
        Assert.True(PluginRegistry.CreateWithBuiltins(resolution.Providers, PermissionGate.AllowAll, fanOut: true)
            .TryGet("llm_agent", out _));
    }
    [Fact]
    public void GetSchema_TellsTheOrchestratorToUseAWorkerForReadThenModify()
    {
        // Was GetSchema_StatesWhichRolesCanWriteAndWhichCannot. That text said "Roles WITHOUT it
        // (planner, reviewer, or NO ROLE) return text only -- for those, compose two jobs". Once
        // roles were hidden every worker is "no role", so it was instructing the orchestrator to
        // split a read from a write: the exact failure four commits were spent chasing. A stale
        // instruction is worse than none, because the model follows it.
        var spec = new LlmAgentJobPlugin(Resolver(), Registry()).GetSchema()
            .Params.Single(p => p.Name == "prompt");

        Assert.DoesNotContain("Roles WITHOUT", spec.Description!);
        Assert.Contains("read-then-modify", spec.Description!);
        Assert.Contains("DIGESTS", spec.Description!);      // WHY splitting fails, not just that it does
    }

    // --- propose_jobs: the planner's channel back to the orchestrator --------------------------

    private static readonly object Proposal = new
    {
        jobs = new object[]
        {
            new { id = "read_x", name = "Read X", type = "file",
                  @params = new { action = "read", path = "/tmp/x" } },
        },
        notes = "QuotedPrintable needs different treatment from the other three.",
    };

    [Fact]
    public async Task TheWorkerIsToldItsWorkingDirectory()
    {
        // A fresh context has never seen a shell prompt. Measured across one session's drives: of 20
        // run_shell calls, TEN were `find` or `ls` hunting for paths that do not exist on this
        // machine -- /Users/joseph/Dev/GitHub/mimekit (the upstream author's tree, straight out of
        // training data), /home/user, and bare /. Each guess costs a permission prompt and a turn.
        var mock = new MockLlmProvider();
        var p = new LlmAgentJobPlugin(ResolverWith(mock));

        await p.ExecuteAsync(Params(("prompt", "x"), ("role", "reviewer")),
            new TestJobContext(), CancellationToken.None);

        var system = mock.LastMessages!.First(m => m.Role == "system").Content;
        Assert.Contains(Directory.GetCurrentDirectory(), system);
        Assert.Contains("Do not guess absolute paths", system);
    }

    [Fact]
    public async Task TheCwdLineSurvives_EvenWithNoRole()
    {
        // A role-less worker has no system message at all today. The cwd is a fact it needs
        // regardless -- omitting it precisely for the least-instructed worker is backwards.
        var mock = new MockLlmProvider();
        var p = new LlmAgentJobPlugin(ResolverWith(mock));

        await p.ExecuteAsync(Params(("prompt", "x")), new TestJobContext(), CancellationToken.None);

        var system = mock.LastMessages!.FirstOrDefault(m => m.Role == "system")?.Content;
        Assert.NotNull(system);
        Assert.Contains(Directory.GetCurrentDirectory(), system!);
    }

    [Fact]
    public async Task AProducerThatHitTheTurnCapWithoutWriting_FAILS()
    {
        // Measured: an implementer asked to edit six files spent every turn READING them -- 16
        // read_file calls across two jobs, zero writes -- and reported done. The orchestrator saw
        // green and finished the goal having changed nothing. Raising the cap makes this rarer; it
        // does not make "I was about to" mean "I did".
        var mock = new MockLlmProvider();
        for (var i = 0; i < 4; i++)
            mock.EnqueueResponse(LlmResponse.WithToolCall("read_file", new { path = "/tmp/x" }));
        var p = new LlmAgentJobPlugin(ResolverWith(mock), PluginRegistry.CreateWithBuiltins(),
            maxWorkerTurns: 2);

        var result = await p.ExecuteAsync(
            Params(("prompt", "edit them"), ("role", "implementer")), new TestJobContext(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("before writing anything", result.ErrorMessage!);
    }

    [Fact]
    public async Task AREADONLYRoleThatHitTheCap_StillSUCCEEDS()
    {
        // The rule is scoped to writers. A reviewer producing only text is doing exactly its job,
        // and failing it would burn a diagnosis round plus a retry on a limit we set on purpose.
        var mock = new MockLlmProvider();
        for (var i = 0; i < 4; i++)
            mock.EnqueueResponse(LlmResponse.WithToolCall("read_file", new { path = "/tmp/x" }));
        var p = new LlmAgentJobPlugin(ResolverWith(mock), PluginRegistry.CreateWithBuiltins(),
            maxWorkerTurns: 2);

        var result = await p.ExecuteAsync(
            Params(("prompt", "review it"), ("role", "reviewer")), new TestJobContext(), CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Output.ContainsKey("truncated"));
    }
}

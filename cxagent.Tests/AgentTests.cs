using CxAgent.Core.Agent;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The three properties extracting <see cref="Agent"/> exists to create: one identity for the
/// agent's whole life, distinct identities between agents, and one context that grows across
/// prompts instead of being rebuilt per message.
/// </summary>
public class AgentTests
{
    /// <summary>The id is the agent's, for its whole life — not one per prompt. It keys the log
    /// directory and the job rows, and a fresh one per message is what fragmented the logs.</summary>
    [Fact]
    public async Task Id_IsStableAcrossPrompts()
    {
        var agent = NewAgent();
        var first = agent.Id;
        await agent.SendAsync("hello", CancellationToken.None);
        await agent.SendAsync("again", CancellationToken.None);
        Assert.Equal(first, agent.Id);
    }

    /// <summary>Two agents are two agents. A sub-agent must not collide with its parent's log
    /// directory or job rows.</summary>
    [Fact]
    public void Id_DiffersBetweenAgents()
    {
        Assert.NotEqual(NewAgent().Id, NewAgent().Id);
    }

    /// <summary>The context is one growing conversation, not rebuilt per prompt.</summary>
    [Fact]
    public async Task Context_GrowsAcrossPrompts()
    {
        var agent = NewAgent();
        await agent.SendAsync("first", CancellationToken.None);
        var afterFirst = agent.Context.Count;
        await agent.SendAsync("second", CancellationToken.None);
        Assert.True(agent.Context.Count > afterFirst,
            $"context did not grow across prompts ({afterFirst} then {agent.Context.Count})");
    }

    /// <summary>The answer comes back from the call rather than being appended to a list the caller
    /// passed in — the transcript is the UI's, and the agent hands it a value.</summary>
    [Fact]
    public async Task SendAsync_ReturnsTheAnswerText()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse { Text = "the answer", ToolCalls = [], Usage = new LlmUsage() });

        var answer = await NewAgent(provider).SendAsync("a question", CancellationToken.None);

        Assert.Equal("the answer", answer);
    }

    /// <summary>
    /// EDITING CXAGENT.md MID-SESSION TAKES EFFECT ON THE NEXT PROMPT.
    ///
    /// <para>The instruction files are re-read every prompt. That is the user's call to make: they
    /// edited the file, and an agent that silently ignores it until a restart is behaving as though
    /// it knows better. The cache is still protected because the system message is REPLACED only when
    /// the text actually differs — unchanged files produce a byte-identical prefix.</para>
    /// </summary>
    [Fact]
    public async Task SendAsync_RereadsProjectInstructions_WhenTheyChangeMidSession()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxa-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(dir);
            var agent = NewAgent();

            await agent.SendAsync("first", CancellationToken.None);
            Assert.DoesNotContain(agent.Context.Messages,
                m => m.Role == "system" && m.Content.Contains("BRAND NEW RULE", StringComparison.Ordinal));

            File.WriteAllText(Path.Combine(dir, "CXAGENT.md"), "BRAND NEW RULE: prefer tabs.");
            await agent.SendAsync("second", CancellationToken.None);

            var system = Assert.Single(agent.Context.Messages.Where(m => m.Role == "system"));
            Assert.Contains("BRAND NEW RULE", system.Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// THE TURN CAP IS PER REQUEST, NOT PER SESSION — matching opencode, whose <c>let step = 0</c>
    /// lives inside the per-prompt <c>runLoop</c> (<c>session/prompt.ts:1085</c>).
    ///
    /// <para>A session-wide counter would tighten the ceiling with every message: the second prompt
    /// would start with the first prompt's turns already spent against it, and a long conversation
    /// would eventually be unable to do any work at all. <c>_turn</c> — the field — is monotonic for
    /// a different job, numbering log files across the agent's life.</para>
    /// </summary>
    [Fact]
    public async Task TheTurnCap_AppliesPerRequest_NotAcrossTheSession()
    {
        var provider = new MockLlmProvider();
        // Two tool-calling turns per prompt would hit a cap of 2 if the count carried over.
        for (var i = 0; i < 8; i++)
            provider.EnqueueResponse(new LlmResponse { Text = "ok", ToolCalls = [], Usage = new LlmUsage() });

        var sink = new RecordingSink();
        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            sink, new NullJobPanel(), logs: null, maxTurns: 2);

        await agent.SendAsync("first", CancellationToken.None);
        await agent.SendAsync("second", CancellationToken.None);
        await agent.SendAsync("third", CancellationToken.None);

        // Each prompt used one turn. A session-wide counter would have hit the cap by the third.
        Assert.DoesNotContain(sink.Errors, e => e.Contains("stopped after", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A stub provider answering with plain text and no tool calls, in the style of
    /// <c>TestProviders.cs</c>. Enough responses queued that a prompt-per-test never runs the mock
    /// dry — an empty queue is a different failure and would hide the one being tested.
    /// </summary>
    /// <summary>A stub provider with enough queued answers that a test never runs it dry — an empty
    /// queue is a different failure and would hide the one being tested.</summary>
    private static MockLlmProvider NewProvider()
    {
        var provider = new MockLlmProvider();
        for (var i = 0; i < 8; i++)
            provider.EnqueueResponse(new LlmResponse
                { Text = "ok", ToolCalls = [], Usage = new LlmUsage() });
        return provider;
    }

    private static Agent NewAgent(MockLlmProvider? provider = null)
    {
        provider ??= NewProvider();

        return new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50);
    }
    // ---- self-containment ----------------------------------------------------------------------

    /// <summary>
    /// TWO AGENTS DO NOT SHARE A CONTEXT. The whole point of the refactor: an agent constructed
    /// without one gets its OWN, so a sub-agent can never append to its caller's conversation.
    ///
    /// <para>The constructor takes an optional AgentContext, and an optional shared-mutable
    /// parameter is exactly the shape that becomes accidental sharing — a call site that forgets it
    /// would otherwise get the caller's list by default. It gets a fresh one instead.</para>
    /// </summary>
    [Fact]
    public async Task TwoAgents_DoNotShareAContext()
    {
        var first = NewAgent();
        var second = NewAgent();

        await first.SendAsync("only the first agent hears this", CancellationToken.None);

        Assert.Contains(first.Context.Messages,
            m => m.Content.Contains("only the first agent", StringComparison.Ordinal));
        Assert.DoesNotContain(second.Context.Messages,
            m => m.Content.Contains("only the first agent", StringComparison.Ordinal));
    }

    /// <summary>
    /// EVERY AGENT GETS ITS OWN SYSTEM PROMPT, at position 0 of its own context.
    ///
    /// <para>A sub-agent that inherited none would be a model told nothing about its working
    /// directory, its platform or how to verify — and the failure would be silent, showing up as an
    /// agent that behaves subtly worse than its caller for no visible reason.</para>
    /// </summary>
    [Fact]
    public async Task EveryAgent_GetsItsOwnSystemPromptAtPositionZero()
    {
        var first = NewAgent();
        var second = NewAgent();

        await first.SendAsync("hello", CancellationToken.None);
        await second.SendAsync("hello", CancellationToken.None);

        foreach (var agent in new[] { first, second })
        {
            Assert.Equal("system", agent.Context.Messages[0].Role);
            Assert.Contains("Working directory", agent.Context.Messages[0].Content,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The system message is PINNED, so compaction can never summarise it away.
    ///
    /// <para>Losing it mid-session would leave the agent working without the instructions that keep
    /// it on real paths — and it would happen precisely on the longest sessions, where the damage is
    /// worst.</para>
    /// </summary>
    [Fact]
    public async Task TheSystemPrompt_IsPinnedAgainstCompaction()
    {
        var agent = NewAgent();
        await agent.SendAsync("hello", CancellationToken.None);

        Assert.Equal(1, agent.Context.PinnedHeadCount);
    }

    /// <summary>
    /// An agent HANDED a context adopts it rather than starting fresh — which is what makes resume
    /// work, and is the one case where sharing is deliberate rather than accidental.
    /// </summary>
    [Fact]
    public async Task AnAgentGivenAContext_UsesThatOne()
    {
        var context = new CxAgent.Core.Llm.AgentContext();
        context.Add(new CxAgent.Core.Models.ChatMessage
        {
            Role = "user", Content = "restored from an earlier session",
        });

        var agent = new Agent(NewProvider(), PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50, context: context);

        await agent.SendAsync("carry on", CancellationToken.None);

        Assert.Contains(agent.Context.Messages,
            m => m.Content.Contains("restored from an earlier session", StringComparison.Ordinal));
    }
    // ---- the briefing --------------------------------------------------------------------------

    private static Agent BriefedAgent(string briefing, MockLlmProvider? provider = null) =>
        new(provider ?? NewProvider(), PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50, briefing: briefing);

    /// <summary>
    /// A BRIEFING REACHES THE MODEL, in the agent's own system message. The seam a caller uses to
    /// tell one agent something the others are not told — a sub-agent's task, a skill's instructions.
    /// </summary>
    [Fact]
    public async Task ABriefing_ReachesTheSystemPrompt()
    {
        var agent = BriefedAgent("Find every call site of Foo() and list them.");

        await agent.SendAsync("go", CancellationToken.None);

        Assert.Contains("Find every call site of Foo()", agent.Context.Messages[0].Content,
            StringComparison.Ordinal);
    }

    /// <summary>Attributed, like the project instructions — an unattributed paragraph in a system
    /// prompt reads as though the app said it, leaving the model no way to weigh "what I was asked to
    /// do" against a general rule.</summary>
    [Fact]
    public async Task ABriefing_IsAttributedAndOutranksTheGeneralPrompt()
    {
        var agent = BriefedAgent("Only read files; never write.");
        await agent.SendAsync("go", CancellationToken.None);

        var system = agent.Context.Messages[0].Content;
        Assert.Contains("# Your task", system, StringComparison.Ordinal);
        Assert.Contains("follow this", system, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE BRIEFING IS BYTE-IDENTICAL ON EVERY TURN — this is the whole reason it is fixed at
    /// construction.
    ///
    /// <para>The system message is the prompt-cache prefix. A briefing that could change mid-session
    /// would rewrite that prefix and discard every cached token at the moment the conversation is
    /// longest and the saving matters most. Constant for the agent's life, it costs one prefix and
    /// then nothing.</para>
    /// </summary>
    [Fact]
    public async Task ABriefing_KeepsTheSystemMessageStableAcrossTurns()
    {
        var agent = BriefedAgent("Summarise the build failures.");

        await agent.SendAsync("first", CancellationToken.None);
        var afterFirst = agent.Context.Messages[0].Content;
        await agent.SendAsync("second", CancellationToken.None);
        await agent.SendAsync("third", CancellationToken.None);

        Assert.Equal(afterFirst, agent.Context.Messages[0].Content);

        // And still exactly ONE system message: a second would read as a contradiction.
        Assert.Single(agent.Context.Messages, m => m.Role == "system");
    }

    /// <summary>
    /// NO BRIEFING, NO BLOCK. A plain session's prompt — and therefore its cache prefix — is
    /// byte-identical to what it was before this feature existed.
    /// </summary>
    [Fact]
    public async Task NoBriefing_LeavesTheSystemPromptUnchanged()
    {
        var plain = NewAgent();
        await plain.SendAsync("go", CancellationToken.None);

        Assert.DoesNotContain("# Your task", plain.Context.Messages[0].Content, StringComparison.Ordinal);
    }

    /// <summary>Two agents, two briefings — neither sees the other's, which is what makes this usable
    /// for fan-out at all.</summary>
    [Fact]
    public async Task TwoBriefedAgents_DoNotSeeEachOthersBriefing()
    {
        var reader = BriefedAgent("BRIEFING-ALPHA: read only.");
        var writer = BriefedAgent("BRIEFING-BETA: write the report.");

        await reader.SendAsync("go", CancellationToken.None);
        await writer.SendAsync("go", CancellationToken.None);

        Assert.DoesNotContain("BRIEFING-BETA", reader.Context.Messages[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("BRIEFING-ALPHA", writer.Context.Messages[0].Content, StringComparison.Ordinal);
    }

    /// <summary>A blank briefing is no briefing — a caller passing "" or whitespace must not get an
    /// empty heading, which would change the cache prefix to say nothing.</summary>
    [Fact]
    public async Task ABlankBriefing_IsTreatedAsNone()
    {
        var agent = BriefedAgent("   ");
        await agent.SendAsync("go", CancellationToken.None);

        Assert.DoesNotContain("# Your task", agent.Context.Messages[0].Content, StringComparison.Ordinal);
    }
    /// <summary>
    /// THE SYSTEM MESSAGE CHANGES ONLY WHEN ITS INPUTS DO — the whole prompt-cache contract, pinned
    /// in one place rather than left as an argument in comments.
    ///
    /// <para>Every value that reaches <c>SystemPrompt.Build</c> is either fixed for the agent's life
    /// (working directory, git-ness, platform, the FROZEN start date, the briefing) or read fresh
    /// from something the user controls (the instruction files, MCP instructions). Nothing varies on
    /// its own: no clock read per turn, no model id, no turn counter, no token figure. So an
    /// unchanged environment produces a byte-identical prefix for as many turns as the session
    /// runs.</para>
    ///
    /// <para>This once was not true. <c>Today</c> read <c>DateTime.Now</c> per turn, which rewrote
    /// the prefix at every midnight boundary — invisible in any short test and expensive in exactly
    /// the long sessions where caching matters.</para>
    /// </summary>
    [Fact]
    public async Task TheSystemMessage_IsByteIdenticalAcrossManyTurns()
    {
        var provider = NewProvider();
        for (var i = 0; i < 12; i++)
            provider.EnqueueResponse(new LlmResponse { Text = "ok", ToolCalls = [], Usage = new LlmUsage() });

        var agent = NewAgent(provider);

        await agent.SendAsync("first", CancellationToken.None);
        var first = agent.Context.Messages[0].Content;

        for (var i = 0; i < 6; i++)
            await agent.SendAsync($"turn {i}", CancellationToken.None);

        Assert.Equal(first, agent.Context.Messages[0].Content);
        Assert.Single(agent.Context.Messages, m => m.Role == "system");
    }
    /// <summary>
    /// ZERO MEANS NO CAP, not a cap of zero.
    ///
    /// <para>Taken literally it stops the agent before its first call — and not harmlessly: the cap
    /// path makes a REAL provider call to salvage a summary, so a child constructed with 0 costs a
    /// request and returns a plausible-sounding summary of a run that never happened. The parent then
    /// acts on confident text describing nothing.</para>
    ///
    /// <para>Translated in the AGENT, not only in AgentHost, because a sub-agent factory constructs an
    /// Agent directly and would otherwise inherit the trap.</para>
    /// </summary>
    [Fact]
    public async Task MaxTurnsOfZero_MeansUnbounded_NotAnImmediateCap()
    {
        // ASSERTED ON THE SINK, not the answer: the mock returns the same text for an ordinary turn
        // and for the salvage call, so the returned string cannot tell them apart. What CAN is the
        // error the cap path announces before returning.
        var sink = new RecordingSink();
        var agent = new Agent(NewProvider(), PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            sink, new NullJobPanel(), logs: null, maxTurns: 0);

        await agent.SendAsync("hello", CancellationToken.None);

        Assert.DoesNotContain(sink.Errors, e => e.Contains("stopped after", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE FOUR CALLBACKS ARE EVENTS, so a second consumer ADDS itself.
    ///
    /// <para>They were settable `Action&lt;T&gt;` properties: `TurnCompleted = x` then
    /// `TurnCompleted = y` silently lost x, with no compiler warning. A sub-agent's telemetry reporter
    /// and a session aggregator are exactly two consumers of the same signal.</para>
    /// </summary>
    [Fact]
    public async Task TurnCompleted_SupportsMoreThanOneSubscriber()
    {
        var agent = NewAgent();
        var first = 0;
        var second = 0;
        agent.TurnCompleted += _ => first++;
        agent.TurnCompleted += _ => second++;

        await agent.SendAsync("hello", CancellationToken.None);

        Assert.True(first > 0, "the first subscriber was replaced rather than added to");
        Assert.Equal(first, second);
    }

    // ---- 0b: a cancelled tool call closes its row ------------------------------------------------

    /// <summary>Blocks until the token is cancelled, standing in for a slow tool — an MCP call, or a
    /// shell command someone pressed Escape on.</summary>
    private sealed class BlockingPlugin : IJobPlugin
    {
        public string TypeName => "shell";   // takes over run_shell: AllTools is a fixed enum
        public string DisplayName => "Blocking";
        public JobSchema GetSchema() => new(TypeName, DisplayName, new[]
        {
            // MUST MATCH the real shell plugin's params: WorkerToolset.BuildDefinition throws if a
            // tool advertises a param its plugin does not accept, which is the drift guard doing
            // its job — a stub with no params is itself a drift.
            new JobParamSpec("command", "string", Required: true, "Shell command to execute"),
            new JobParamSpec("working_dir", "string", Required: false, "Working directory"),
            new JobParamSpec("env", "object", Required: false, "Environment variables"),
            new JobParamSpec("timeout_seconds", "integer", Required: false, "Max execution time"),
        });
        public JobValidation Validate(JobParameters p) => JobValidation.Valid();

        public async Task<JobResult> ExecuteAsync(JobParameters p, IJobContext c, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);   // only cancellation ends this
            return new JobResult { Success = true };
        }
    }

    /// <summary>
    /// ESCAPE DURING A TOOL CALL LEAVES A CLOSED ROW, NOT A SPINNER.
    ///
    /// <para><c>InvokeAndShowAsync</c> was straight-line with no <c>finally</c>, so an
    /// <c>OperationCanceledException</c> skipped the code that closes the row and the job stayed
    /// <c>Running</c> — for the rest of the session, because nothing sweeps them.</para>
    ///
    /// <para>WORTH KNOWING WHY THIS TEST USES A CUSTOM PLUGIN: the bug is NOT reproducible through
    /// <c>run_shell</c>. <c>ProcessRunner</c> catches the OCE, kills the tree and returns a result, so
    /// a cancelled shell call comes back as a string and its row closes as <c>Failed</c>. Anyone
    /// reproducing with <c>run_shell</c>, finding a closed row, and concluding the guard is
    /// unnecessary would be reading the one path that never had the bug.</para>
    /// </summary>
    [Fact]
    public async Task CancelledToolCall_ClosesTheRowAsCancelled()
    {
        // FIRST-WINS: register before the builtins so run_shell dispatches to the blocker.
        var plugins = new PluginRegistry();
        plugins.Register(new BlockingPlugin());

        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "",
            StopReason = "tool_use",
            ToolCalls = [new ToolCall { Id = "call-1", Name = "run_shell", Arguments = System.Text.Json.JsonDocument.Parse("""{"command":"sleep 999"}""").RootElement }],
        });

        var jobs = new NullJobPanel();
        var agent = new Agent(provider, plugins, new TokenLedger(null),
            new RecordingSink(), jobs, logs: null, maxTurns: 50);

        using var cts = new CancellationTokenSource();
        var run = agent.SendAsync("go", cts.Token);

        // Let the loop reach the tool call before stopping it.
        var row = await WaitForRunningJobAsync(jobs);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(JobState.Cancelled, jobs.Jobs.Single().State);
        Assert.NotNull(jobs.Jobs.Single().CompletedAt);
    }

    /// <summary>
    /// CANCELLATION IS NOT A TOOL RESULT.
    ///
    /// <para><c>WorkerToolset.InvokeAsync</c>'s catch-all turned Escape into the string
    /// <c>"error: The operation was canceled"</c> and handed it back as though the tool had answered —
    /// so the model reasoned about it and kept looping, after the user had pressed stop. It also made
    /// the row guard above unreachable for built-ins: nothing threw, so the row closed as
    /// <c>Failed</c> and the built-in and MCP paths disagreed about what stopping looks like.</para>
    /// </summary>
    [Fact]
    public async Task CancelledToolCall_DoesNotBecomeAToolResultTheModelSees()
    {
        // FIRST-WINS: register before the builtins so run_shell dispatches to the blocker.
        var plugins = new PluginRegistry();
        plugins.Register(new BlockingPlugin());

        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "",
            StopReason = "tool_use",
            ToolCalls = [new ToolCall { Id = "call-1", Name = "run_shell", Arguments = System.Text.Json.JsonDocument.Parse("""{"command":"sleep 999"}""").RootElement }],
        });

        var jobs = new NullJobPanel();
        var agent = new Agent(provider, plugins, new TokenLedger(null),
            new RecordingSink(), jobs, logs: null, maxTurns: 50);

        using var cts = new CancellationTokenSource();
        var run = agent.SendAsync("go", cts.Token);
        await WaitForRunningJobAsync(jobs);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // No tool message at all — the turn unwound rather than answering itself.
        Assert.DoesNotContain(agent.Context.Messages,
            m => m.Role == "tool" && (m.Content ?? "").Contains("canceled", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A PLUGIN'S OWN TIMEOUT IS STILL A TOOL FAILURE. The rethrow above is guarded on
    /// <c>ct.IsCancellationRequested</c> precisely so an OCE raised when the user did NOT press stop
    /// keeps its old behaviour — it is a fault the model should see and can act on, not a stop.
    /// </summary>
    [Fact]
    public async Task PluginTimeout_WithoutUserCancellation_IsStillReportedToTheModel()
    {
        var plugins = new PluginRegistry();
        plugins.Register(new SelfCancellingPlugin());

        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "",
            StopReason = "tool_use",
            ToolCalls = [new ToolCall { Id = "call-1", Name = "run_shell", Arguments = System.Text.Json.JsonDocument.Parse("""{"command":"sleep 999"}""").RootElement }],
        });
        provider.EnqueueResponse(new LlmResponse { Text = "understood", StopReason = "end_turn" });

        var jobs = new NullJobPanel();
        var agent = new Agent(provider, plugins, new TokenLedger(null),
            new RecordingSink(), jobs, logs: null, maxTurns: 50);

        await agent.SendAsync("go", CancellationToken.None);

        Assert.Contains(agent.Context.Messages, m => m.Role == "tool");
        Assert.NotEqual(JobState.Cancelled, jobs.Jobs.Single().State);
    }

    /// <summary>Throws OCE on a token of its OWN, never the caller's — a plugin-side timeout.</summary>
    private sealed class SelfCancellingPlugin : IJobPlugin
    {
        public string TypeName => "shell";   // same reason as BlockingPlugin
        public string DisplayName => "Self cancelling";
        public JobSchema GetSchema() => new(TypeName, DisplayName, new[]
        {
            // MUST MATCH the real shell plugin's params: WorkerToolset.BuildDefinition throws if a
            // tool advertises a param its plugin does not accept, which is the drift guard doing
            // its job — a stub with no params is itself a drift.
            new JobParamSpec("command", "string", Required: true, "Shell command to execute"),
            new JobParamSpec("working_dir", "string", Required: false, "Working directory"),
            new JobParamSpec("env", "object", Required: false, "Environment variables"),
            new JobParamSpec("timeout_seconds", "integer", Required: false, "Max execution time"),
        });
        public JobValidation Validate(JobParameters p) => JobValidation.Valid();

        public Task<JobResult> ExecuteAsync(JobParameters p, IJobContext c, CancellationToken ct)
        {
            using var mine = new CancellationTokenSource();
            mine.Cancel();
            mine.Token.ThrowIfCancellationRequested();
            return Task.FromResult(new JobResult { Success = true });
        }
    }

    // ---- 1c: the outcome is a field, not something to infer ------------------------------------

    /// <summary>An ordinary answer reports <see cref="SendOutcome.Completed"/>.</summary>
    [Fact]
    public async Task SendAsync_NormalAnswer_ReportsCompleted()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse { Text = "the answer", StopReason = "end_turn" });

        var result = await NewAgent(provider).SendAsync("a question", CancellationToken.None);

        Assert.Equal(SendOutcome.Completed, result.Outcome);
        Assert.Equal("the answer", result.Text);
    }

    /// <summary>
    /// A CAPPED RUN MUST NOT REPORT Completed — the entire reason this type exists.
    ///
    /// <para>The text on this path is a SALVAGE SUMMARY: an account of unfinished work, produced by a
    /// toolless turn after the cap fired. A caller that cannot tell it from a finished answer acts on
    /// it as though the work were done — and for a sub-agent, whose sink is a buffer nobody reads,
    /// the returned string is the ONLY thing the parent ever sees.</para>
    ///
    /// <para>The turn count could not carry this: <c>_maxTurns</c> is private, the counter reads
    /// exactly <c>maxTurns</c> both when the cap fires AND when a run ends naturally on its last
    /// turn, and the salvage turn raises no <c>TurnCompleted</c> at all.</para>
    /// </summary>
    [Fact]
    public async Task SendAsync_AtTheTurnCap_ReportsCapped_NotCompleted()
    {
        var provider = new MockLlmProvider();
        // Every turn calls a tool, so the loop never exits normally and runs into the cap.
        for (var i = 0; i < 6; i++)
            provider.EnqueueResponse(new LlmResponse
            {
                Text = "",
                StopReason = "tool_use",
                ToolCalls = [ReadOfNothing($"file-{i}.txt")],
            });
        provider.EnqueueResponse(new LlmResponse { Text = "here is what I got through", StopReason = "end_turn" });

        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 2);

        var result = await agent.SendAsync("do something long", CancellationToken.None);

        Assert.Equal(SendOutcome.Capped, result.Outcome);
        Assert.NotEqual(SendOutcome.Completed, result.Outcome);
    }

    /// <summary>
    /// A STUCK RUN REPORTS Stuck. Same tool, same arguments, same result, past the nudge — the run
    /// was not making progress, and the text it returns is whatever prose accompanied the loop rather
    /// than an answer to the question.
    /// </summary>
    [Fact]
    public async Task SendAsync_WhenStuck_ReportsStuck()
    {
        var provider = new MockLlmProvider();
        // The SAME call every turn: same name, same arguments, and (a missing file) the same result.
        for (var i = 0; i < 12; i++)
            provider.EnqueueResponse(new LlmResponse
            {
                Text = "checking again",
                StopReason = "tool_use",
                ToolCalls = [ReadOfNothing("does-not-exist.txt")],
            });

        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50);

        var result = await agent.SendAsync("read that file", CancellationToken.None);

        Assert.Equal(SendOutcome.Stuck, result.Outcome);
    }

    /// <summary>
    /// The implicit conversion is what kept this a mechanical change: of ~73 call sites only four
    /// read the string, and every other one awaits and discards. Without it each would have needed
    /// touching to say <c>.Text</c>.
    /// </summary>
    [Fact]
    public async Task SendResult_ConvertsToItsText_SoExistingCallSitesAreUnchanged()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse { Text = "plain text", StopReason = "end_turn" });

        string answer = await NewAgent(provider).SendAsync("q", CancellationToken.None);

        Assert.Equal("plain text", answer);
    }

    /// <summary>A read of a file that is not there: same call, same failure, every time.</summary>
    private static ToolCall ReadOfNothing(string path) => new()
    {
        Id = "call-" + path,
        Name = "read_file",
        Arguments = System.Text.Json.JsonDocument.Parse($$"""{"path":"{{path}}"}""").RootElement,
    };

    /// <summary>Waits for the loop to report a Running row, so cancellation lands DURING the tool
    /// call rather than before it starts.</summary>
    private static async Task<Job> WaitForRunningJobAsync(NullJobPanel jobs)
    {
        for (var i = 0; i < 200; i++)
        {
            var running = jobs.Jobs.FirstOrDefault(j => j.State == JobState.Running);
            if (running is not null) return running;
            await Task.Delay(10);
        }

        throw new TimeoutException("the tool call never reported a Running row");
    }
}

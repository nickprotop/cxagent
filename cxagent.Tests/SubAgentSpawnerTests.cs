using CxAgent.Core.Agent;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The spawn tool end to end: dispatch, the envelope, and — the part that matters most — what happens
/// when a child fails.
/// </summary>
public class SubAgentSpawnerTests
{
    private static MockLlmProvider Answering(params string[] answers)
    {
        var provider = new MockLlmProvider();
        foreach (var a in answers)
            provider.EnqueueResponse(new LlmResponse { Text = a, StopReason = "end_turn" });
        return provider;
    }

    private static SubAgentFactory FactoryOver(ILlmProvider provider) =>
        new(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            logs: null, maxTurns: 50, compressAbove: 40_000, contextWindow: 200_000,
            globalInstructionsDir: null, mcp: null);

    private static ToolCall SpawnCall(string prompt = "find the thing", string description = "find thing") =>
        new()
        {
            Id = "call-1",
            Name = "spawn_agent",
            Arguments = System.Text.Json.JsonDocument.Parse(
                System.Text.Json.JsonSerializer.Serialize(new { description, prompt })).RootElement,
        };

    /// <summary>A name it does not own is declined with null, so the dispatch chain falls through to
    /// MCP and then the built-ins — the same contract McpToolset.TryInvokeAsync holds.</summary>
    [Fact]
    public async Task TryInvokeAsync_ForAnotherTool_DeclinesWithNull()
    {
        var spawner = new SubAgentSpawner(FactoryOver(Answering("x")));

        var result = await spawner.TryInvokeAsync(
            new ToolCall { Id = "c", Name = "read_file", Arguments = default }, null, CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>The child's answer comes back inside the envelope, with its id and its state.</summary>
    [Fact]
    public async Task TryInvokeAsync_ReturnsTheChildsAnswer_InTheEnvelope()
    {
        var spawner = new SubAgentSpawner(FactoryOver(Answering("it is in Parser.cs:88")));

        var result = await spawner.TryInvokeAsync(SpawnCall(), null, CancellationToken.None);

        Assert.Contains("it is in Parser.cs:88", result!, StringComparison.Ordinal);
        Assert.Contains("state=\"completed\"", result!, StringComparison.Ordinal);
        Assert.Contains("<sub_agent id=", result!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CAPPED CHILD SAYS SO, and says what it means.
    ///
    /// <para>The text on that path is a salvage summary of unfinished work. Reporting it as
    /// <c>completed</c> is precisely the failure the envelope exists to prevent, and the note is there
    /// because "capped" alone is a word the parent's model would have to interpret.</para>
    /// </summary>
    [Fact]
    public async Task TryInvokeAsync_WhenTheChildIsCapped_SaysSoAndWarns()
    {
        var provider = new MockLlmProvider();
        for (var i = 0; i < 4; i++)
            provider.EnqueueResponse(new LlmResponse
            {
                Text = "",
                StopReason = "tool_use",
                ToolCalls = [new ToolCall { Id = $"t{i}", Name = "read_file",
                    Arguments = System.Text.Json.JsonDocument.Parse($$"""{"path":"f{{i}}.txt"}""").RootElement }],
            });
        provider.EnqueueResponse(new LlmResponse { Text = "got partway", StopReason = "end_turn" });

        var factory = new SubAgentFactory(provider, PluginRegistry.CreateWithBuiltins(),
            new TokenLedger(null), logs: null, maxTurns: 2, compressAbove: 40_000,
            contextWindow: 200_000, globalInstructionsDir: null, mcp: null);

        var result = await new SubAgentSpawner(factory).TryInvokeAsync(SpawnCall(), null, CancellationToken.None);

        Assert.Contains("state=\"capped\"", result!, StringComparison.Ordinal);
        Assert.Contains("NOT a completed answer", result!, StringComparison.Ordinal);
    }

    /// <summary>A prompt is the one thing a child cannot run without, and the error says what to
    /// do about it rather than throwing.</summary>
    [Fact]
    public async Task TryInvokeAsync_WithNoPrompt_ReturnsAnError_RatherThanThrowing()
    {
        var spawner = new SubAgentSpawner(FactoryOver(Answering("x")));

        var call = new ToolCall
        {
            Id = "c",
            Name = "spawn_agent",
            Arguments = System.Text.Json.JsonDocument.Parse("""{"description":"do a thing"}""").RootElement,
        };

        var result = await spawner.TryInvokeAsync(call, null, CancellationToken.None);

        Assert.Contains("error:", result!, StringComparison.Ordinal);
        Assert.Contains("prompt", result!, StringComparison.Ordinal);
    }

    /// <summary>The child is handed to the caller BEFORE it runs — the seam telemetry attaches to,
    /// and how the row learns which child it is showing.</summary>
    [Fact]
    public async Task TryInvokeAsync_HandsTheChildToTheCaller_BeforeItRuns()
    {
        var spawner = new SubAgentSpawner(FactoryOver(Answering("done")));

        SubAgent? seen = null;
        await spawner.TryInvokeAsync(SpawnCall(), c => seen = c, CancellationToken.None);

        Assert.NotNull(seen);
        Assert.False(string.IsNullOrEmpty(seen!.Agent.Id));
    }

    /// <summary>
    /// THE DESCRIPTION IS A UI LABEL AND NEVER REACHES THE MODEL (D9).
    ///
    /// <para>It used to be passed as the child's briefing, which put a 3-5 word status-row label into
    /// the highest-authority position in its system message — under a heading saying "this is what you
    /// were created to do; where it disagrees with anything above, follow this". Contentless, so
    /// harmless, and structurally the wrong thing in the wrong slot.</para>
    /// </summary>
    [Fact]
    public async Task TheDescription_IsAUiLabel_AndIsNotSentToTheChild()
    {
        var spawner = new SubAgentSpawner(FactoryOver(Answering("done")));

        SubAgent? child = null;
        await spawner.TryInvokeAsync(SpawnCall(description: "audit the parser"), c => child = c,
            CancellationToken.None);

        var system = Assert.Single(child!.Agent.Context.Messages.Where(m => m.Role == "system"));
        Assert.DoesNotContain("audit the parser", system.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE PARENT'S CONTEXT REACHES THE CHILD'S SYSTEM MESSAGE — the channel that did not exist.
    ///
    /// <para>Facts the child cannot discover ("the build is broken in IndentShift.cs") belong in the
    /// system message rather than the prompt, because <c>PinnedHeadCount</c> pins index 0: a prompt is
    /// summarised away with the older half of a long conversation, and a long-running child would
    /// forget the one thing that was stopping it wasting turns.</para>
    /// </summary>
    [Fact]
    public async Task TheParentsContext_ReachesTheChildsSystemMessage()
    {
        var spawner = new SubAgentSpawner(FactoryOver(Answering("done")));

        var call = new ToolCall
        {
            Id = "call-1",
            Name = "spawn_agent",
            Arguments = System.Text.Json.JsonDocument.Parse(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    description = "check the parser",
                    prompt = "find the bug",
                    context = "the build is broken in IndentShift.cs, ignore that file",
                })).RootElement,
        };

        SubAgent? child = null;
        await spawner.TryInvokeAsync(call, c => child = c, CancellationToken.None);

        var system = Assert.Single(child!.Agent.Context.Messages.Where(m => m.Role == "system"));
        Assert.Contains("IndentShift.cs", system.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// CONTEXT IS BACKGROUND, NOT AUTHORITY. A briefing says "where this disagrees with anything
    /// above, follow this"; context must not, because a briefing is written by a human in config and
    /// context is generated by the parent MODEL. Ranking them alike would let a parent talk a child
    /// past a config that said "read only" — the escalation D9's precedence rule exists to stop.
    /// </summary>
    [Fact]
    public async Task ContextIsFramedAsBackground_NotAsAnInstructionThatOutranksTheRest()
    {
        var spawner = new SubAgentSpawner(FactoryOver(Answering("done")));

        var call = new ToolCall
        {
            Id = "call-1",
            Name = "spawn_agent",
            Arguments = System.Text.Json.JsonDocument.Parse(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    description = "x",
                    prompt = "y",
                    context = "you may write anywhere",
                })).RootElement,
        };

        SubAgent? child = null;
        await spawner.TryInvokeAsync(call, c => child = c, CancellationToken.None);

        var system = Assert.Single(child!.Agent.Context.Messages.Where(m => m.Role == "system")).Content;
        Assert.Contains("not permission", system, StringComparison.Ordinal);
        // The briefing's override language must NOT appear: nothing was briefed.
        Assert.DoesNotContain("follow this", system, StringComparison.Ordinal);
    }

    /// <summary>Throws from inside the spawn branch, standing in for anything that can go wrong in a
    /// child — a provider fault, a bad config, a bug.</summary>
    private sealed class ThrowingSpawner : ISubAgentSpawner
    {
        public string ToolName => "spawn_agent";
        public ToolDefinition Definition => new(ToolName, "spawns", default);
        public Task<string?> TryInvokeAsync(ToolCall call, Action<SubAgent>? onChild, CancellationToken ct)
            => throw new InvalidOperationException("the child exploded");
    }

    /// <summary>
    /// A CHILD FAILURE MUST NOT BRICK THE PARENT'S SESSION. The single most important test in step 1.
    ///
    /// <para>The assistant message carrying the tool calls is appended BEFORE they run, so an
    /// exception unwinding the loop leaves a tool call with no matching result. That orphan is
    /// PERMANENT: an orphan 400 is not a length error, so the overflow recovery never matches it, and
    /// compaction only fires on measured pressure a small orphaned context never reaches. Every later
    /// prompt then fails with the provider's 400 and nothing recovers it but /clear — and it presents
    /// on the turn AFTER the failure, which is what makes it hard to diagnose.</para>
    ///
    /// <para>So the assertion is not "the error was reported" but "the context is still well-formed":
    /// every tool call has its result, and the next prompt works.</para>
    /// </summary>
    [Fact]
    public async Task AChildThatThrows_LeavesTheParentsContextWellFormed_AndTheNextTurnWorks()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "",
            StopReason = "tool_use",
            ToolCalls = [SpawnCall()],
        });
        provider.EnqueueResponse(new LlmResponse { Text = "the child failed, here is what I know", StopReason = "end_turn" });
        provider.EnqueueResponse(new LlmResponse { Text = "a later answer", StopReason = "end_turn" });

        var parent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new ThrowingSpawner())
        {
            // A SPAWNER IS NO LONGER ENOUGH — the agent must also be in fan-out mode. That is the
            // point of the mode: a session holding a spawner it is not using offers no spawn tool.
            Mode = AgentMode.FanOut,
        };

        // The turn completes rather than throwing.
        var first = await parent.SendAsync("delegate something", CancellationToken.None);
        Assert.Equal(SendOutcome.Completed, first.Outcome);

        // EVERY TOOL CALL HAS ITS RESULT. This is the orphan check, and it is what the provider's
        // 400 would otherwise be complaining about.
        var toolCallIds = parent.Context.Messages
            .Where(m => m.ToolCalls is { Count: > 0 })
            .SelectMany(m => m.ToolCalls!)
            .Select(c => c.Id ?? c.Name)
            .ToList();
        var resultIds = parent.Context.Messages
            .Where(m => m.Role == "tool")
            .Select(m => m.ToolCallId)
            .ToList();

        Assert.NotEmpty(toolCallIds);
        Assert.All(toolCallIds, id => Assert.Contains(id, resultIds));

        // And the session keeps working, which is the property a user would notice losing.
        var second = await parent.SendAsync("carry on", CancellationToken.None);
        Assert.Equal("a later answer", second.Text);
    }

    /// <summary>
    /// THE TOOL DESCRIPTION CARRIES THE WHEN-NOT-TO (D25), because that is the part that stops a model
    /// delegating work it should simply do. Asserted rather than assumed: the description is the only
    /// place this guidance exists, so losing it in an edit would be silent.
    /// </summary>
    [Fact]
    public void Definition_TellsTheModelWhenNotToSpawn()
    {
        var definition = new SubAgentSpawner(FactoryOver(Answering("x"))).Definition;

        // THE BENEFIT AND THE COST, BOTH. A model weighing a decision needs both sides: ours used to
        // state only the cost of spawning (a briefing plus a full run) and never what delegation buys,
        // which is an argument with one half missing.
        // Asserted on a fragment that does not span the source's line break — the raw string keeps
        // its newlines, so "keep the conclusion, not the file dumps" is not contiguous in the value.
        Assert.Contains("the conclusion, not the file dumps",
            definition.Description, StringComparison.Ordinal);

        // THE TEST IS WHAT YOU ALREADY KNOW, not a count of tool calls. "two or three tool calls
        // away" asked the model to predict something it cannot know before starting; "you already
        // know the file, symbol or value" is checkable up front.
        Assert.Contains("single-fact lookup", definition.Description, StringComparison.Ordinal);

        // DO NOT DUPLICATE DELEGATED WORK — a real failure mode, and one both Claude Code and
        // opencode call out: the model spawns, grows impatient, does the work anyway, pays twice and
        // ends up reconciling two answers.
        Assert.Contains("do not also do it yourself", definition.Description, StringComparison.Ordinal);

        // AND WHAT GOES IN WHICH CHANNEL. Found on a live drive: asked to spawn WITH context, the
        // model folded the fact into the PROMPT instead and left `context` unused — the description
        // said nothing about it, and a schema blurb reads as documentation rather than instruction.
        //
        // The two are not interchangeable, which is why the description gives the mechanical reason
        // rather than a preference: a fact in the prompt is a user turn and is summarised away when
        // the child compacts; a fact in context sits in the system message, pinned at index 0, and
        // survives. On a short run both work. On a long one the prompt version is forgotten exactly
        // when it was most needed.
        Assert.Contains("in context", definition.Description, StringComparison.Ordinal);
        Assert.Contains("whole run", definition.Description, StringComparison.Ordinal);
        Assert.Contains("cannot spawn sub-agents of its own", definition.Description, StringComparison.Ordinal);
        Assert.Contains("NOT shown to the user", definition.Description, StringComparison.Ordinal);
    }

    // ---- 1e: telemetry ---------------------------------------------------------------------

    /// <summary>
    /// A RUNNING CHILD'S ROW REPORTS PROGRESS. Without this the row shows a spinner and nothing else
    /// for however long the child runs — indistinguishable from frozen, which is the state a
    /// minutes-long child spends most of its life in.
    /// </summary>
    [Fact]
    public async Task ARunningChild_ReportsProgressOntoItsRow()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "",
            StopReason = "tool_use",
            ToolCalls = [SpawnCall()],
        });
        provider.EnqueueResponse(new LlmResponse { Text = "parent done", StopReason = "end_turn" });

        // The child's own provider, answering after one tool call so it takes two turns.
        var childProvider = new MockLlmProvider();
        childProvider.EnqueueResponse(new LlmResponse
        {
            Text = "",
            StopReason = "tool_use",
            ToolCalls = [new ToolCall { Id = "t1", Name = "read_file",
                Arguments = System.Text.Json.JsonDocument.Parse("""{"path":"nope.txt"}""").RootElement }],
        });
        childProvider.EnqueueResponse(new LlmResponse { Text = "child done", StopReason = "end_turn" });

        var jobs = new NullJobPanel();
        var parent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            new RecordingSink(), jobs, logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(childProvider)))
        {
            // A SPAWNER IS NO LONGER ENOUGH — the agent must also be in fan-out mode. That is the
            // point of the mode: a session holding a spawner it is not using offers no spawn tool.
            Mode = AgentMode.FanOut,
        };

        await parent.SendAsync("delegate", CancellationToken.None);

        // The row carries progress text rather than staying blank...
        var row = Assert.Single(jobs.Jobs);
        Assert.False(string.IsNullOrWhiteSpace(row.ProgressMessage),
            "the row never reported progress — it would render as a frozen spinner");
        Assert.Contains("turn", row.ProgressMessage!, StringComparison.Ordinal);

        // ...and EVERY tick arrived through UpdateProgress, NOT UpdateJob. That distinction is the
        // whole point: UpdateJob force-expands the row and blanks its body on every call, so a
        // per-second tick through it would re-open a row the user collapsed and erase its contents.
        //
        // COUNTED, NOT MERELY NON-ZERO. A first draft asserted ProgressTicks > 0 and passed even with
        // the reporter routed back through UpdateJob, because the "starting…" tick alone satisfied
        // it. The real invariant is that UpdateJob fires only for genuine state transitions — one
        // here, when the tool call completes — and everything else goes through UpdateProgress.
        Assert.True(jobs.ProgressTicks >= 2,
            $"expected the starting tick plus at least one turn report, saw {jobs.ProgressTicks}");
        Assert.Equal(1, jobs.StateTransitions);
    }

    /// <summary>
    /// THE ROW IS A WORKER, NOT A FILE OPERATION. ToolPluginType maps unknown names to "file", and
    /// InlineJobSink.IsCompactRow treats anything that is not "llm_agent" as compact — so without this
    /// the row COLLAPSES the moment the child finishes, hiding the answer behind an "expand…".
    /// </summary>
    [Fact]
    public async Task ASpawnRow_IsTypedAsAWorker_SoItStaysExpanded()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "",
            StopReason = "tool_use",
            ToolCalls = [SpawnCall()],
        });
        provider.EnqueueResponse(new LlmResponse { Text = "parent done", StopReason = "end_turn" });

        var jobs = new NullJobPanel();
        var parent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            new RecordingSink(), jobs, logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(Answering("child done"))))
        {
            // A SPAWNER IS NO LONGER ENOUGH — the agent must also be in fan-out mode. That is the
            // point of the mode: a session holding a spawner it is not using offers no spawn tool.
            Mode = AgentMode.FanOut,
        };

        await parent.SendAsync("delegate", CancellationToken.None);

        Assert.Equal("llm_agent", Assert.Single(jobs.Jobs).PluginType);
    }

    // ---- the permission prompt names the child --------------------------------------------------

    /// <summary>Captures the requests a gate is asked to approve, so a test can read who was named.</summary>
    private sealed class RecordingGate : CxAgent.Core.Permissions.IPermissionGate
    {
        public List<CxAgent.Core.Permissions.PermissionRequest> Seen { get; } = [];

        public Task<bool> RequestAsync(CxAgent.Core.Permissions.PermissionRequest request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// A CHILD'S PERMISSION REQUEST CARRIES ITS DESCRIPTION, END TO END.
    ///
    /// <para>Observed live: a child asked to run shell commands and the prompt looked exactly like
    /// the parent asking. This pins the whole chain — spawn description becomes the child's briefing,
    /// the briefing becomes its requester label, the label rides on its JobContext, and the gated
    /// plugin stamps it onto every request it raises.</para>
    ///
    /// <para>A LABEL, NOT AN ID: "01KZQ…" in a prompt is unanswerable, where the phrase the parent's
    /// model wrote to name the task is something a user can weigh.</para>
    /// </summary>
    [Fact]
    public async Task AChildsPermissionRequest_NamesTheChild()
    {
        var gate = new RecordingGate();
        var plugins = PluginRegistry.CreateWithBuiltins(null, gate);

        var childProvider = new MockLlmProvider();
        childProvider.EnqueueResponse(new LlmResponse
        {
            Text = "",
            StopReason = "tool_use",
            ToolCalls = [new ToolCall { Id = "t1", Name = "run_shell",
                Arguments = System.Text.Json.JsonDocument.Parse("""{"command":"ls -l"}""").RootElement }],
        });
        childProvider.EnqueueResponse(new LlmResponse { Text = "listed", StopReason = "end_turn" });

        var factory = new SubAgentFactory(childProvider, plugins, new TokenLedger(null),
            logs: null, maxTurns: 50, compressAbove: 40_000, contextWindow: 200_000,
            globalInstructionsDir: null, mcp: null);

        await new SubAgentSpawner(factory).TryInvokeAsync(
            SpawnCall(description: "Analyze TextWrapping failures"), null, CancellationToken.None);

        var shellRequest = Assert.Single(gate.Seen,
            r => r.Kind == CxAgent.Core.Permissions.PermissionKind.Shell);
        Assert.Equal("Analyze TextWrapping failures", shellRequest.Requester);
    }

    /// <summary>The parent's own requests stay unattributed — see PermissionPromptControlTests for
    /// why that is a decision rather than an omission.</summary>
    [Fact]
    public async Task TheParentsOwnPermissionRequest_HasNoRequester()
    {
        var gate = new RecordingGate();
        var plugins = PluginRegistry.CreateWithBuiltins(null, gate);

        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "",
            StopReason = "tool_use",
            ToolCalls = [new ToolCall { Id = "t1", Name = "run_shell",
                Arguments = System.Text.Json.JsonDocument.Parse("""{"command":"ls -l"}""").RootElement }],
        });
        provider.EnqueueResponse(new LlmResponse { Text = "listed", StopReason = "end_turn" });

        var parent = new Agent(provider, plugins, new TokenLedger(null),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50);

        await parent.SendAsync("list the files", CancellationToken.None);

        var shellRequest = Assert.Single(gate.Seen,
            r => r.Kind == CxAgent.Core.Permissions.PermissionKind.Shell);
        Assert.Null(shellRequest.Requester);
    }

    // ---- mode gates the whole capability --------------------------------------------------------

    /// <summary>
    /// SINGLE MODE OFFERS NO SPAWN TOOL, even when a spawner is wired.
    ///
    /// <para>The seam is the same one that makes no-nesting structural for a child: a model cannot
    /// call a tool it was never sent. Here it is a session-level switch rather than a construction
    /// fact, which is what lets it change mid-session.</para>
    /// </summary>
    [Fact]
    public async Task SingleMode_DoesNotOfferTheSpawnTool_EvenWithASpawnerWired()
    {
        var provider = Answering("done");
        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(Answering("child"))))
        {
            Mode = AgentMode.Single,
        };

        await agent.SendAsync("go", CancellationToken.None);

        Assert.NotNull(provider.LastTools);
        Assert.DoesNotContain(provider.LastTools!, t => t.Name == "spawn_agent");
    }

    /// <summary>Fan-out offers it — the difference is the mode, not the wiring.</summary>
    [Fact]
    public async Task FanOutMode_OffersTheSpawnTool()
    {
        var provider = Answering("done");
        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(Answering("child"))))
        {
            Mode = AgentMode.FanOut,
        };

        await agent.SendAsync("go", CancellationToken.None);

        Assert.Contains(provider.LastTools!, t => t.Name == "spawn_agent");
    }

    /// <summary>
    /// SWITCHING MODE TAKES EFFECT ON THE NEXT PROMPT, WITHOUT REBUILDING ANYTHING — and the
    /// conversation survives.
    ///
    /// <para>Both things a mode changes are rebuilt every prompt anyway: the tool list at the request
    /// build, and the system message reconciled at index 0. The conversation belongs to the host and
    /// is handed to the agent, so a switch cannot disturb it — messages 1..N are untouched and only
    /// index 0 is replaced.</para>
    /// </summary>
    [Fact]
    public async Task SwitchingMode_TakesEffectNextPrompt_AndKeepsTheConversation()
    {
        var provider = Answering("one", "two", "three");
        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(Answering("child"))))
        {
            Mode = AgentMode.FanOut,
        };

        await agent.SendAsync("remember this word: pelican", CancellationToken.None);
        Assert.Contains(provider.LastTools!, t => t.Name == "spawn_agent");
        var messagesBefore = agent.Context.Messages.Count;

        agent.Mode = AgentMode.Single;
        await agent.SendAsync("and now?", CancellationToken.None);

        // The tool is gone...
        Assert.DoesNotContain(provider.LastTools!, t => t.Name == "spawn_agent");
        // ...and everything said before the switch is still there.
        Assert.True(agent.Context.Messages.Count > messagesBefore);
        Assert.Contains(agent.Context.Messages, m => m.Content.Contains("pelican", StringComparison.Ordinal));
    }

    /// <summary>
    /// A SPAWN CALL IN SINGLE MODE IS REFUSED, not quietly honoured.
    ///
    /// <para>A model that saw <c>spawn_agent</c> in an earlier fan-out turn can call it by name after
    /// a switch — the conversation still contains the evidence that the tool once existed. Gating only
    /// the tool LIST would leave the dispatch branch happily running a child the user had just turned
    /// off.</para>
    /// </summary>
    [Fact]
    public async Task ASpawnCallInSingleMode_IsRefused()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "",
            StopReason = "tool_use",
            ToolCalls = [SpawnCall()],
        });
        provider.EnqueueResponse(new LlmResponse { Text = "understood", StopReason = "end_turn" });

        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(Answering("child ran!"))))
        {
            Mode = AgentMode.Single,
        };

        await agent.SendAsync("delegate something", CancellationToken.None);

        // It fell through to "no such tool" rather than running a child.
        var toolResult = Assert.Single(agent.Context.Messages, m => m.Role == "tool");
        Assert.DoesNotContain("child ran!", toolResult.Content, StringComparison.Ordinal);
    }
}

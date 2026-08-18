using CxAgent.Core.Sessions;
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
        new(new SubAgentFactory.SubAgentRuntime
        {
            Provider = provider,
            Plugins = PluginRegistry.CreateWithBuiltins(),
            Ledger = new TokenLedger(),
            MaxTurns = 50,
            CompressAbove = 40_000,
            ContextWindow = 200_000,
        });

    /// <summary>
    /// THE OLD NAME STILL WORKS. A rename is invisible to a model working from habit, or to a
    /// RESUMED conversation whose earlier turns called it spawn_agent — and an unknown tool is a
    /// hard failure costing a turn, for a call that is completely unambiguous.
    /// </summary>
    [Fact]
    public async Task Spawn_StillAnswersToItsOldName()
    {
        var spawner = new SubAgentSpawner(FactoryOver(Answering("found it")));

        var result = await spawner.TryInvokeAsync(
            new ToolCall
            {
                Id = "call-1",
                Name = "spawn_agent",
                Arguments = System.Text.Json.JsonDocument.Parse(
                    System.Text.Json.JsonSerializer.Serialize(
                        new { description = "find thing", prompt = "find the thing" })).RootElement,
            },
            onChild: null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("found it", result!);
    }

    /// <summary>...but only the new name is advertised, so nothing pulls the model backwards.</summary>
    [Fact]
    public void OnlyTheCurrentSpawnNameIsOffered()
    {
        var spawner = new SubAgentSpawner(FactoryOver(Answering("x")));

        Assert.Equal("task", spawner.Definition.Name);
    }

    private static ToolCall SpawnCall(string prompt = "find the thing", string description = "find thing") =>
        new()
        {
            Id = "call-1",
            Name = "task",
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

        var factory = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = provider,
            Plugins = PluginRegistry.CreateWithBuiltins(),
            Ledger = new TokenLedger(),
            MaxTurns = 2,
            CompressAbove = 40_000,
            ContextWindow = 200_000,
        });

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
            Name = "task",
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
            Name = "task",
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
            Name = "task",
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

    /// <summary>
    /// CANCELLING A SPAWN LEAVES THE CONTEXT MALFORMED — a live defect, not a step 3 hazard.
    ///
    /// <para>The assistant message carrying the tool calls is appended at <c>Agent.cs:766</c>, BEFORE
    /// the loop runs. When a child observes cancellation, <c>InvokeAndShowAsync</c> closes the row and
    /// RETHROWS — its comment says "there is no next request", which is not true of the session: the
    /// user presses Escape, the app catches the cancellation, and the conversation continues. But
    /// <c>messages</c> IS <c>_context.Messages</c> (<c>:383</c>), so the assistant message stays with
    /// a tool call that has no matching <c>tool</c> result.</para>
    ///
    /// <para>That is the orphan of §1b: the provider rejects the whole conversation with a 400,
    /// <c>ContextOverflow.IsOverflow</c> does not match it, and nothing recovers but <c>/clear</c>.
    /// One Escape during a sub-agent run poisons the session permanently.</para>
    ///
    /// <para>Asserted as the INVARIANT rather than the bug, so this test states what must be true and
    /// fails until it is: every tool call in the context has a matching result.</para>
    /// </summary>
    [Fact]
    public async Task CancellingAChild_LeavesNoToolCallWithoutAResult()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use", ToolCalls = [SpawnCall()],
        });

        using var cts = new CancellationTokenSource();

        // A child whose provider cancels the moment it is asked — which is what Escape does to a
        // child mid-run, since the token is the parent's turn token handed straight down.
        var childProvider = new CancellingProvider(cts);

        var parent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(childProvider)))
        {
            Mode = AgentMode.FanOut,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => parent.SendAsync("delegate", cts.Token));

        // THE INVARIANT: every tool call has a result. Stated as a set difference so the failure
        // message names the orphaned call rather than only a count.
        var calls = parent.Context.Messages
            .Where(m => m.ToolCalls is { Count: > 0 })
            .SelectMany(m => m.ToolCalls!)
            .Select(c => c.Id ?? c.Name)
            .ToList();
        var results = parent.Context.Messages
            .Where(m => m.Role == "tool" && m.ToolCallId is not null)
            .Select(m => m.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);

        var orphans = calls.Where(id => !results.Contains(id)).ToList();

        Assert.True(orphans.Count == 0,
            $"tool call(s) left with no result: {string.Join(", ", orphans)} — this context is "
          + "malformed and the provider will reject the next request with a 400.");
    }

    /// <summary>
    /// A CALL THAT NEVER STARTED IS AS ORPHANING AS ONE THAT WAS INTERRUPTED. The provider checks
    /// only that every id in the assistant message has a matching result — it does not care whether
    /// the tool ran. So a turn cancelled two calls into a list of three must backfill the third,
    /// which it never touched.
    ///
    /// <para>This is the half a "close the row on cancel" fix would miss: rows exist only for calls
    /// that started, and the unstarted one has no row to close.</para>
    /// </summary>
    [Fact]
    public async Task CancellingMidTurn_BackfillsCallsThatNeverRan()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "",
            StopReason = "tool_use",
            // THREE calls; the spawn is first and cancels, so the two reads never run.
            ToolCalls =
            [
                SpawnCall(),
                new ToolCall { Id = "r1", Name = "read_file",
                    Arguments = System.Text.Json.JsonDocument.Parse("""{"path":"a.txt"}""").RootElement },
                new ToolCall { Id = "r2", Name = "read_file",
                    Arguments = System.Text.Json.JsonDocument.Parse("""{"path":"b.txt"}""").RootElement },
            ],
        });

        using var cts = new CancellationTokenSource();

        var parent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(new CancellingProvider(cts))))
        {
            Mode = AgentMode.FanOut,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => parent.SendAsync("delegate", cts.Token));

        var results = parent.Context.Messages
            .Where(m => m.Role == "tool" && m.ToolCallId is not null)
            .Select(m => m.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("call-1", results);   // the spawn, interrupted
        Assert.Contains("r1", results);       // never started
        Assert.Contains("r2", results);       // never started
    }

    /// <summary>
    /// EXACTLY ONE RESULT PER CALL. The interrupted call may or may not have appended before it
    /// threw, so the backfill matches by id rather than counting — a second result for one id is its
    /// own malformation, and one the provider rejects just as readily as a missing one.
    /// </summary>
    [Fact]
    public async Task CancellingATurn_DoesNotDoubleAnswerACall()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use", ToolCalls = [SpawnCall()],
        });

        using var cts = new CancellationTokenSource();

        var parent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(new CancellingProvider(cts))))
        {
            Mode = AgentMode.FanOut,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => parent.SendAsync("delegate", cts.Token));

        var ids = parent.Context.Messages
            .Where(m => m.Role == "tool" && m.ToolCallId is not null)
            .Select(m => m.ToolCallId!)
            .ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    // ---- start-and-defer: several children in one turn -----------------------------------------

    private static ToolCall Spawn(string id, string description) =>
        new()
        {
            Id = id,
            Name = "task",
            Arguments = System.Text.Json.JsonDocument.Parse(
                System.Text.Json.JsonSerializer.Serialize(
                    new { description, prompt = "do the thing" })).RootElement,
        };

    /// <summary>
    /// TWO CHILDREN RUN AT ONCE. The point of the whole step, and the thing no other test can see:
    /// before this, every spawn was awaited inline, so two children were strictly sequential.
    ///
    /// <para>Asserted by construction rather than by timing — each child's provider blocks until BOTH
    /// have arrived. Under the old sequential loop the first child would wait forever and the test
    /// would hang; the timeout is what makes the failure legible rather than a false pass.</para>
    /// </summary>
    [Fact]
    public async Task TwoSpawnsInOneTurn_RunConcurrently()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use",
            ToolCalls = [Spawn("s1", "first"), Spawn("s2", "second")],
        });
        provider.EnqueueResponse(new LlmResponse { Text = "parent done", StopReason = "end_turn" });

        // Both children must be INSIDE their provider call before either may return.
        var arrived = new SemaphoreSlim(0, 2);
        var bothArrived = new TaskCompletionSource();
        var gate = new RendezvousProvider(arrived, bothArrived);

        var parent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(gate)))
        {
            Mode = AgentMode.FanOut,
        };

        var run = parent.SendAsync("delegate twice", CancellationToken.None);

        // BOTH CHILDREN MUST ARRIVE BEFORE EITHER IS RELEASED. Sequentially this cannot happen: the
        // first child blocks forever waiting for a release that only comes once the second arrives,
        // and the second is never started. So the assertion is that both arrivals are observed —
        // and it FAILS, rather than hanging, because each wait has its own deadline.
        //
        // A first draft released on a background task that swallowed its own timeout, so the run
        // completed anyway and the test passed under a sabotaged (sequential) loop. Verified against
        // that sabotage now: this version fails.
        Assert.True(await arrived.WaitAsync(TimeSpan.FromSeconds(5)),
            "no child reached its provider");
        Assert.True(await arrived.WaitAsync(TimeSpan.FromSeconds(5)),
            "the second child never started while the first was still running — the spawns did not "
          + "overlap, so the loop is awaiting each one inline");

        bothArrived.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        // And both answered — the barrier held.
        var results = parent.Context.Messages
            .Where(m => m.Role == "tool" && m.ToolCallId is not null)
            .Select(m => m.ToolCallId!)
            .ToList();
        Assert.Contains("s1", results);
        Assert.Contains("s2", results);
    }

    /// <summary>
    /// A SPAWN NEVER OVERTAKES A PRECEDING TOOL. `[run_shell "git checkout -b x", spawn "work on x"]`
    /// is the shape that makes hoisting wrong: a child started before the branch exists works against
    /// the wrong tree and reports a confident answer, with no error anywhere.
    ///
    /// <para>Emitted order is preserved because the walk defers the AWAIT, never the CALL.</para>
    /// </summary>
    [Fact]
    public async Task ASpawnDoesNotOvertakeAToolThatPrecedesIt()
    {
        var order = new List<string>();

        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use",
            ToolCalls =
            [
                new ToolCall { Id = "t1", Name = "read_file",
                    Arguments = System.Text.Json.JsonDocument.Parse("""{"path":"nope.txt"}""").RootElement },
                Spawn("s1", "after the read"),
            ],
        });
        provider.EnqueueResponse(new LlmResponse { Text = "parent done", StopReason = "end_turn" });

        var childProvider = new RecordingOrderProvider(order, "child-started");

        var jobs = new OrderRecordingJobPanel(order);
        var parent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), jobs, logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(childProvider)))
        {
            Mode = AgentMode.FanOut,
        };

        await parent.SendAsync("read then delegate", CancellationToken.None);

        var readAt = order.IndexOf("read_file");
        var childAt = order.IndexOf("child-started");

        Assert.True(readAt >= 0 && childAt >= 0, $"missing marker in [{string.Join(", ", order)}]");
        Assert.True(readAt < childAt,
            $"the child started before the read that preceded it: [{string.Join(", ", order)}]");
    }

    /// <summary>
    /// ONE CHILD FAULTING DOES NOT ORPHAN ITS SIBLINGS. Every call of the turn gets exactly one
    /// result, whatever happened to any of them — a missing result is the 400 that ends the session,
    /// and "the other child threw" is no reason to hand the provider a malformed conversation.
    /// </summary>
    [Fact]
    public async Task AFaultedChild_DoesNotOrphanTheOthers()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use",
            ToolCalls = [Spawn("s1", "explodes"), Spawn("s2", "succeeds")],
        });
        provider.EnqueueResponse(new LlmResponse { Text = "parent done", StopReason = "end_turn" });

        var parent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(new FaultOnFirstProvider())))
        {
            Mode = AgentMode.FanOut,
        };

        await parent.SendAsync("delegate twice", CancellationToken.None);

        var results = parent.Context.Messages
            .Where(m => m.Role == "tool" && m.ToolCallId is not null)
            .Select(m => m.ToolCallId!)
            .ToList();

        Assert.Contains("s1", results);
        Assert.Contains("s2", results);
        Assert.Equal(results.Count, results.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// CANCELLING WITH SEVERAL IN FLIGHT still answers every call — 3-0's invariant, now under N.
    /// This is the case the barrier could lose through the back door: the exception leaves while a
    /// child is still running, and that child finishes onto an id the backfill has already answered.
    /// </summary>
    [Fact]
    public async Task CancellingWithTwoChildrenInFlight_AnswersEveryCallExactlyOnce()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use",
            ToolCalls = [Spawn("s1", "first"), Spawn("s2", "second")],
        });

        using var cts = new CancellationTokenSource();

        var parent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(new CancellingProvider(cts))))
        {
            Mode = AgentMode.FanOut,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => parent.SendAsync("delegate twice", cts.Token));

        var results = parent.Context.Messages
            .Where(m => m.Role == "tool" && m.ToolCallId is not null)
            .Select(m => m.ToolCallId!)
            .ToList();

        Assert.Contains("s1", results);
        Assert.Contains("s2", results);
        Assert.Equal(results.Count, results.Distinct(StringComparer.Ordinal).Count());
    }

    // ---- waiting on permission ------------------------------------------------------------------

    /// <summary>
    /// ONLY THE ASKING AGENT IS MARKED WAITING. The assertion that matters: with two children up,
    /// one at a prompt and one working, exactly one row changes.
    ///
    /// <para>A single-child version of this test would pass on a broken implementation — routing the
    /// signal from the SHARED gate by its display label would look right until two children of the
    /// same type existed, at which point both rows would flip together. The flag lives on the agent
    /// precisely because an agent knows whether it is the one waiting and a label does not.</para>
    /// </summary>
    [Fact]
    public void EveryAgentTracksItsOwnWait_AndStartsNotWaiting()
    {
        var a = new Agent(new MockLlmProvider(), PluginRegistry.CreateWithBuiltins(),
            new TokenLedger(), new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 5);
        var b = new Agent(new MockLlmProvider(), PluginRegistry.CreateWithBuiltins(),
            new TokenLedger(), new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 5);

        // PER AGENT, not per gate. The flag lives here precisely so two children sharing one gate —
        // and one display label, if they are the same type — remain distinguishable.
        Assert.False(a.IsWaitingOnPermission);
        Assert.False(b.IsWaitingOnPermission);
        Assert.NotSame(a, b);
    }

    /// <summary>
    /// THE GATED PLUGIN REPORTS THE WAIT, and reports its END even when the answer is no — a row
    /// left permanently "waiting" for an answer nobody will be asked for is worse than one that
    /// never said it was waiting. The signal is what an agent subscribes to in order to mark itself.
    /// </summary>
    [Fact]
    public async Task TheGatedPlugin_ReportsTheWaitAndItsEnd()
    {
        var ctx = new TestJobContext();
        var plugin = new CxAgent.Core.Permissions.PermissionGatedPlugin(
            new AlwaysAskPlugin(), new DenyingGate());

        // A real shell parameter set — PermissionPolicy reads `command` to build the request, so an
        // empty one throws before the gate is ever consulted and the test would pass on a fixture
        // fault rather than on the behaviour.
        var parameters = new JobParameters(new Dictionary<string, object?> { ["command"] = "ls" });

        await plugin.ExecuteAsync(parameters, ctx, CancellationToken.None);

        // True then false, in that order: the interval had a start and an end.
        Assert.Equal([true, false], ctx.PermissionWaits);
    }

    /// <summary>A shell plugin, so the gate is genuinely consulted rather than short-circuited.</summary>
    private sealed class AlwaysAskPlugin : IJobPlugin
    {
        public string TypeName => "shell";
        public string DisplayName => "shell";
        public JobSchema GetSchema() => new(TypeName, DisplayName, []);
        public JobValidation Validate(JobParameters parameters) => JobValidation.Valid();

        public Task<JobResult> ExecuteAsync(JobParameters parameters, IJobContext context,
            CancellationToken ct) =>
            Task.FromResult(new JobResult { Success = true, ExitCode = 0 });
    }

    /// <summary>Answers no, so the wait ends through the denial path rather than the happy one.</summary>
    private sealed class DenyingGate : CxAgent.Core.Permissions.IPermissionGate
    {
        public Task<bool> RequestAsync(CxAgent.Core.Permissions.PermissionRequest request,
            CancellationToken ct) => Task.FromResult(false);
    }

    // ---- the cap, and the bound that matters more ----------------------------------------------

    private static SubAgentFactory FactoryOver(ILlmProvider provider, TokenLedger ledger,
        int? maxConcurrent = null) =>
        new(new SubAgentFactory.SubAgentRuntime
        {
            Provider = provider,
            Plugins = PluginRegistry.CreateWithBuiltins(),
            Ledger = ledger,
            MaxTurns = 50,
            CompressAbove = 40_000,
            ContextWindow = 200_000,
            MaxConcurrentAgents = maxConcurrent,
        });

    /// <summary>
    /// A CAP OF ONE SERIALISES THE CHILDREN. The rendezvous that proves overlap becomes the proof of
    /// its absence: with one slot, the second child cannot arrive while the first holds it, so the
    /// wait times out — which is the whole point of a cap.
    /// </summary>
    [Fact]
    public async Task ACapOfOne_StopsTheSecondChildStarting()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use",
            ToolCalls = [Spawn("s1", "first"), Spawn("s2", "second")],
        });
        provider.EnqueueResponse(new LlmResponse { Text = "parent done", StopReason = "end_turn" });

        var arrived = new SemaphoreSlim(0, 2);
        var release = new TaskCompletionSource();
        var gate = new RendezvousProvider(arrived, release);

        var parent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(gate, new TokenLedger(), maxConcurrent: 1)))
        {
            Mode = AgentMode.FanOut,
        };

        var run = parent.SendAsync("delegate twice", CancellationToken.None);

        Assert.True(await arrived.WaitAsync(TimeSpan.FromSeconds(5)), "no child started at all");

        // THE SECOND MUST NOT ARRIVE while the first holds the only slot.
        Assert.False(await arrived.WaitAsync(TimeSpan.FromSeconds(2)),
            "both children ran at once despite a cap of 1");

        release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        // And the cap DELAYS rather than drops: both still answered.
        var results = parent.Context.Messages
            .Where(m => m.Role == "tool" && m.ToolCallId is not null)
            .Select(m => m.ToolCallId!)
            .ToList();
        Assert.Contains("s1", results);
        Assert.Contains("s2", results);
    }

    /// <summary>
    /// UNCONFIGURED MEANS UNBOUNDED — the default, and a decision rather than an oversight. A cap
    /// picked without evidence throttles every user to guard against a problem none of them may have,
    /// and cxagent cannot discover what its endpoint tolerates.
    /// </summary>
    [Fact]
    public async Task NoCapConfigured_RunsChildrenConcurrently()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use",
            ToolCalls = [Spawn("s1", "first"), Spawn("s2", "second")],
        });
        provider.EnqueueResponse(new LlmResponse { Text = "parent done", StopReason = "end_turn" });

        var arrived = new SemaphoreSlim(0, 2);
        var release = new TaskCompletionSource();

        var parent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(
                new RendezvousProvider(arrived, release), new TokenLedger())))
        {
            Mode = AgentMode.FanOut,
        };

        var run = parent.SendAsync("delegate twice", CancellationToken.None);

        Assert.True(await arrived.WaitAsync(TimeSpan.FromSeconds(5)), "no child started");
        Assert.True(await arrived.WaitAsync(TimeSpan.FromSeconds(5)),
            "the second child did not start — unconfigured must mean unbounded");

        release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(15));
    }


    /// <summary>Blocks until every child has arrived, so overlap is proven by construction rather
    /// than inferred from timing.</summary>
    private sealed class RendezvousProvider(SemaphoreSlim arrived, TaskCompletionSource release)
        : StubProvider
    {
        public override async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
            List<ChatMessage> messages, List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            arrived.Release();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            yield return new LlmStreamChunk("done", null, IsFinal: true, StopReason: "end_turn");
        }
    }

    /// <summary>Notes when a child first reaches its provider, for the ordering test.</summary>
    private sealed class RecordingOrderProvider(List<string> order, string marker) : StubProvider
    {
        public override async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
            List<ChatMessage> messages, List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (order) order.Add(marker);
            await Task.Yield();
            yield return new LlmStreamChunk("done", null, IsFinal: true, StopReason: "end_turn");
        }
    }

    /// <summary>Throws for the first child asked, answers the rest.</summary>
    private sealed class FaultOnFirstProvider : StubProvider
    {
        private int _calls;

        public override async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
            List<ChatMessage> messages, List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                throw new InvalidOperationException("the child exploded");

            await Task.Yield();
            yield return new LlmStreamChunk("done", null, IsFinal: true, StopReason: "end_turn");
        }
    }

    /// <summary>The boilerplate every stub above would otherwise repeat.</summary>
    private abstract class StubProvider : ILlmProvider
    {
        public string ProviderId => "stub";
        public string ModelId => "stub";
        public string DisplayName => "stub";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => true;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools, CancellationToken ct) =>
            Task.FromResult(new LlmResponse { Text = "done", StopReason = "end_turn" });

        public abstract IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
            List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct = default);
    }

    /// <summary>Notes each tool row as it opens, so a test can assert execution order.</summary>
    private sealed class OrderRecordingJobPanel(List<string> order) : IToolObserver
    {
        public void ToolsChanged(IReadOnlyList<Job> jobs)
        {
            foreach (var job in jobs)
                lock (order) order.Add(job.PlanLocalId ?? "?");
        }

        public void ToolUpdated(Job job) { }
        public void ToolProgressed(Job job) { }
        public void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) { }
        public void ToolOutputAppended(string jobId, string delta) { }
    }

    /// <summary>Cancels the token as soon as the model is asked, standing in for Escape landing while
    /// a child is mid-turn.</summary>
    private sealed class CancellingProvider(CancellationTokenSource cts) : ILlmProvider
    {
        public string ProviderId => "cancelling";
        public string ModelId => "cancelling";
        public string DisplayName => "cancelling";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => true;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools, CancellationToken ct)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException();
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            yield break;
        }
    }

    /// <summary>Throws from inside the spawn branch, standing in for anything that can go wrong in a
    /// child — a provider fault, a bad config, a bug.</summary>
    private sealed class ThrowingSpawner : ISubAgentSpawner
    {
        public void SwapDefaultProvider(ILlmProvider provider, int? contextWindow, string? instanceName) { }

        public string ToolName => "task";
        public ToolDefinition Definition => new(ToolName, "spawns", default);
        public Task<string?> TryInvokeAsync(ToolCall call, Action<SubAgent>? onChild,
            CancellationToken ct, string? parentAgentId = null)
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

        var parent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
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

    // ---- 2c: the description lists the catalog --------------------------------------------------

    /// <summary>
    /// WITH NO TYPES CONFIGURED THE CATALOG IS STILL NOT EMPTY — `general` is always there, which is
    /// what stops the description ever having to say "valid types: (none)" and what makes adding a
    /// second type look like an addition rather than a section materialising from nothing.
    /// </summary>
    [Fact]
    public void Definition_WithNoConfiguredTypes_StillListsTheShippedTypes()
    {
        var d = new SubAgentSpawner(FactoryOver(Answering("x"))).Definition.Description;

        // THE SHIPPED TYPES ARE THERE WITHOUT CONFIG — that is the point of moving them into code.
        // What must NOT appear is a type nobody defined anywhere.
        Assert.Contains("- general: runs where you do", d, StringComparison.Ordinal);
        Assert.Contains("- builder:", d, StringComparison.Ordinal);
        Assert.DoesNotContain("- scout:", d, StringComparison.Ordinal);
    }

    /// <summary>
    /// A TYPE BOUND TO ANOTHER INSTANCE SAYS SO, because it is usually bound for a reason — a bigger
    /// window, a stronger model, a cheaper one — and that is a fact the parent should choose by.
    ///
    /// <para>THE INSTANCE, NOT THE MODEL. Two entries can serve the same model with different
    /// endpoints and windows, so naming the model would report a real routing decision as no routing
    /// at all.</para>
    /// </summary>
    [Fact]
    public void Definition_WhenATypeRunsElsewhere_NamesTheInstance()
    {
        var registry = ProviderRegistry.FromProviders(
            new Dictionary<string, ILlmProvider>(StringComparer.Ordinal)
            {
                ["local"] = Answering("x"),
                ["small"] = Answering("x"),
            },
            "local",
            new Dictionary<string, int?>(StringComparer.Ordinal) { ["small"] = 32_000 });

        var types = new AgentTypeCatalog(
            new Dictionary<string, AgentTypeConfig>(StringComparer.Ordinal)
            {
                ["cheap"] = new("Answers quick questions.", "small"),
            },
            registry);

        var d = new SubAgentSpawner(FactoryOver(Answering("x")), types).Definition.Description;

        Assert.Contains("[runs on small]", d, StringComparison.Ordinal);

        // The common type runs where the parent does and has nothing to say about it.
        Assert.Contains("- general: runs where you do", d, StringComparison.Ordinal);
        Assert.DoesNotContain("general: runs where you do, no special instructions [runs on",
            d, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE DESCRIPTION SAYS SEVERAL AGENTS MAY RUN AT ONCE, and no longer says the opposite.
    ///
    /// <para>Pinned because guidance vanishes silently. Two sentences here contradicted the
    /// capability the loop now has — "It runs once… and returns one message", and "wait for the
    /// result" in the singular. Both were TRUE when written; a reader hitting either alongside "you
    /// may launch several" believes the older, more specific one.</para>
    ///
    /// <para>This is also the sentence that made the 0-of-118 baseline uninformative about the model:
    /// it was told not to, in the one place D25 says such instructions belong.</para>
    /// </summary>
    [Fact]
    public void Definition_SaysAgentsCanRunConcurrently_AndNoLongerSaysOtherwise()
    {
        var d = new SubAgentSpawner(FactoryOver(Answering("x"))).Definition.Description;

        Assert.Contains("LAUNCH SEVERAL AT ONCE", d, StringComparison.Ordinal);
        Assert.Contains("ONE message", d, StringComparison.Ordinal);

        // The two sentences that forbade it. Matched on their distinguishing fragments so a reworded
        // version of the same claim still trips this.
        Assert.DoesNotContain("It runs once", d, StringComparison.Ordinal);
        Assert.DoesNotContain("returns\n        one message", d, StringComparison.Ordinal);
    }

    /// <summary>The obligation that only exists with several: overlapping work is the failure two
    /// agents can produce that one cannot, and no permission prompt catches it — stored "Always"
    /// rules mean both may write the same file without asking.</summary>
    [Fact]
    public void Definition_WarnsAgainstOverlappingWork()
    {
        var d = new SubAgentSpawner(FactoryOver(Answering("x"))).Definition.Description;

        Assert.Contains("non-overlapping", d, StringComparison.Ordinal);
    }

    /// <summary>
    /// CONFIGURED TYPES APPEAR WITH WHAT THEY ARE FOR. A model cannot pick from a catalog it has never
    /// seen (D5).
    ///
    /// <para>THIS TEST USED TO ASSERT THE OPPOSITE OF WHAT IT NOW DOES, and the change is the point.
    /// It read "a type's briefing IS its description — nothing extra needs writing in config", and
    /// checked that the catalog showed the briefing's first sentence with the rest dropped. That
    /// produced "- scout: You search and report." — text written in the second person for the
    /// CHILD, which tells a parent nothing about when to reach for it. The briefing is no longer
    /// consulted here at all.</para>
    /// </summary>
    [Fact]
    public void Definition_ListsConfiguredTypes_WithTheirDescriptions()
    {
        var d = SpawnerWith(("scout", "You search and report. Never edit files.",
            "when answering means reading across several files")).Definition.Description;

        Assert.Contains("- scout: when answering means reading across several files", d,
            StringComparison.Ordinal);

        // NEITHER HALF of the briefing reaches the catalog now — not the first sentence it used to
        // show, and not the rest it used to drop.
        Assert.DoesNotContain("You search and report", d, StringComparison.Ordinal);
        Assert.DoesNotContain("Never edit files", d, StringComparison.Ordinal);
    }

    /// <summary>
    /// SAY THE PARAMETER IS OPTIONAL. A model that suddenly sees a catalog may infer it MUST choose,
    /// and choose badly where `general` was right — a helpful list turned into a forced decision.
    /// </summary>
    [Fact]
    public void Definition_SaysTypeIsOptional()
    {
        var d = new SubAgentSpawner(FactoryOver(Answering("x"))).Definition.Description;

        Assert.Contains("Omit `type` for a general-purpose agent", d, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE TUNED PROSE SURVIVES. Everything above the catalog was tuned across three live drives and
    /// is load-bearing; types are an ADDITION to that text, not a rewrite of it.
    /// </summary>
    [Fact]
    public void Definition_KeepsTheTunedGuidance_AboveTheCatalog()
    {
        var d = new SubAgentSpawner(FactoryOver(Answering("x")), Catalog(("scout", "Search."))).Definition.Description;

        Assert.Contains("the conclusion, not the file dumps", d, StringComparison.Ordinal);
        Assert.Contains("single-fact lookup", d, StringComparison.Ordinal);
        Assert.Contains("do not also do it yourself", d, StringComparison.Ordinal);
        // And the catalog is BELOW it, so the guidance is read first.
        Assert.True(d.IndexOf("single-fact lookup", StringComparison.Ordinal)
                  < d.IndexOf("- scout:", StringComparison.Ordinal));
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
        var parent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), jobs, logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(childProvider)))
        {
            // A SPAWNER IS NO LONGER ENOUGH — the agent must also be in fan-out mode. That is the
            // point of the mode: a session holding a spawner it is not using offers no spawn tool.
            Mode = AgentMode.FanOut,
        };

        await parent.SendAsync("delegate", CancellationToken.None);

        // The row carries progress text rather than staying blank. SendAsync has returned by now, so
        // the header has already switched to its finished form — the live "N turns · x% ctx · 12s"
        // is what ToolProgressed carried DURING the run, counted below.
        var row = Assert.Single(jobs.Jobs);
        Assert.False(string.IsNullOrWhiteSpace(row.ProgressMessage),
            "the row never reported progress — it would render as a frozen spinner");
        Assert.Contains("done", row.ProgressMessage!, StringComparison.Ordinal);

        // The standing facts survive into the finished row: what this child WAS is still the first
        // question of a row you expand after the fact.
        Assert.Contains("type: general", row.ProgressBody!, StringComparison.Ordinal);

        // ...and EVERY tick arrived through ToolProgressed, NOT ToolUpdated. That distinction is the
        // whole point: ToolUpdated force-expands the row and blanks its body on every call, so a
        // per-second tick through it would re-open a row the user collapsed and erase its contents.
        //
        // COUNTED, NOT MERELY NON-ZERO. A first draft asserted ProgressTicks > 0 and passed even with
        // the reporter routed back through ToolUpdated, because the "starting…" tick alone satisfied
        // it. The real invariant is that ToolUpdated fires only for genuine state transitions — one
        // here, when the tool call completes — and everything else goes through ToolProgressed.
        Assert.True(jobs.ProgressTicks >= 2,
            $"expected the starting tick plus at least one turn report, saw {jobs.ProgressTicks}");
        Assert.Equal(1, jobs.StateTransitions);
    }

    /// <summary>
    /// THE ROW NAMES THE TYPE. Reported from a live session: the header showed
    /// <c>task {"description":"Explore cxgpu repo struct…</c> and the type was invisible.
    ///
    /// <para>The cause was generic truncation — DescribeCall clipped the serialised arguments at 60
    /// characters, and a spawn's JSON opens with <c>description</c> while <c>type</c> serialises
    /// LAST, so the one field identifying the worker was always the field cut off. A session that
    /// spawned an explore, another explore and a planner rendered three rows that read alike.</para>
    /// </summary>
    [Fact]
    public async Task ASpawnRow_LeadsWithTheAgentType_NotTruncatedJson()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use",
            ToolCalls = [SpawnCall(
                description: "Explore the cxgpu repository structure and report back",
                prompt: "explore it")],
        });
        provider.EnqueueResponse(new LlmResponse { Text = "parent done", StopReason = "end_turn" });

        var jobs = new NullJobPanel();
        var parent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), jobs, logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(Answering("child done"))))
        {
            Mode = AgentMode.FanOut,
        };

        await parent.SendAsync("delegate", CancellationToken.None);

        var row = Assert.Single(jobs.Jobs);

        // The TYPE leads, and the raw tool name and JSON braces are gone from the header entirely.
        Assert.StartsWith("general", row.DisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain("{", row.DisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain("task", row.DisplayName, StringComparison.Ordinal);

        // The description still appears — it is what distinguishes two agents of the SAME type.
        Assert.Contains("Explore", row.DisplayName, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CHILD'S SPEND IS ATTRIBUTED, and this is the half the per-model breakdown could never carry.
    ///
    /// <para>The panel had one attribution mechanism — spend keyed by model id — and it suppressed
    /// itself below two entries. But the ordinary fan-out session runs its children on the PARENT'S
    /// provider: one model, one entry, section hidden, and a whole run of spawned agents showing no
    /// attribution whatsoever. Here parent and child share one provider, exactly as configured
    /// sessions do, so ByModel cannot distinguish them and only this counter can.</para>
    /// </summary>
    [Fact]
    public async Task AChildsTokens_AreAttributedToWorkers_EvenOnTheParentsModel()
    {
        var ledger = new TokenLedger();

        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use", ToolCalls = [SpawnCall()],
            Usage = new LlmUsage { InputTokens = 100, OutputTokens = 10 },
        });
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "parent done", StopReason = "end_turn",
            Usage = new LlmUsage { InputTokens = 50, OutputTokens = 5 },
        });

        // THE SAME MODEL ID as the parent — that is the case that was invisible.
        var childProvider = new MockLlmProvider();
        childProvider.EnqueueResponse(new LlmResponse
        {
            Text = "child done", StopReason = "end_turn",
            Usage = new LlmUsage { InputTokens = 700, OutputTokens = 30 },
        });

        var parent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), ledger,
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
            {
                Provider = childProvider,
                Plugins = PluginRegistry.CreateWithBuiltins(),
                Ledger = ledger,
                MaxTurns = 50,
                CompressAbove = 40_000,
                ContextWindow = 200_000,
            })))
        {
            Mode = AgentMode.FanOut,
        };

        await parent.SendAsync("delegate", CancellationToken.None);

        // The child's 730 and nobody else's: the parent spent 165 across its two turns.
        Assert.Equal(730, ledger.SubAgentTokens);
        Assert.Equal(895, ledger.TotalTokens);

        // And ByModel genuinely cannot answer this — one bucket, both agents in it. Asserted rather
        // than assumed, because if the mock ever gave the two providers different ids this test
        // would silently stop covering the case it exists for.
        Assert.Single(ledger.ByModel);
    }

    /// <summary>
    /// THE SESSION READOUT REPAINTS WHILE A CHILD RUNS.
    ///
    /// <para>Spend reached the panel only on the parent's TurnCompleted — and a parent completes no
    /// turns while blocked inside the spawn tool. So a worker could burn a window's worth of tokens
    /// and the panel showed pre-spawn figures for the whole run: right in memory, stale on screen,
    /// and worst in exactly the sessions the breakdown exists for.</para>
    /// </summary>
    [Fact]
    public async Task ARunningChild_RaisesChildSpend_SoThePanelCanRepaintMidTurn()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use", ToolCalls = [SpawnCall()],
        });
        provider.EnqueueResponse(new LlmResponse { Text = "parent done", StopReason = "end_turn" });

        // A CHILD THAT TAKES TWO TURNS — one tool call, then an answer. A one-turn child would make
        // this assertion `>= 1`, which is the trap ProgressTicks fell into: a single raise proves the
        // event is wired but not that it fires AS the child works, which is the whole defect.
        var childProvider = new MockLlmProvider();
        childProvider.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use",
            ToolCalls = [new ToolCall { Id = "t1", Name = "read_file",
                Arguments = System.Text.Json.JsonDocument.Parse("""{"path":"nope.txt"}""").RootElement }],
        });
        childProvider.EnqueueResponse(new LlmResponse { Text = "child done", StopReason = "end_turn" });

        var parent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(childProvider)))
        {
            Mode = AgentMode.FanOut,
        };

        var raised = 0;
        parent.ChildSpend += () => raised++;

        await parent.SendAsync("delegate", CancellationToken.None);

        // Both of the child's turns reported, while the parent sat inside one tool call completing
        // none of its own — which is exactly the window in which the panel used to go stale.
        Assert.True(raised >= 2,
            $"expected one report per child turn while the parent was blocked, saw {raised}");
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
        var parent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
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

        var factory = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = childProvider,
            Plugins = plugins,
            Ledger = new TokenLedger(),
            MaxTurns = 50,
            CompressAbove = 40_000,
            ContextWindow = 200_000,
        });

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

        var parent = new Agent(provider, plugins, new TokenLedger(),
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
        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(Answering("child"))))
        {
            Mode = AgentMode.Single,
        };

        await agent.SendAsync("go", CancellationToken.None);

        Assert.NotNull(provider.LastTools);
        Assert.DoesNotContain(provider.LastTools!, t => t.Name == "task");
    }

    /// <summary>Fan-out offers it — the difference is the mode, not the wiring.</summary>
    [Fact]
    public async Task FanOutMode_OffersTheSpawnTool()
    {
        var provider = Answering("done");
        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(Answering("child"))))
        {
            Mode = AgentMode.FanOut,
        };

        await agent.SendAsync("go", CancellationToken.None);

        Assert.Contains(provider.LastTools!, t => t.Name == "task");
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
        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new SubAgentSpawner(FactoryOver(Answering("child"))))
        {
            Mode = AgentMode.FanOut,
        };

        await agent.SendAsync("remember this word: pelican", CancellationToken.None);
        Assert.Contains(provider.LastTools!, t => t.Name == "task");
        var messagesBefore = agent.Context.Messages.Count;

        agent.Mode = AgentMode.Single;
        await agent.SendAsync("and now?", CancellationToken.None);

        // The tool is gone...
        Assert.DoesNotContain(provider.LastTools!, t => t.Name == "task");
        // ...and everything said before the switch is still there.
        Assert.True(agent.Context.Messages.Count > messagesBefore);
        Assert.Contains(agent.Context.Messages, m => m.Content.Contains("pelican", StringComparison.Ordinal));
    }

    /// <summary>
    /// A SPAWN CALL IN SINGLE MODE IS REFUSED, not quietly honoured.
    ///
    /// <para>A model that saw <c>task</c> in an earlier fan-out turn can call it by name after
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

        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
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

    // ---- 2b: named types ------------------------------------------------------------------------

    private static AgentTypeCatalog Catalog(params (string Name, string Briefing)[] types)
    {
        var cfg = types.ToDictionary(t => t.Name,
            t => new CxAgent.Core.Llm.AgentTypeConfig(t.Briefing), StringComparer.Ordinal);
        return new AgentTypeCatalog(cfg, null);
    }

    private static ToolCall TypedSpawn(string? type, string prompt = "find it") =>
        new()
        {
            Id = "call-1",
            Name = "task",
            Arguments = System.Text.Json.JsonDocument.Parse(
                System.Text.Json.JsonSerializer.Serialize(
                    type is null ? new { description = "d", prompt }
                                 : (object)new { description = "d", prompt, type })).RootElement,
        };

    /// <summary>
    /// A TYPE'S BRIEFING REACHES THE CHILD'S SYSTEM MESSAGE — D9's precedence working for the first
    /// time. The briefing slot was left deliberately null since ea97fbd, on the grounds that the only
    /// legitimate author of the highest-authority text in a prompt is a human writing config. This is
    /// that human.
    /// </summary>
    [Fact]
    public async Task AType_PutsItsBriefingInTheChildsSystemMessage()
    {
        var spawner = new SubAgentSpawner(FactoryOver(Answering("done")),
            Catalog(("scout", "You search and report. Never edit files.")));

        SubAgent? child = null;
        await spawner.TryInvokeAsync(TypedSpawn("scout"), c => child = c, CancellationToken.None);

        var system = Assert.Single(child!.Agent.Context.Messages.Where(m => m.Role == "system"));
        Assert.Contains("You search and report", system.Content, StringComparison.Ordinal);
        // Under the briefing heading, which is what makes it outrank everything above it.
        Assert.Contains("# Your task", system.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// TWO TYPES PRODUCE TWO DIFFERENT PROMPTS. Asserted rather than driven: a judgement about
    /// "visibly different behaviour" is exactly the kind of signal that proved unreliable when three
    /// prompt interventions produced two nulls and one win.
    /// </summary>
    [Fact]
    public async Task TwoTypes_ProduceDifferentChildPrompts()
    {
        // NEITHER NAME IS A BUILT-IN. A shipped type keeps its shipped briefing however config
        // spells it, so using `review` here would assert against text this test did not write.
        var catalog = Catalog(("scout", "You search and report."), ("judge", "You judge correctness."));

        SubAgent? a = null, b = null;
        await new SubAgentSpawner(FactoryOver(Answering("x")), catalog)
            .TryInvokeAsync(TypedSpawn("scout"), c => a = c, CancellationToken.None);
        await new SubAgentSpawner(FactoryOver(Answering("x")), catalog)
            .TryInvokeAsync(TypedSpawn("judge"), c => b = c, CancellationToken.None);

        var pa = a!.Agent.Context.Messages.First(m => m.Role == "system").Content;
        var pb = b!.Agent.Context.Messages.First(m => m.Role == "system").Content;

        Assert.Contains("search and report", pa, StringComparison.Ordinal);
        Assert.Contains("judge correctness", pb, StringComparison.Ordinal);
        Assert.NotEqual(pa, pb);
    }

    /// <summary>
    /// A BARE SPAWN AND AN EXPLICIT `general` ARE THE SAME THING, byte for byte. This is the property
    /// that makes "general" mean what the word says, and the one that proves the implicit default did
    /// not quietly acquire a briefing — a briefing being the highest-authority text in the prompt,
    /// acquiring one by accident is the failure worth guarding.
    /// </summary>
    [Fact]
    public async Task ABareSpawn_AndExplicitGeneral_ProduceIdenticalPrompts()
    {
        var catalog = Catalog(("scout", "Search."));

        SubAgent? bare = null, general = null;
        await new SubAgentSpawner(FactoryOver(Answering("x")), catalog)
            .TryInvokeAsync(TypedSpawn(null), c => bare = c, CancellationToken.None);
        await new SubAgentSpawner(FactoryOver(Answering("x")), catalog)
            .TryInvokeAsync(TypedSpawn("general"), c => general = c, CancellationToken.None);

        Assert.Equal(
            bare!.Agent.Context.Messages.First(m => m.Role == "system").Content,
            general!.Agent.Context.Messages.First(m => m.Role == "system").Content);
    }

    /// <summary>
    /// AN UNKNOWN TYPE IS REFUSED AND THE ERROR NAMES WHAT IS VALID. The model will invent
    /// "researcher"; silently substituting `general` means the user's briefing did not apply and
    /// nobody was told. `general` is always in the catalog, so the list is never empty.
    /// </summary>
    [Fact]
    public async Task AnUnknownType_IsRefused_WithTheValidNames()
    {
        var spawner = new SubAgentSpawner(FactoryOver(Answering("x")), Catalog(("scout", "Search.")));

        var result = await spawner.TryInvokeAsync(TypedSpawn("researcher"), null, CancellationToken.None);

        Assert.Contains("unknown agent type", result!, StringComparison.Ordinal);
        Assert.Contains("researcher", result!, StringComparison.Ordinal);
        Assert.Contains("general", result!, StringComparison.Ordinal);
        Assert.Contains("scout", result!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A TYPE'S maxTurns CAPS THE CHILD, and the envelope says `capped` rather than `completed`.
    ///
    /// <para>A per-type limit makes this path reachable in ordinary use for the first time — until now
    /// only a deliberately low session ceiling could reach it. A cap that reports `completed` hands
    /// the parent a salvage summary of unfinished work as though it were an answer, which is the
    /// failure D13 exists to prevent.</para>
    /// </summary>
    [Fact]
    public async Task ATypesMaxTurns_CapsTheChild_AndTheEnvelopeSaysCapped()
    {
        var provider = new MockLlmProvider();
        for (var i = 0; i < 6; i++)
            provider.EnqueueResponse(new LlmResponse
            {
                Text = "",
                StopReason = "tool_use",
                ToolCalls = [new ToolCall { Id = $"t{i}", Name = "read_file",
                    Arguments = System.Text.Json.JsonDocument.Parse($$"""{"path":"f{{i}}.txt"}""").RootElement }],
            });
        provider.EnqueueResponse(new LlmResponse { Text = "got partway", StopReason = "end_turn" });

        var catalog = new AgentTypeCatalog(
            new Dictionary<string, CxAgent.Core.Llm.AgentTypeConfig>
            {
                ["quick"] = new("Be quick.", Provider: null, MaxTurns: 2),
            }, null);

        var result = await new SubAgentSpawner(FactoryOver(provider), catalog)
            .TryInvokeAsync(TypedSpawn("quick"), null, CancellationToken.None);

        Assert.Contains("state=\"capped\"", result!, StringComparison.Ordinal);
        Assert.Contains("NOT a completed answer", result!, StringComparison.Ordinal);
    }

    // ---- the type catalog in the tool description -----------------------------------------------

    /// <summary>Builds a spawner over a catalog of (name, briefing, description) types.</summary>
    private static SubAgentSpawner SpawnerWith(params (string Name, string Briefing, string? Desc)[] types)
    {
        var configured = types.ToDictionary(
            t => t.Name,
            t => new AgentTypeConfig(t.Briefing, Description: t.Desc),
            StringComparer.Ordinal);

        return new SubAgentSpawner(FactoryOver(Answering("x")),
            new AgentTypeCatalog(configured, null));
    }

    /// <summary>The description is what the catalog shows, verbatim. This is the whole feature.</summary>
    [Fact]
    public void TheCatalog_ShowsTheDescription()
    {
        var text = SpawnerWith(("scout", "You search and report.", "when a search spans files"))
            .Definition.Description;

        Assert.Contains("- scout: when a search spans files", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// SUMMARISE IS GONE, and this is the test that says so rather than merely not exercising it.
    /// A type with a briefing and no description shows the neutral line — NOT the briefing's first
    /// sentence, which is written in the second person for the child and tells a chooser nothing.
    /// </summary>
    [Fact]
    public void AtypeWithNoDescription_ShowsTheNeutralLine_NotTheBriefing()
    {
        var text = SpawnerWith(("scout", "You search and report. Then stop.", null))
            .Definition.Description;

        Assert.Contains("- scout: runs where you do, no special instructions", text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("You search and report", text, StringComparison.Ordinal);
    }

    /// <summary>A type with neither gets the same line — one answer for "nothing was said".</summary>
    [Fact]
    public void AtypeWithNeitherDescriptionNorBriefing_ShowsTheNeutralLine()
    {
        var text = SpawnerWith(("bare", "", null)).Definition.Description;

        Assert.Contains("- bare: runs where you do, no special instructions", text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// EMITTED WHOLE. A description someone wrote long is their config and their tokens; the app does
    /// not shorten it to fit a number the old summariser happened to need.
    /// </summary>
    [Fact]
    public void ALongDescription_ReachesTheCatalogIntact()
    {
        var longText = new string('x', 400);
        var text = SpawnerWith(("scout", "b", longText)).Definition.Description;

        Assert.Contains(longText, text, StringComparison.Ordinal);
        Assert.DoesNotContain("…", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE DEFAULT TYPE IS LISTED, with the neutral line rather than a description.
    ///
    /// <para>The spec for this change assumed it was absent and was wrong: `general` has always had a
    /// row, and it should. It is a real name a model may pass, `AgentTypeCatalog` puts it first
    /// deliberately, and config may give it a briefing — a catalog that hid it would be describing a
    /// smaller world than the one the tool accepts.</para>
    ///
    /// <para>It has no description because none is configured, and the neutral line says exactly
    /// that. If a user gives `general` a description, it shows like any other.</para>
    /// </summary>
    [Fact]
    public void TheGeneralType_IsListed_WithTheNeutralLine()
    {
        var text = SpawnerWith(("scout", "b", "when a search spans files")).Definition.Description;

        Assert.Contains("- general: runs where you do, no special instructions", text,
            StringComparison.Ordinal);
    }
}

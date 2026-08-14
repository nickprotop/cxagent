using System.Text.Json;
using CxAgent.Core.Agent;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// THE TASK LIST MUST NOT SIT IN THE CACHE PREFIX.
///
/// <para>It used to be appended to the system message, so flipping one marker from <c>- [ ]</c> to
/// <c>- [&gt;]</c> rewrote that message and invalidated the provider's prompt cache from token zero.
/// Measured on a 116-turn drive: a 134-character change re-processed a 67,367-token context, on an
/// endpoint where an identical prompt costs 43ms warm against 1,420ms cold.</para>
///
/// <para>The compaction argument that justified the old placement did not hold — the system message
/// is rebuilt from the TodoList every turn, so the plan was always RE-INJECTED rather than
/// preserved. Moving it to the newest end keeps that mechanism and costs the cache nothing.</para>
/// </summary>
public class TaskListPlacementTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-todo-" + Guid.NewGuid().ToString("N"));
    public TaskListPlacementTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private Agent Build(MockLlmProvider provider) =>
        new(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 10,
            workingDir: _dir);

    private static LlmResponse Done(string text) =>
        new() { Text = text, ToolCalls = [], StopReason = "end_turn", Usage = new LlmUsage() };

    /// <summary>One todowrite call, with the given items.</summary>
    private static LlmResponse TodoCall(params (string Text, string Status)[] items)
    {
        var todos = items.Select(i => new { text = i.Text, status = i.Status }).ToArray();
        return LlmResponse.WithToolCall("todowrite", new { todos });
    }

    private static string SystemOf(MockLlmProvider p) =>
        p.LastMessages!.First(m => m.Role == "system").Content;

    /// <summary>
    /// THE REGRESSION. A status flip must leave the system message byte-identical — that message is
    /// the cache prefix, and rewriting it re-processes the whole conversation.
    /// </summary>
    [Fact]
    public async Task AStatusFlip_LeavesTheSystemMessageByteIdentical()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(TodoCall(("wire the flag", "pending")));
        provider.EnqueueResponse(Done("ok"));
        provider.EnqueueResponse(TodoCall(("wire the flag", "in_progress")));
        provider.EnqueueResponse(Done("ok"));

        var agent = Build(provider);
        await agent.SendAsync("first", CancellationToken.None);
        var before = SystemOf(provider);

        await agent.SendAsync("second", CancellationToken.None);

        Assert.Equal(before, SystemOf(provider));
    }

    /// <summary>The plan still reaches the model — moving it must not mean dropping it.</summary>
    [Fact]
    public async Task TheTaskList_IsSentToTheModel()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(TodoCall(("wire the flag", "pending")));
        provider.EnqueueResponse(Done("ok"));

        await Build(provider).SendAsync("go", CancellationToken.None);

        Assert.Contains(provider.LastMessages!, m => m.IsTaskList);
        Assert.Contains(provider.LastMessages!,
            m => m.IsTaskList && m.Content.Contains("wire the flag", StringComparison.Ordinal));
    }

    /// <summary>
    /// EXACTLY ONE, EVER. _context.Messages is the agent's persistent list, not a per-turn copy, so
    /// appending each turn would leave a trail of stale plans the model has to reconcile.
    /// </summary>
    [Fact]
    public async Task ThreeRewrites_LeaveExactlyOneTaskListMessage()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(TodoCall(("a", "pending")));
        provider.EnqueueResponse(Done("ok"));
        provider.EnqueueResponse(TodoCall(("a", "in_progress")));
        provider.EnqueueResponse(Done("ok"));
        provider.EnqueueResponse(TodoCall(("a", "completed")));
        provider.EnqueueResponse(Done("ok"));

        var agent = Build(provider);
        await agent.SendAsync("one", CancellationToken.None);
        await agent.SendAsync("two", CancellationToken.None);
        await agent.SendAsync("three", CancellationToken.None);

        Assert.Single(provider.LastMessages!, m => m.IsTaskList);
    }

    /// <summary>
    /// LAST, which is the whole point: everything before it stays cached, and the plan is the final
    /// thing the model reads before answering.
    /// </summary>
    [Fact]
    public async Task TheTaskList_IsTheFinalMessage()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(TodoCall(("a", "pending")));
        provider.EnqueueResponse(Done("ok"));

        await Build(provider).SendAsync("go", CancellationToken.None);

        Assert.True(provider.LastMessages![^1].IsTaskList);
    }

    /// <summary>
    /// A SESSION THAT NEVER PLANS PAYS NOTHING. No todos means no message at all — not an empty one.
    /// </summary>
    [Fact]
    public async Task WithNoTodos_NoTaskListMessageIsAdded()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(Done("ok"));

        await Build(provider).SendAsync("go", CancellationToken.None);

        Assert.DoesNotContain(provider.LastMessages!, m => m.IsTaskList);
    }

    /// <summary>
    /// CLEARING THE LIST REMOVES THE MESSAGE — the transition the "never planned" test above cannot
    /// reach.
    ///
    /// <para>Without this, <c>RemoveAll</c> doing real removal work is verified only by reading the
    /// code: every other test either has a plan throughout or never has one, so a build that placed
    /// the message and then never took it away would pass them all. It is also the resume shape —
    /// the TodoList is not persisted, so a restored conversation arrives holding a plan message with
    /// an empty list behind it, and the stale plan must go rather than linger for the model to act
    /// on.</para>
    /// </summary>
    [Fact]
    public async Task ClearingTheTodos_RemovesTheTaskListMessage()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(TodoCall(("a", "pending")));
        provider.EnqueueResponse(Done("ok"));
        provider.EnqueueResponse(TodoCall());          // an empty list clears it
        provider.EnqueueResponse(Done("ok"));

        var agent = Build(provider);
        await agent.SendAsync("plan it", CancellationToken.None);
        Assert.Contains(provider.LastMessages!, m => m.IsTaskList);

        await agent.SendAsync("never mind", CancellationToken.None);

        Assert.DoesNotContain(provider.LastMessages!, m => m.IsTaskList);
        Assert.DoesNotContain(agent.Context.Messages, m => m.IsTaskList);
    }

    /// <summary>The plan is user-role with no ToolCallId. Both compaction cut paths key on
    /// ToolCallId to keep a tool result with its call; a synthetic result would be the orphan those
    /// walks exist to prevent.</summary>
    [Fact]
    public async Task TheTaskList_IsAUserMessageWithNoToolCallId()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(TodoCall(("a", "pending")));
        provider.EnqueueResponse(Done("ok"));

        await Build(provider).SendAsync("go", CancellationToken.None);

        var plan = Assert.Single(provider.LastMessages!, m => m.IsTaskList);
        Assert.Equal("user", plan.Role);
        Assert.Null(plan.ToolCallId);
    }

    /// <summary>
    /// THE PROPERTY THAT ACTUALLY SAVES THE TIME. A prefix cache matches the longest common prefix
    /// and stops at the first differing byte, so what matters is not merely that the system message
    /// is stable — it is that EVERY message before the plan is unchanged. Only then does a rewritten
    /// plan cost nothing but itself.
    /// </summary>
    [Fact]
    public async Task AStatusFlip_LeavesEveryMessageBeforeThePlanUnchanged()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(TodoCall(("a", "pending")));
        provider.EnqueueResponse(Done("ok"));
        provider.EnqueueResponse(TodoCall(("a", "in_progress")));
        provider.EnqueueResponse(Done("ok"));

        var agent = Build(provider);
        await agent.SendAsync("first", CancellationToken.None);

        var before = provider.LastMessages!
            .TakeWhile(m => !m.IsTaskList)
            .Select(m => $"{m.Role} {m.Content}")
            .ToList();

        await agent.SendAsync("second", CancellationToken.None);

        var after = provider.LastMessages!
            .TakeWhile(m => !m.IsTaskList)
            .Select(m => $"{m.Role} {m.Content}")
            .ToList();

        // The second turn adds real conversation, so `after` grows — but everything the first turn
        // sent must still be there, byte for byte, in the same order.
        Assert.Equal(before, after.Take(before.Count));
    }
}

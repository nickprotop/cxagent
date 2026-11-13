using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A session spawns a worker from an order as the agent tool does from a call — and only an order can say where the
/// worker came from, or ask for the handle it works under.
/// </summary>
public sealed class SpawnOrderTests
{
    private static (SubAgentSpawner Spawner, SubAgentStore Store) Spawner(params string[] answers)
    {
        var provider = new MockLlmProvider();
        foreach (var a in answers) provider.EnqueueResponse(new LlmResponse { Text = a, StopReason = "end_turn" });
        var store = new SubAgentStore();
        var factory = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = provider, Ledger = new TokenLedger(), MaxTurns = 5, ContextWindow = 200_000,
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
        });
        return (new SubAgentSpawner(factory, store: store), store);
    }

    private static ToolCall Call(object args) => new()
    {
        Name = "agent", Id = "c1", Arguments = JsonSerializer.SerializeToElement(args),
    };

    private static SubAgent Child(SubAgentStore store) => new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
    {
        Provider = new MockLlmProvider(), Ledger = new TokenLedger(), MaxTurns = 5,
        Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
    }).Create(new SubAgentFactory.ChildRequest { Origin = "colleague:Charting" });

    [Fact]
    public async Task An_order_spawns_like_the_tool_call()
    {
        var (byCall, callStore) = Spawner("done");
        var (byOrder, orderStore) = Spawner("done");

        var called = await byCall.TryInvokeAsync(Call(new { prompt = "look", description = "Look around", background = false }), null, CancellationToken.None);
        var ordered = await byOrder.SpawnAsync(new SpawnOrder { Prompt = "look", Description = "Look around", Background = false }, null, CancellationToken.None);

        Assert.Equal(SubAgentEnvelope.NameOf(called), SubAgentEnvelope.NameOf(ordered));
        Assert.Equal(SubAgentEnvelope.StateOf(called), SubAgentEnvelope.StateOf(ordered));
        Assert.Equal(callStore.All().Select(a => a.Name), orderStore.All().Select(a => a.Name));
    }

    [Fact]
    public async Task The_agent_tool_ignores_an_origin_argument()
    {
        var (spawner, store) = Spawner("done");

        var envelope = await spawner.TryInvokeAsync(Call(new { prompt = "look", description = "Look", background = false, origin = "colleague:X" }), null, CancellationToken.None);

        var name = SubAgentEnvelope.NameOf(envelope)!;
        Assert.Null(store.OriginOf(name));
        Assert.Null(store.Find(name)!.Agent.Origin);
    }

    [Fact]
    public async Task An_order_with_origin_marks_the_stored_worker()
    {
        var (spawner, store) = Spawner("done");

        var envelope = await spawner.SpawnAsync(new SpawnOrder
        {
            Prompt = "look", Description = "Charting", Background = false, Origin = "colleague:Charting", Handle = "charting",
        }, null, CancellationToken.None);

        Assert.Equal("charting", SubAgentEnvelope.NameOf(envelope));
        Assert.Equal("colleague:Charting", store.OriginOf("charting"));
        Assert.Equal("charting", store.NameOfOrigin("colleague:Charting"));
    }

    /// <summary>
    /// A WORKER OF AN ORIGIN HAS NO TYPE (user, 2026-11-13): config's <c>general</c> briefs every ordinary worker, and
    /// a colleague is not one — nothing of a type reaches its prompt.
    /// </summary>
    [Fact]
    public async Task An_order_with_origin_carries_no_type_s_briefing()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });
        provider.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });
        var factory = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = provider, Ledger = new TokenLedger(), MaxTurns = 5, ContextWindow = 200_000,
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
        });
        var types = new CxAgent.Core.Agents.AgentTypeCatalog(
            new Dictionary<string, AgentTypeConfig> { ["general"] = new("BRIEFED: never edit anything.") }, null);
        var spawner = new SubAgentSpawner(factory, types, new SubAgentStore());
        string SystemSent() => string.Join("\n", provider.LastMessages!.Where(m => m.Role == "system").Select(m => m.Content));

        await spawner.SpawnAsync(new SpawnOrder { Prompt = "look", Description = "Look", Background = false }, null, CancellationToken.None);
        Assert.Contains("BRIEFED", SystemSent());

        await spawner.SpawnAsync(new SpawnOrder
        {
            Prompt = "look", Description = "Charting", Background = false, Origin = "colleague:Charting", Handle = "charting",
        }, null, CancellationToken.None);
        Assert.DoesNotContain("BRIEFED", SystemSent());
    }

    [Fact]
    public void Handle_is_taken_when_free_and_suffixed_when_held_by_an_ordinary_worker()
    {
        var store = new SubAgentStore();
        Assert.Equal("charting", store.Keep(Child(store), "Charting", "charting"));

        var other = new SubAgentStore();
        other.Keep(new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(), Ledger = new TokenLedger(), MaxTurns = 5,
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
        }).Create(), "charting");
        Assert.Equal("charting-2", other.Keep(Child(other), "Charting", "charting"));
    }

    [Fact]
    public void A_tombstone_with_the_same_origin_is_reused()
    {
        var store = new SubAgentStore();
        store.Keep(Child(store), "Charting", "charting");
        Assert.Equal(ForgetOutcome.Forgotten, store.Forget("charting"));

        Assert.Equal("charting", store.Keep(Child(store), "Charting", "charting"));
        Assert.False(store.WasForgotten("charting"));
    }

    [Fact]
    public void A_tombstone_with_another_origin_is_not_reused()
    {
        var store = new SubAgentStore();
        store.Keep(new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(), Ledger = new TokenLedger(), MaxTurns = 5,
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
        }).Create(), "charting");
        store.Forget("charting");

        Assert.Equal("charting-2", store.Keep(Child(store), "Charting", "charting"));
        Assert.True(store.WasForgotten("charting"));
    }
}

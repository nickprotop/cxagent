using System.Text.Json;
using CxAgent.Core.Agents;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The whole point, end to end: spawn a child, then ask THAT child something else and have its own
/// context still be there. A unit test of the store proves naming; only this proves the fridge.
/// </summary>
public class SubAgentFridgeTests
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
            Executors = JobRegistry.CreateWithBuiltins(),
            Ledger = new TokenLedger(),
            MaxTurns = 50,
            CompressAbove = 40_000,
            ContextWindow = 200_000,
        });

    private static ToolCall Spawn(string description, string prompt) =>
        new()
        {
            Id = "call-1",
            Name = "agent",
            Arguments = JsonDocument.Parse(
                JsonSerializer.Serialize(new { description, prompt })).RootElement,
        };

    [Fact]
    public async Task A_spawned_child_is_kept_under_a_name_taken_from_its_description()
    {
        var store = new SubAgentStore();
        var spawner = new SubAgentSpawner(FactoryOver(Answering("found it")), null, store);

        await spawner.TryInvokeAsync(Spawn("review the auth code", "look at auth.cs"),
            onChild: null, CancellationToken.None);

        var kept = Assert.Single(store.All());
        Assert.Equal("review-the-auth-code", kept.Name);
    }

    /// <summary>
    /// THE NAME TRAVELS IN THE ENVELOPE, so the model can send to the child without a listing call.
    /// </summary>
    [Fact]
    public async Task The_spawn_result_names_the_handle_the_child_is_reachable_by()
    {
        var store = new SubAgentStore();
        var spawner = new SubAgentSpawner(FactoryOver(Answering("found it")), null, store);

        var envelope = await spawner.TryInvokeAsync(Spawn("check the parser", "look"),
            onChild: null, CancellationToken.None);

        Assert.Contains("name=\"check-the-parser\"", envelope);
    }

    /// <summary>
    /// THE FRIDGE ITSELF. The second answer is served on the child's OWN context — the one that
    /// already holds its first task and reply — which is what makes waking it cheaper than spawning
    /// a replacement to cover the same ground.
    /// </summary>
    [Fact]
    public async Task A_finished_child_answers_again_on_the_context_it_already_had()
    {
        var store = new SubAgentStore();
        var spawner = new SubAgentSpawner(
            FactoryOver(Answering("the parser is fine", "and the lexer is too")), null, store);
        await spawner.TryInvokeAsync(Spawn("check the parser", "look at the parser"),
            onChild: null, CancellationToken.None);

        var reach = new AgentReachTools(store);
        var answer = await reach.InvokeAsync("agent_send", "check-the-parser",
            "now check the lexer", CancellationToken.None);

        Assert.Equal("and the lexer is too", answer);

        // ITS FIRST TASK IS STILL IN ITS CONTEXT — the whole reason this is not a fresh spawn.
        var kept = store.Find("check-the-parser")!;
        Assert.Contains(kept.Agent.Agent.Context.Messages,
            m => m.Content.Contains("look at the parser"));
    }

    [Fact]
    public async Task Listing_names_a_child_that_was_spawned()
    {
        var store = new SubAgentStore();
        var spawner = new SubAgentSpawner(FactoryOver(Answering("done")), null, store);
        await spawner.TryInvokeAsync(Spawn("review auth", "look"),
            onChild: null, CancellationToken.None);

        var listing = await new AgentReachTools(store)
            .InvokeAsync("agent_list", "", "", CancellationToken.None);

        Assert.Contains("review-auth", listing);
    }

    /// <summary>
    /// A SPAWNER WITH NO STORE KEEPS NOTHING and says so in the envelope by omitting the attribute —
    /// which is what every existing construction site does, unchanged.
    /// </summary>
    [Fact]
    public async Task A_spawner_with_no_store_renders_no_handle()
    {
        var spawner = new SubAgentSpawner(FactoryOver(Answering("done")));

        var envelope = await spawner.TryInvokeAsync(Spawn("review auth", "look"),
            onChild: null, CancellationToken.None);

        Assert.DoesNotContain("name=", envelope);
    }
}

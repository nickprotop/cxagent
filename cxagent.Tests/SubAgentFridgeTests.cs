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

    /// <summary>
    /// A SEND TO A RUNNING CHILD IS DELIVERED, NOT REFUSED — and says so instead of answering, so
    /// the model can tell the two apart without being told which mode it is in.
    /// </summary>
    [Fact]
    public async Task A_send_to_a_busy_child_confirms_delivery_rather_than_answering()
    {
        var store = new SubAgentStore();
        var spawner = new SubAgentSpawner(FactoryOver(Answering("done")), null, store);
        await spawner.TryInvokeAsync(Spawn("check the parser", "look"),
            onChild: null, CancellationToken.None);
        // Claim it, as a send in flight would.
        Assert.True(store.TryBeginSend("check-the-parser"));

        var answer = await new AgentReachTools(store).InvokeAsync(
            "agent_send", "check-the-parser", "the schema changed", CancellationToken.None);

        Assert.Contains("delivered", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("next turn", answer, StringComparison.OrdinalIgnoreCase);
        // AND IT NAMES WHERE THE ANSWER IS NOT, or the model calls again expecting a reply.
        Assert.Contains("will not answer here", answer, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// NOTHING IS STRANDED. A child that finished without draining is IDLE, and idle is the
    /// synchronous path — which empties the mailbox before appending, so the queued message is
    /// delivered by the very next call rather than needing a policy of its own.
    /// </summary>
    [Fact]
    public async Task A_message_queued_while_busy_is_delivered_by_the_next_idle_send()
    {
        var store = new SubAgentStore();
        var spawner = new SubAgentSpawner(
            FactoryOver(Answering("done", "acknowledged")), null, store);
        await spawner.TryInvokeAsync(Spawn("check the parser", "look"),
            onChild: null, CancellationToken.None);

        // Queued while busy, and the child never laps again.
        Assert.True(store.TryBeginSend("check-the-parser"));
        var reach = new AgentReachTools(store);
        await reach.InvokeAsync("agent_send", "check-the-parser", "the schema changed",
            CancellationToken.None);
        store.EndSend("check-the-parser");

        await reach.InvokeAsync("agent_send", "check-the-parser", "anything else?",
            CancellationToken.None);

        // BOTH reached its context, the queued one first.
        var kept = store.Find("check-the-parser")!;
        var texts = kept.Agent.Agent.Context.Messages.Select(m => m.Content).ToList();
        var queued = texts.FindIndex(t => t.Contains("the schema changed"));
        var later = texts.FindIndex(t => t.Contains("anything else?"));
        Assert.True(queued >= 0, "the queued message never arrived");
        Assert.True(queued < later, "the queued message arrived after the later one");
    }

    /// <summary>
    /// THE DRAIN ITSELF: a message left in the mailbox is in front of the model on the agent's next
    /// request, not merely in its context list. MockLlmProvider records what it was sent, which is
    /// the only place that distinction is visible.
    /// </summary>
    [Fact]
    public async Task A_mailbox_message_reaches_the_model_on_the_agents_next_request()
    {
        var provider = Answering("first", "second");
        var store = new SubAgentStore();
        var spawner = new SubAgentSpawner(FactoryOver(provider), null, store);
        await spawner.TryInvokeAsync(Spawn("worker", "do the thing"),
            onChild: null, CancellationToken.None);

        // Left in the mailbox as a mid-run send would leave it, then the agent takes another lap.
        var kept = store.Find("worker")!;
        kept.Agent.Agent.Mailbox.TryEnqueue("STOP: the schema changed", out _);

        await new AgentReachTools(store).InvokeAsync("agent_send", "worker", "carry on",
            CancellationToken.None);

        Assert.NotNull(provider.LastMessages);
        Assert.Contains(provider.LastMessages!,
            m => m.Content.Contains("STOP: the schema changed"));
    }

    /// <summary>
    /// A HANDLE IS CLAIMED BEFORE THE CHILD EXISTS, so the name the store ends up holding is decided
    /// at dispatch rather than at completion — which is what lets agent_list name a child while it is
    /// still running, instead of answering "no sub-agents" for the whole of its run.
    /// </summary>
    [Fact]
    public async Task A_handle_is_reserved_before_the_child_exists()
    {
        var store = new SubAgentStore();
        var name = store.Reserve("survey the notes", "call-1");

        Assert.Equal("survey-the-notes", name);
        // AND THE RESERVATION IS FINDABLE BY THE CALL, which is how the spawner claims it later.
        Assert.Equal("survey-the-notes", store.ReservationFor("call-1"));
    }

    /// <summary>
    /// A RESERVED NAME IS NOT AVAILABLE TO THE NEXT SPAWN. Two children described the same way in
    /// one response would otherwise slug identically, and the second would take the first's handle —
    /// leaving a receipt naming an agent the model can no longer reach.
    /// </summary>
    [Fact]
    public void A_second_reservation_of_the_same_description_is_suffixed()
    {
        var store = new SubAgentStore();

        Assert.Equal("review-auth", store.Reserve("review auth", "c1"));
        Assert.Equal("review-auth-2", store.Reserve("review auth", "c2"));
    }

    /// <summary>
    /// AND THE CHILD ENDS UP UNDER THE NAME THE RECEIPT PUBLISHED, rather than a freshly minted one.
    /// </summary>
    [Fact]
    public async Task A_kept_child_takes_the_handle_that_was_reserved_for_it()
    {
        var store = new SubAgentStore();
        var reserved = store.Reserve("survey the notes", "call-1");
        var spawner = new SubAgentSpawner(FactoryOver(Answering("done")), null, store);

        await spawner.TryInvokeAsync(
            new ToolCall
            {
                Id = "call-1",
                Name = "agent",
                Arguments = JsonSerializer.SerializeToElement(
                    new { description = "survey the notes", prompt = "read them" }),
            },
            onChild: null, CancellationToken.None);

        Assert.NotNull(store.Find(reserved));
        Assert.Single(store.All());
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

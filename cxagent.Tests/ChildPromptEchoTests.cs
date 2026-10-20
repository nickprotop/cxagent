using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What a child is asked shows in its own transcript: the user's words as the user's turn, the parent's as a note
/// from the parent — never the parent's words attributed to the user.
/// </summary>
public class ChildPromptEchoTests
{
    private static (SubAgentStore Store, SubAgent Child) Kept(params string[] answers)
    {
        var provider = new MockLlmProvider();
        foreach (var a in answers) provider.EnqueueResponse(new LlmResponse { Text = a, StopReason = "end_turn" });
        var child = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = provider, Ledger = new TokenLedger(),
            MaxTurns = 5, CompressAbove = 40_000, ContextWindow = 200_000,
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
        }).Create();
        var store = new SubAgentStore();
        store.Keep(child, "x");
        return (store, child);
    }

    private static SendRequest Ask(string prompt, bool fromUser) =>
        new(To: "x", Prompt: prompt, Background: false, ParentAgentId: null, FromUser: fromUser);

    [Fact]
    public async Task AUsersSend_ShowsAsTheUsersTurn()
    {
        var (store, child) = Kept("ok");
        var spy = new ChildFeedTests.Spy();
        child.Feed.Attach(spy.View);

        await new AgentReachTools(store).SendAsync(Ask("look again", fromUser: true), CancellationToken.None);

        Assert.Contains("user:look again", spy.Seen);
    }

    [Fact]
    public async Task TheParentsSend_ShowsAsANoteFromTheParent_NotAsTheUser()
    {
        var (store, child) = Kept("ok");
        var spy = new ChildFeedTests.Spy();
        child.Feed.Attach(spy.View);

        await new AgentReachTools(store).SendAsync(Ask("look again", fromUser: false), CancellationToken.None);

        Assert.Contains(spy.Seen, s => s.StartsWith("said:from the parent agent:", StringComparison.Ordinal)
                                       && s.Contains("look again", StringComparison.Ordinal));
        Assert.DoesNotContain("user:look again", spy.Seen);
    }

    /// <summary>A message for a busy child shows when it is delivered — the moment the sender pressed Enter.</summary>
    [Fact]
    public async Task AMessageForABusyChild_ShowsWhenDelivered()
    {
        var (store, child) = Kept();
        Assert.True(store.TryClaim("x"));
        var spy = new ChildFeedTests.Spy();
        child.Feed.Attach(spy.View);

        var answer = await new AgentReachTools(store).SendAsync(Ask("also check y", fromUser: true), CancellationToken.None);

        Assert.StartsWith("delivered", answer, StringComparison.Ordinal);
        Assert.Contains("user:also check y", spy.Seen);
        store.Release("x");
    }

    /// <summary>A background spawn's brief is the parent's, shown as such — replayed to a view opened later.</summary>
    [Fact]
    public async Task ABackgroundSpawnsBrief_ShowsAsANoteFromTheParent()
    {
        var delivery = new RecordingDelivery();
        var factory = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(), Ledger = new TokenLedger(),
            MaxTurns = 5, CompressAbove = 40_000, ContextWindow = 200_000, Delivery = delivery,
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
        });
        var store = new SubAgentStore();
        var spawner = new SubAgentSpawner(factory, store: store);

        await spawner.TryInvokeAsync(Calls.Spawn(prompt: "map the auth code", background: true), null,
            CancellationToken.None, parentAgentId: "session");
        await delivery.Next();
        var spy = new ChildFeedTests.Spy();
        store.All().Single().Agent.Feed.Attach(spy.View);

        Assert.Contains(spy.Seen, s => s.StartsWith("said:from the parent agent:", StringComparison.Ordinal)
                                       && s.Contains("map the auth code", StringComparison.Ordinal));
    }

    /// <summary>The session's own agent is shown its prompts by the session; echoing them too would show each twice.</summary>
    [Fact]
    public void TheSessionsOwnAgent_EchoesNothing()
    {
        var sink = new BufferedChatSink();
        var agent = new Agent(TestAgents.Setup(new MockLlmProvider(), new(sink, new BufferedJobPanel()), 5));

        agent.EchoPrompt("hi", fromUser: true);
        agent.EchoPrompt("hi", fromUser: false);

        Assert.Equal("", sink.Transcript);
    }
}

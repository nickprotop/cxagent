using CxAgent.Core.Commands;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The `/agents` verbs that reach INSTANCES — what this session spawned — rather than the types it
/// could spawn.
///
/// <para>SEPARATE FROM <see cref="AgentsCommandTests"/> for the reason the two commands are separate:
/// that one lists the kinds of agent config declares, this one lists the ones that were made and
/// carries the send that reaches them. Two subjects that share a word.</para>
/// </summary>
public class SpawnedAgentsCommandTests
{
    /// <summary>A store holding one kept child, optionally already claimed for a send.</summary>
    private static SubAgentStore StoreWith(string description, bool busy)
    {
        var store = new SubAgentStore();
        var name = store.Keep(Spawned(), description);
        if (busy) Assert.True(store.TryBeginSend(name));
        return store;
    }

    private static SubAgent Spawned() =>
        new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new CxAgent.Core.Llm.MockLlmProvider(),
            Executors = CxAgent.Core.Jobs.JobRegistry.CreateWithBuiltins(),
            Ledger = new CxAgent.Core.Llm.TokenLedger(),
            MaxTurns = 50,
            CompressAbove = 40_000,
            ContextWindow = 200_000,
        }).Create(briefing: null, callerContext: null, label: "find thing", type: null,
            parentAgentId: null, turnTools: null);

    /// <summary>
    /// A BUSY AGENT IS SENT TO, NOT REFUSED.
    ///
    /// <para>This command used to answer "'x' is busy with another request. Try again once it
    /// answers." — which made the user the only caller that could not do what <c>agent_send</c>
    /// does. The mailbox exists precisely so a running child can be told something it reads on its
    /// next turn lap, and telling it the moment you know is worth more than telling it after it has
    /// finished working on the wrong thing.</para>
    ///
    /// <para>ASSERTED AS A NULL REFUSAL, because that is the whole contract of this method: null
    /// means the send is about to be attempted, and the send itself decides between answering and
    /// confirming delivery from the claim it takes.</para>
    /// </summary>
    [Fact]
    public void ASendToABusyAgent_IsNotRefused()
    {
        var store = StoreWith("find thing", busy: true);
        var command = new SpawnedAgentsCommand(store);

        var refusal = command.RefuseSend("send find-thing also check the loader",
            out var name, out var prompt);

        Assert.Null(refusal);
        Assert.Equal("find-thing", name);
        Assert.Equal("also check the loader", prompt);
    }

    /// <summary>An idle agent is reached the same way — the busy state changes nothing here.</summary>
    [Fact]
    public void ASendToAnIdleAgent_IsNotRefused()
    {
        var store = StoreWith("find thing", busy: false);
        var command = new SpawnedAgentsCommand(store);

        Assert.Null(command.RefuseSend("send find-thing more please", out _, out _));
    }

    /// <summary>
    /// THE REFUSALS THAT REMAIN ARE THE ONES A SEND CANNOT RECOVER FROM. A handle nothing holds and a
    /// prompt that asks nothing are mistakes to report now, in the same breath as the command — where
    /// busy-ness is a state the mailbox already has an answer for.
    /// </summary>
    [Theory]
    [InlineData("send nobody hello", "no sub-agent named")]
    [InlineData("send find-thing", "asks it nothing")]
    public void ASendThatCannotBeDelivered_IsStillRefused(string arguments, string expected)
    {
        var store = StoreWith("find thing", busy: true);
        var command = new SpawnedAgentsCommand(store);

        var refusal = command.RefuseSend(arguments, out _, out _);

        Assert.NotNull(refusal);
        Assert.Contains(expected, refusal, StringComparison.Ordinal);
    }

    /// <summary>The listing marks a working agent, so a user can see which one their message queues
    /// behind — the state that no longer blocks a send still has to be visible.</summary>
    [Fact]
    public void TheListing_MarksABusyAgent()
    {
        Assert.Contains("busy", new SpawnedAgentsCommand(StoreWith("find thing", busy: true)).Render(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("busy",
            new SpawnedAgentsCommand(StoreWith("find thing", busy: false)).Render(),
            StringComparison.Ordinal);
    }
}

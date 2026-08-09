using CxAgent.Core.Llm;
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
    /// A stub provider answering with plain text and no tool calls, in the style of
    /// <c>TestProviders.cs</c>. Enough responses queued that a prompt-per-test never runs the mock
    /// dry — an empty queue is a different failure and would hide the one being tested.
    /// </summary>
    private static Agent NewAgent(MockLlmProvider? provider = null)
    {
        if (provider is null)
        {
            provider = new MockLlmProvider();
            for (var i = 0; i < 8; i++)
                provider.EnqueueResponse(new LlmResponse
                    { Text = "ok", ToolCalls = [], Usage = new LlmUsage() });
        }

        return new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50);
    }
}

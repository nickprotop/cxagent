using System.Text.Json;
using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A correction typed while a turn is running reaches the model DURING that turn.
///
/// <para>WHY IT MATTERS, measured: a build-error report typed at turn 104 of a live drive was queued
/// behind the turn and delivered at turn 115, by which point the error had been fixed. The agent
/// trusted the text over the clean tree in front of it and spent ~1.9M tokens hunting a phantom.
/// Queueing is correct between turns and wrong during one — the window is where the staleness comes
/// from, so the fix is to close the window rather than to detect it.</para>
/// </summary>
public class SteeringTests
{
    private static JsonElement NoArgs => JsonDocument.Parse("{}").RootElement;

    /// <summary>A response asking for one tool call, then a plain reply to end the turn.</summary>
    private static MockLlmProvider ProviderThatCallsATool()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "",
            ToolCalls = [new ToolCall { Name = "todoread", Id = "call-1", Arguments = NoArgs }],
            Usage = new LlmUsage(),
        });
        provider.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn", Usage = new LlmUsage() });
        return provider;
    }

    private static Agent NewAgent(MockLlmProvider provider, RecordingSink sink) =>
        new(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            sink, new NullJobPanel(), logs: null, maxTurns: 50);

    // THE CORRECTION LANDS IN THE CONVERSATION, in the same turn it was typed. Taken at the tool
    // barrier — after every result is recorded, before the next request is composed — because that
    // is the only point where a user message can legally join: the assistant message above declared
    // a tool call, and a user turn spliced before its result is the orphan shape that 400s a session.
    [Fact]
    public async Task ASteerReachesTheModelWithinTheSameTurn()
    {
        var provider = ProviderThatCallsATool();
        var sink = new RecordingSink();
        var agent = NewAgent(provider, sink);

        var pending = "actually, check the tests first";
        agent.TakePendingSteer = () => { var p = pending; pending = null; return p; };

        await agent.SendAsync("do the thing", CancellationToken.None);

        // The SECOND request is the one composed after the barrier — it must carry the correction.
        Assert.Contains(provider.LastMessages!,
            m => m.Role == "user" && m.Content == "actually, check the tests first");
    }

    // ANNOUNCED, so the transcript can swap its "queued" placeholder for a real user turn. Without
    // this the model changes direction with nothing on screen to say why.
    [Fact]
    public async Task ASteerIsAnnouncedAsAUserTurn()
    {
        var provider = ProviderThatCallsATool();
        var sink = new RecordingSink();
        var agent = NewAgent(provider, sink);

        var pending = "stop and summarise";
        agent.TakePendingSteer = () => { var p = pending; pending = null; return p; };

        await agent.SendAsync("go", CancellationToken.None);

        Assert.Contains("stop and summarise", sink.Users);
    }

    // TAKEN ONCE. The hook clears as it reads, and a turn with several barriers must not deliver the
    // same correction at each of them.
    [Fact]
    public async Task ASteerIsDeliveredExactlyOnce()
    {
        var provider = ProviderThatCallsATool();
        var sink = new RecordingSink();
        var agent = NewAgent(provider, sink);

        var pending = "once";
        agent.TakePendingSteer = () => { var p = pending; pending = null; return p; };

        await agent.SendAsync("go", CancellationToken.None);

        Assert.Single(sink.Users, u => u == "once");
    }

    // NO HOOK, NO CHANGE. A sub-agent has no steer source at all — it was spawned with a brief, and
    // redirecting it mid-flight would mean the parent's account of what it asked for stops matching
    // what happened. This pins that a null hook is an ordinary arrangement, not a crash.
    [Fact]
    public async Task AnAgentWithNoSteerSourceRunsNormally()
    {
        var provider = ProviderThatCallsATool();
        var sink = new RecordingSink();
        var agent = NewAgent(provider, sink);

        var result = await agent.SendAsync("go", CancellationToken.None);

        Assert.NotNull(result);

        // EMPTY, and that is the correct baseline rather than a quirk: Agent does not announce the
        // ORIGINAL prompt — AgentHost does, before handing it over — so every entry here is a steer.
        // Which is what makes the assertions above mean what they say.
        Assert.Empty(sink.Users);
    }
}

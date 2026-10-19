using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Jobs;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A run that produced no answer says so, rather than looking finished.
///
/// <para>MEASURED, NOT THEORISED. A builder sub-agent was mid-implementation when its provider call
/// vanished — its context for the next turn was written, no response arrived, and it returned empty.
/// Its parent read that as "the child returned nothing", guessed at a cause (wrongly: it blamed a
/// plan file the child had read successfully) and re-spawned. The tree was left half-edited in
/// between, and nothing anywhere said the child had died.</para>
/// </summary>
public class SilentRunTests
{
    private static Agent NewAgent(MockLlmProvider provider, RecordingSink sink) =>
        new(provider, JobRegistry.CreateWithBuiltins(), new TokenLedger(),
            sink, new NullJobPanel(), logs: null, maxTurns: 50);

    // AN EMPTY ANSWER IS NOT A COMPLETED RUN. Reported as Completed it was indistinguishable from a
    // finished run with a terse answer, which is exactly the confusion that cost a parent its
    // diagnosis.
    [Fact]
    public async Task AnEmptyAnswerIsReportedAsSilent()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse { Text = "", StopReason = "end_turn" });

        var result = await NewAgent(provider, new RecordingSink()).SendAsync("go", CancellationToken.None);

        Assert.Equal(SendOutcome.Silent, result.Outcome);
    }

    // AND IT IS SAID OUT LOUD. A caller watching the transcript learns the request did not come
    // back; a sub-agent's sink is a buffer nobody reads, which is why the outcome carries it too.
    [Fact]
    public async Task ASilentRunAnnouncesItself()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse { Text = "", StopReason = "end_turn" });

        var sink = new RecordingSink();
        await NewAgent(provider, sink).SendAsync("go", CancellationToken.None);

        Assert.Contains(sink.Errors, e => e.Contains("no answer", StringComparison.OrdinalIgnoreCase));
    }

    // AN ORDINARY ANSWER IS STILL Completed, so the new outcome cannot swallow the common case.
    [Fact]
    public async Task AnAnswerIsStillCompleted()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });

        var result = await NewAgent(provider, new RecordingSink()).SendAsync("go", CancellationToken.None);

        Assert.Equal(SendOutcome.Completed, result.Outcome);
    }

    /// <summary>
    /// WHY IT WAS EMPTY, FROM THE REPLY ITSELF. An empty reply is not always a lost request: a reasoning model can
    /// spend its whole reply thinking, and only a reply with no stop reason looks like a cut connection.
    /// </summary>
    [Theory]
    [InlineData("end_turn", "weighing the options", "reasoned but ended its reply without writing an answer")]
    [InlineData("", null, "ended without a stop reason")]
    [InlineData("end_turn", null, "its reply was empty")]
    public async Task AnEmptyReply_SaysWhy(string stop, string? thinking, string why)
    {
        var provider = new MockLlmProvider();
        // REASONING INLINE, as a local reasoning model sends it: the reply is "<think>…</think>" and nothing after.
        provider.EnqueueResponse(new LlmResponse
        {
            Text = thinking is null ? "" : $"<think>{thinking}</think>", StopReason = stop,
        });
        var sink = new RecordingSink();
        var agent = NewAgent(provider, sink);

        var result = await agent.SendAsync("go", CancellationToken.None);

        Assert.Equal(SendOutcome.Silent, result.Outcome);
        Assert.Contains(why, result.Why, StringComparison.Ordinal);
        Assert.Contains(sink.Errors, e => e.Contains(why, StringComparison.Ordinal));
        Assert.Equal(result.Why, agent.LastEmptyReply);
    }

    /// <summary>The next run that answers clears the reason, so it never describes a later run.</summary>
    [Fact]
    public async Task AnAnswer_ClearsTheLastEmptyReply()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse { Text = "", StopReason = "end_turn" });
        provider.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });
        var agent = NewAgent(provider, new RecordingSink());

        await agent.SendAsync("go", CancellationToken.None);
        await agent.SendAsync("again", CancellationToken.None);

        Assert.Null(agent.LastEmptyReply);
    }

    /// <summary>
    /// THE PARENT'S MODEL IS TOLD WHY, WHAT THE CHILD DID, AND THE CHEAP WAY OUT. A kept child still holds what it
    /// read, so asking it for its answer recovers the work; re-running it redoes every turn.
    /// </summary>
    [Fact]
    public void TheEnvelopeExplainsASilentKeptChild()
    {
        var rendered = SubAgentEnvelope.Render(new EnvelopeChild("child-1", null), "finder",
            new SendResult("", SendOutcome.Silent, "it reasoned but ended its reply without writing an answer"),
            work: "20 tool calls; latest: read a.cs, grep \"Usage\"");

        Assert.Contains("state=\"no-answer\"", rendered);
        Assert.Contains("it reasoned but ended its reply without writing an answer", rendered);
        Assert.Contains("20 tool calls; latest: read a.cs", rendered);
        Assert.Contains("agent_send {\"to\": \"finder\"", rendered);
        Assert.Contains("task_output {\"id\": \"finder\"}", rendered);
        Assert.DoesNotContain("did not come back", rendered);
    }

    /// <summary>A child nothing kept cannot be asked again: the envelope says to check the tree and re-run.</summary>
    [Fact]
    public void TheEnvelopeForAnUnkeptSilentChild_AdvisesARerun()
    {
        var rendered = SubAgentEnvelope.Render(new EnvelopeChild("child-1", null), null,
            new SendResult("", SendOutcome.Silent, "its reply was empty"), work: null);

        Assert.Contains("its reply was empty", rendered);
        Assert.Contains("check the tree", rendered);
        Assert.DoesNotContain("agent_send", rendered);
    }

    /// <summary>
    /// WHERE THE CHILD RAN TRAVELS ON ITS ENVELOPE, which is what the conversation stores: a resumed agent and
    /// an export read it back from there. Absent when not known, so an envelope without it is unchanged.
    /// </summary>
    [Fact]
    public void TheEnvelope_SaysWhereTheChildRan()
    {
        var rendered = SubAgentEnvelope.Render(new EnvelopeChild("child-1", "local:qwen"), "finder",
            new SendResult("found it", SendOutcome.Completed), work: null);
        var receipt = SubAgentEnvelope.Receipt(new EnvelopeChild("child-1", "local:qwen"), "finder", canWrite: false);

        Assert.StartsWith("<sub_agent id=\"child-1\" name=\"finder\" state=\"completed\" runs_on=\"local:qwen\">", rendered);
        Assert.Equal("local:qwen", SubAgentEnvelope.RunsOnOf(rendered));
        Assert.Equal("local:qwen", SubAgentEnvelope.RunsOnOf(receipt));
        Assert.Equal(SubAgentEnvelope.Running, SubAgentEnvelope.StateOf(receipt));

        var unknown = SubAgentEnvelope.Render(new EnvelopeChild("child-1", RunsOn: null), "finder",
            new SendResult("found it", SendOutcome.Completed), work: null);
        Assert.DoesNotContain("runs_on", unknown);
        Assert.Null(SubAgentEnvelope.RunsOnOf(unknown));
    }
}

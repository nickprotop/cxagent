using CxAgent.Core.Agent;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
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
        new(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
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

    // THE PARENT'S MODEL IS TOLD WHAT IT MEANS, in the envelope shape it was told to expect — and
    // told to check the tree, because a child can die having already written files.
    [Fact]
    public void TheEnvelopeExplainsASilentChild()
    {
        var rendered = SubAgentEnvelope.Render("child-1", SendOutcome.Silent, "");

        Assert.Contains("no-answer", rendered);
        Assert.Contains("did not come back", rendered);
        Assert.Contains("check the tree", rendered);
    }
}

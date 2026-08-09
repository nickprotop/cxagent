using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

public class ModelOutputTests
{
    [Fact]
    public void StripReasoning_RemovesABalancedThinkBlock()
    {
        // Seen live: a worker's finished body read literally "</think>". Reasoning tags were never
        // stripped anywhere, so they reached the transcript AND the job's Output -- and JobDigest
        // feeds that same text to the ORCHESTRATOR, so a downstream job consuming
        // {{reviewer.content}} was handed the model's private deliberation as if it were the answer.
        var text = "<think>I should check the obsolete attribute first.</think>Defect 1: a typo.";

        Assert.Equal("Defect 1: a typo.", ModelOutput.StripReasoning(text));
    }

    [Fact]
    public void StripReasoning_HandlesAnUNBALANCEDCloseTag()
    {
        // The exact live case: only the closing tag survived into the visible text. Everything
        // BEFORE it was thought, so the remainder is the answer.
        Assert.Equal("Here are the defects.",
            ModelOutput.StripReasoning("deliberating…</think>Here are the defects."));
    }

    [Fact]
    public void StripReasoning_HandlesAnUNBALANCEDOpenTag()
    {
        // A stream cut mid-thought. Everything AFTER the open tag is thought, so it goes.
        Assert.Equal("Answer.", ModelOutput.StripReasoning("Answer.<think>still reasoning"));
    }

    [Theory]
    [InlineData("A perfectly ordinary review with no tags at all.")]
    [InlineData("")]
    public void StripReasoning_LeavesOrdinaryTextUNTOUCHED(string text)
    {
        // The normal case must not be disturbed -- this runs on every worker turn.
        Assert.Equal(text, ModelOutput.StripReasoning(text));
    }

    [Fact]
    public void ExtractReasoning_ReturnsTheThinkingWhileItIsSTILLOPEN()
    {
        // The mid-stream case, which is the whole point: the opening tag has arrived and the closing
        // one has not, and that is exactly when the model has emitted nothing else.
        var partial = "<think>Checking WrapCellLine for where the flag";
        Assert.Equal("Checking WrapCellLine for where the flag",
            ModelOutput.ExtractReasoning(partial));
    }

    [Fact]
    public void ExtractReasoning_AndStripReasoning_AreComplements()
    {
        const string full = "<think>weighing options</think>The answer is 4.";

        Assert.Equal("weighing options",
            ModelOutput.ExtractReasoning(full));
        Assert.Equal("The answer is 4.",
            ModelOutput.StripReasoning(full).Trim());
    }

    [Fact]
    public void ExtractReasoning_IsEmptyWhenThereIsNoThinking()
    {
        Assert.Equal("", ModelOutput.ExtractReasoning("plain text"));
        Assert.Equal("", ModelOutput.ExtractReasoning(null));
    }
}

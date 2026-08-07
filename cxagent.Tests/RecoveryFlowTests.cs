using CxAgent.Core.Models;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class RecoveryFlowTests
{
    private static AiDiagnosis Diag(RecoveryAction a, string cause = "exit 1: missing file",
        string rationale = "the path does not exist yet") =>
        new(cause, a, rationale, null);

    [Fact]
    public void Describe_LeadsWithTheCause_AndIncludesTheRationale()
    {
        var text = RecoveryFlow.Describe(Diag(RecoveryAction.Retry));

        // The user is being asked to approve someone else's judgement — they need BOTH what went wrong
        // and why this action follows from it, or the confirm prompt is unanswerable.
        Assert.Contains("exit 1: missing file", text);
        Assert.Contains("the path does not exist yet", text);
    }

    [Fact]
    public void Describe_NamesTheSuggestedAction_InPlainLanguage()
    {
        // Not the enum member name — "ModifyAndRetry" is jargon in a confirmation dialog.
        var text = RecoveryFlow.Describe(Diag(RecoveryAction.ModifyAndRetry));
        Assert.DoesNotContain("ModifyAndRetry", text);
        Assert.Contains("modify", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChoicesFor_AlwaysOffersAnEscapeHatch()
    {
        // Whatever the LLM suggests, the user must be able to decline without the goal hanging.
        foreach (var action in Enum.GetValues<RecoveryAction>())
        {
            var choices = RecoveryFlow.ChoicesFor(Diag(action));
            Assert.NotEmpty(choices);
            Assert.Contains(choices, c => c.Contains("Cancel", StringComparison.OrdinalIgnoreCase)
                                       || c.Contains("Do nothing", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ChoicesFor_AskUser_OffersNoAutomaticAction()
    {
        // AskUser means the model explicitly declined to decide — offering "Apply" would be a lie.
        var choices = RecoveryFlow.ChoicesFor(Diag(RecoveryAction.AskUser));
        Assert.DoesNotContain(choices, c => c.Contains("Apply", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DescribeBreach_StatesBothNumbers_AndDoesNotImplyItAlreadyStopped()
    {
        var text = RecoveryFlow.DescribeBreach(totalTokens: 210_000, budget: 200_000);

        Assert.Contains("210", text);       // what was spent
        Assert.Contains("200", text);       // the cap it crossed
        // The goal is PAUSED awaiting a decision, not cancelled — the wording must not say otherwise.
        Assert.DoesNotContain("cancelled", text, StringComparison.OrdinalIgnoreCase);
    }
}

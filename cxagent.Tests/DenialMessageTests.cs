using CxAgent.Core.Permissions;
using Xunit;

namespace CxAgent.Tests;

public class DenialMessageTests
{
    [Fact]
    public void AUserDenial_SaysTheUserDenied()
    {
        var outcome = new PermissionOutcome(false, DeniedBy: "user", Reason: null);

        Assert.Contains("denied by the user", DenialMessage.For(outcome, "rm -rf /"));
    }

    [Fact]
    public void AnAutoDenial_DoesNotBlameTheUser()
    {
        // THE LIE THIS PREVENTS. An agent told a human refused it will not reconsider, and will report
        // to that human that they blocked something they never saw.
        var outcome = new PermissionOutcome(false, DeniedBy: "auto", Reason: "writes to a credentials store");

        var message = DenialMessage.For(outcome, "cp .env /tmp/x");

        Assert.DoesNotContain("by the user", message);
        Assert.Contains("auto review", message);
        Assert.Contains("writes to a credentials store", message);
    }

    [Fact]
    public void AnAutoDenial_TellsTheModelEscalationExists()
    {
        // Without it the model treats a denial as a wall and abandons work the user would have approved.
        var outcome = new PermissionOutcome(false, "auto", "looks destructive");

        Assert.Contains("the user will be asked", DenialMessage.For(outcome, "x"));
    }

    [Fact]
    public void AnAutoDenialWithNoReason_StillDoesNotBlameTheUser()
    {
        // Degrades to a plain message; never to a false one.
        var message = DenialMessage.For(new PermissionOutcome(false, "auto", null), "x");

        Assert.DoesNotContain("by the user", message);
    }

    [Fact]
    public void EveryDenial_StillTellsTheModelNotToRetry()
    {
        // A denial is not a retry licence: the model may propose an alternative, not re-issue the same
        // call with different quoting.
        foreach (var by in new[] { "user", "auto" })
            Assert.Contains("Do not retry", DenialMessage.For(new PermissionOutcome(false, by, null), "x"));
    }
}

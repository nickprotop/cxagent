using CxAgent.Core.Permissions;
using Xunit;

namespace CxAgent.Tests;

public class ClassifierVerdictTests
{
    [Theory]
    [InlineData("ALLOW", ClassifierVerdict.Allow)]
    [InlineData("DENY", ClassifierVerdict.Deny)]
    [InlineData("ASK", ClassifierVerdict.Ask)]
    public void AKnownVerdictParses(string text, ClassifierVerdict expected)
        => Assert.Equal(expected, VerdictParser.Parse(text).Verdict);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("allow")]                       // case matters: a model that lowercased did not answer
    [InlineData("ALLOW, but check the path")]   // qualified
    [InlineData("{\"verdict\":\"allow\"}")]     // answered a different question
    [InlineData("MAYBE")]
    public void AnythingElseIsAsk(string? text)
    {
        // FAIL CLOSED, AND ORDINAL. "ALLOW, but only if you are sure" is a model that did not answer the
        // question asked, and treating it as permission is precisely how a classifier fails open.
        Assert.Equal(ClassifierVerdict.Ask, VerdictParser.Parse(text).Verdict);
    }

    [Fact]
    public void ADenyCarriesItsReason()
    {
        // The reason is the whole value of a reasoning stage: a verdict with no reason makes the model
        // guess what offended, and guessing produces the same call spelled differently.
        var decision = VerdictParser.Parse("DENY: writes to a credentials store outside the project");

        Assert.Equal(ClassifierVerdict.Deny, decision.Verdict);
        Assert.Equal("writes to a credentials store outside the project", decision.Reason);
    }

    [Fact]
    public void AVerdictWithNoReasonHasNullNotEmpty()
        => Assert.Null(VerdictParser.Parse("ALLOW").Reason);
}

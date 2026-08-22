using CxAgent.Core.Commands;
using Xunit;

namespace CxAgent.Tests;

public class CoreMarkdownTests
{
    [Fact]
    public void APlainStringIsAnInfoMessage()
    {
        // THE IMPLICIT CONVERSION IS WHAT KEEPS THE ORDINARY CASE SHORT. Most of Core's lines are
        // neutral, and requiring `new Message(text)` at every one of them would make the common case
        // the noisy one — which is how a severity parameter ends up ignored.
        Message m = "switched to local:qwen";

        Assert.Equal("switched to local:qwen", m.Text);
        Assert.Equal(Severity.Info, m.Severity);
    }

    [Fact]
    public void ToneIsStatedWhenItMatters()
    {
        var m = new Message("could not save this rule", Severity.Warning);

        Assert.Equal(Severity.Warning, m.Severity);
    }
}

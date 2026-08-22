using CxAgent.Core.Commands;
using CxAgent.Core.Sessions;
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

    [Theory]
    [InlineData("my_test_file.cs", @"my\_test\_file.cs")]
    [InlineData("a*b", @"a\*b")]
    [InlineData("`code`", @"\`code\`")]
    [InlineData("[link]", @"\[link\]")]
    public void InterpolatedValuesKeepTheirCharacters(string raw, string escaped)
    {
        // A PATH IS NOT EMPHASIS. `my_test_file.cs` interpolated raw into markdown renders as
        // my<i>test</i>file.cs — the same class of bug the old Markup.Escape existed to prevent, in
        // the new format. Core interpolates paths, error text and model output constantly.
        Assert.Equal(escaped, Md.Escape(raw));
    }

    [Fact]
    public void OrdinaryTextIsUntouched()
    {
        Assert.Equal("could not read the file", Md.Escape("could not read the file"));
    }

    [Fact]
    public void ErrorsIsTheMessagesAtErrorSeverity()
    {
        // WAS A PARALLEL CHANNEL, NOW A FILTER. `Failed` routed into its own list, which is why it
        // survived as a second method — but that list has 29 references and every one is a test
        // using it as an assertion handle. Derived from severity, it answers the same question
        // without the contract carrying a method to ask it.
        var sink = new BufferedChatSink();

        sink.Said("a mode change");
        sink.Said(new("could not save this rule", Severity.Warning));
        sink.Said(new("the model returned no answer", Severity.Error));

        Assert.Single(sink.Errors);
        Assert.Contains("no answer", sink.Errors[0]);

        // A WARNING IS NOT A FAULT. A caller checking for failures must not find one here — the same
        // distinction Notices and Errors were kept apart for.
        Assert.DoesNotContain(sink.Errors, e => e.Contains("could not save"));
    }
}

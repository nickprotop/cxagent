using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The detail header already names the plugin; its README's own title would say it twice.</summary>
public class ReadmeTitleTests
{
    [Theory]
    [InlineData("# calculator\n\nText.", "calculator", "Text.")]
    [InlineData("# Calculator\n\nText.", "calculator", "Text.")]
    [InlineData("\n# calculator\nText.", "calculator", "Text.")]
    public void ATitleThatIsThePluginsName_IsDropped(string md, string name, string expected) =>
        Assert.Equal(expected, ReadmeTitle.Drop(md, name));

    [Theory]
    [InlineData("# Calculator plugin for agents\n\nText.", "calculator")]
    [InlineData("## calculator\n\nText.", "calculator")]
    [InlineData("Intro.\n\n# calculator", "calculator")]
    public void AnyOtherOpening_StaysAsWritten(string md, string name) =>
        Assert.Equal(md, ReadmeTitle.Drop(md, name));

    [Fact]
    public void AStorePlugin_MatchesOnItsBareName() =>
        Assert.Equal("Text.", ReadmeTitle.Drop("# calculator\n\nText.", "acme/calculator"));
}

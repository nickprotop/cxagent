using CxAgent.UI.Config;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The Providers tab's Images row: its first item says what asking the server gave, and each item maps to
/// the setting it writes — asking writes none, Yes and No pin it.
/// </summary>
public class ProvidersTabImagesTests
{
    [Theory]
    [InlineData(true, "Ask the server: yes")]
    [InlineData(false, "Ask the server: no")]
    [InlineData(null, "Ask the server: the server did not say")]
    public void TheFirstItem_SaysWhatTheServerSaid(bool? said, string item)
    {
        Assert.Equal(item, ProvidersTab.ImagesAsk(said));
    }

    [Fact]
    public void TheFirstItem_WhileAsking_SaysSo()
    {
        Assert.Equal("Ask the server: …", ProvidersTab.ImagesAsk(null, asking: true));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public void EachSetting_HasItsItem_AndEachItemWritesItsSetting(bool? images, int index)
    {
        Assert.Equal(index, ProvidersTab.ImagesIndex(images));
        Assert.Equal(images, ProvidersTab.ImagesChoice(index));
    }
}

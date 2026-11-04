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

    private static readonly CxAgent.Core.Llm.ProviderInstanceConfig Local =
        new("openai-compatible", "qwen3", null, "http://localhost:8771/v1", null);

    [Theory]
    [InlineData(null, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public void TheRow_OpensOnTheSavedSetting_WhileTheServerIsAsked(bool? saved, int selected)
    {
        var dropdown = ProvidersTab.ImagesDropdown(new InstanceDraft("local", Local with { Images = saved }), () => { });

        Assert.Equal(["Ask the server: …", "Yes", "No"], dropdown.StringItems);
        Assert.Equal(selected, dropdown.SelectedIndex);
    }

    [Fact]
    public void Choosing_WritesTheDraft_AndSaysItChanged()
    {
        var draft = new InstanceDraft("local", Local);
        var changed = 0;
        var dropdown = ProvidersTab.ImagesDropdown(draft, () => changed++);

        dropdown.SelectedIndex = 2;

        Assert.False(draft.Images);
        Assert.Equal(1, changed);
    }

    /// <summary>
    /// The server's answer names the first item, pinned or not: a pin of No beside "the server says yes"
    /// is the one place that disagreement shows.
    /// </summary>
    [Fact]
    public void TheServersAnswer_NamesTheFirstItem_AndKeepsTheChoice()
    {
        var draft = new InstanceDraft("local", Local with { Images = false });
        var changed = 0;
        var dropdown = ProvidersTab.ImagesDropdown(draft, () => changed++);

        ProvidersTab.ShowServerAnswer(dropdown, said: true);

        Assert.Equal(["Ask the server: yes", "Yes", "No"], dropdown.StringItems);
        Assert.Equal(2, dropdown.SelectedIndex);
        Assert.False(draft.Images);
        Assert.Equal(0, changed);
    }
}

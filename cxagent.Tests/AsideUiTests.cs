using CxAgent.Core.Sessions;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class AsideUiTests
{
    /// <summary>The no-cache hint follows the model name, and only when asked for.</summary>
    [Fact]
    public void TheModeLine_CarriesTheCacheHint_AfterTheModel()
    {
        var plain = MainWindow.ModeLineTextForTest(WorkingMode.Default, "anthropic:claude");
        var off = MainWindow.ModeLineTextForTest(WorkingMode.Default, "anthropic:claude", cacheOff: true);

        Assert.DoesNotContain(MainWindow.CacheOffHint, plain);
        Assert.True(off.IndexOf("anthropic:claude", StringComparison.Ordinal)
                    < off.IndexOf(MainWindow.CacheOffHint, StringComparison.Ordinal));
    }

    /// <summary>
    /// Only an Anthropic instance without cacheControl is uncached. A local server caches by itself,
    /// so it must never be told to turn something on.
    /// </summary>
    [Fact]
    public void PromptCacheOff_OnlyForAnAnthropicInstanceWithoutCacheControl()
    {
        CxAgent.Core.Llm.ILlmProvider Anthropic(bool cache) =>
            new CxAgent.Core.Llm.Providers.AnthropicProvider("a", "a", "claude-x", "k", cacheControl: cache);

        Assert.True(MainWindow.PromptCacheOff(CxAgent.Core.Llm.ResolvedConfig.ForTesting(Anthropic(false))));
        Assert.False(MainWindow.PromptCacheOff(CxAgent.Core.Llm.ResolvedConfig.ForTesting(Anthropic(true))));
        Assert.False(MainWindow.PromptCacheOff(CxAgent.Core.Llm.ResolvedConfig.ForTesting(new CxAgent.Core.Llm.MockLlmProvider())));
    }
}

// IN THE file-tabs COLLECTION, which runs alone: the grip's colour is read from the static
// ColorScheme, and ColorSchemeTests running beside it switches the theme between the two reads.
[Collection("file-tabs")]
public class AsideComposerTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void BtwMode_RecoloursTheGrip_AndLeavingRestoresIt()
    {
        var main = _fixture.Host.Main;
        var session = main.ActiveSession!;
        var before = main.GripTextForTest();

        main.SetAside(session, true);
        Assert.NotEqual(before, main.GripTextForTest());

        main.SetAside(session, false);
        Assert.Equal(before, main.GripTextForTest());
    }

    /// <summary>
    /// The rule above the composer carries the keys about the conversation: "F12 export" at its right, and in btw mode
    /// the way out at its left — one title at a time, on the line that already marks where the composer starts, not
    /// crowding the mode line after the model.
    /// </summary>
    [Fact]
    public void TheRuleAboveTheComposer_SaysF12Export_AndBtwModeTakesItOver()
    {
        var main = _fixture.Host.Main;
        var session = main.ActiveSession!;
        Assert.Contains("F12 export", main.ComposerRuleTitleForTest());
        Assert.Equal(SharpConsoleUI.Layout.TextJustification.Right, main.ComposerRuleAlignmentForTest());

        main.SetAside(session, true);
        Assert.Contains(MainWindow.AsideHint, main.ComposerRuleTitleForTest());
        Assert.DoesNotContain("F12", main.ComposerRuleTitleForTest());
        Assert.Equal(SharpConsoleUI.Layout.TextJustification.Left, main.ComposerRuleAlignmentForTest());
        Assert.DoesNotContain(MainWindow.AsideHint, main.ModeLineForTest());

        main.SetAside(session, false);
        Assert.Contains("F12 export", main.ComposerRuleTitleForTest());
        Assert.Equal(SharpConsoleUI.Layout.TextJustification.Right, main.ComposerRuleAlignmentForTest());
    }
}

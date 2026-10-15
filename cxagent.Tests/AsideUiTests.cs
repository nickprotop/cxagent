using CxAgent.Core.Sessions;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class AsideUiTests
{
    /// <summary>The hint sits after the model name, and only in btw mode.</summary>
    [Fact]
    public void TheModeLine_CarriesTheHint_AfterTheModel_OnlyInBtwMode()
    {
        var plain = MainWindow.ModeLineTextForTest(WorkingMode.Default, "local:qwen", aside: false);
        var btw = MainWindow.ModeLineTextForTest(WorkingMode.Default, "local:qwen", aside: true);

        Assert.DoesNotContain(MainWindow.AsideHint, plain);
        Assert.Contains(MainWindow.AsideHint, btw);
        Assert.True(btw.IndexOf("local:qwen", StringComparison.Ordinal)
                    < btw.IndexOf(MainWindow.AsideHint, StringComparison.Ordinal));
    }

    /// <summary>The no-cache hint follows the model name, and only when asked for.</summary>
    [Fact]
    public void TheModeLine_CarriesTheCacheHint_AfterTheModel()
    {
        var plain = MainWindow.ModeLineTextForTest(WorkingMode.Default, "anthropic:claude", aside: false);
        var off = MainWindow.ModeLineTextForTest(WorkingMode.Default, "anthropic:claude", aside: false, cacheOff: true);

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
}

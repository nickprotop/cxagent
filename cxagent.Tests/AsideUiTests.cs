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
}

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

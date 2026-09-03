using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class OpenAfterRefusalTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    // A REFUSAL MUST NOT POISON THE REGISTRY. ShowRefusal registers the path so its tab has a title,
    // but a later /open of a DIFFERENT file must still work — and reopening the refused one must not
    // find a title with no state behind it.
    [Fact]
    public void AFileOpensAfterAnotherWasRefused()
    {
        var bad = Path.Combine(_fixture.WorkingDirectory, "bad.bin");
        File.WriteAllBytes(bad, new byte[] { 0x01, 0x00, 0x02 });
        var good = Path.Combine(_fixture.WorkingDirectory, "good.cs");
        File.WriteAllText(good, "class A { }\n");

        FileTab.ShowRefusal(_fixture.Host, bad, "bad.bin looks binary, so it is not shown.");
        FileTab.Open(_fixture.Host, FileLoad.TryLoad(good, out _)!);

        Assert.Contains(_fixture.Host.Main.Tabs.TabTitles, t => t.Contains("good.cs"));
    }

    // AND THE REFUSED FILE CAN BE ASKED FOR AGAIN — it switches to the tab already showing why.
    [Fact]
    public void ARefusedFileCanBeAskedForTwice()
    {
        var bad = Path.Combine(_fixture.WorkingDirectory, "twice.bin");
        File.WriteAllBytes(bad, new byte[] { 0x01, 0x00 });

        FileTab.ShowRefusal(_fixture.Host, bad, "refused");
        var after1 = _fixture.Host.Main.Tabs.TabCount;
        FileTab.ShowRefusal(_fixture.Host, bad, "refused");

        Assert.Equal(after1, _fixture.Host.Main.Tabs.TabCount);
    }
}

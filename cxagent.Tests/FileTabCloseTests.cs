using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class FileTabCloseTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private LoadedFile Open(string name)
    {
        var path = Path.Combine(_fixture.WorkingDirectory, name);
        File.WriteAllText(path, "a\n");
        var loaded = FileLoad.TryLoad(path, out _)!;
        FileTab.Open(_fixture.Host, loaded);
        return loaded;
    }

    [Fact]
    public void CloseRequest_OnACleanBuffer_ClosesWithoutAsking()
    {
        Open("clean.cs");
        var before = _fixture.Host.Main.Tabs.TabCount;

        FileTab.RequestCloseForTest(_fixture.Host, "clean.cs");

        Assert.Equal(before - 1, _fixture.Host.Main.Tabs.TabCount);
        Assert.Equal(0, FileTab.PendingConfirmationsForTest(_fixture.Host));
    }

    // ONE DIALOG, NOT ONE PER ATTEMPT. A close request arrives on every attempt; without a guard the
    // second press stacks a second dialog behind the first and looks like a dead button.
    [Fact]
    public void CloseRequest_Twice_OnAModifiedBuffer_AsksOnce()
    {
        Open("dirty.cs");
        FileTab.MarkModifiedForTest(_fixture.Host, "dirty.cs");

        FileTab.RequestCloseForTest(_fixture.Host, "dirty.cs");
        FileTab.RequestCloseForTest(_fixture.Host, "dirty.cs");

        Assert.Equal(1, FileTab.PendingConfirmationsForTest(_fixture.Host));
    }

    // THE GUARD IS PER TAB AND COVERS ALL THREE QUESTIONS. A save-confirm and a close-confirm are
    // different questions about the same tab, and stacking them is the shell window's bug in a new
    // costume.
    [Fact]
    public void ASaveConfirmation_AndACloseRequest_DoNotStack()
    {
        Open("both.cs");
        FileTab.MarkModifiedForTest(_fixture.Host, "both.cs");
        FileTab.MarkExternallyChangedForTest(_fixture.Host, "both.cs");

        FileTab.RequestSaveForTest(_fixture.Host, "both.cs");
        FileTab.RequestCloseForTest(_fixture.Host, "both.cs");

        Assert.Equal(1, FileTab.PendingConfirmationsForTest(_fixture.Host));
    }

    // A MODIFIED BUFFER IS NOT CLOSED BY THE QUESTION ITSELF — only by an answer to it.
    [Fact]
    public void CloseRequest_OnAModifiedBuffer_LeavesTheTabUp()
    {
        Open("stay.cs");
        FileTab.MarkModifiedForTest(_fixture.Host, "stay.cs");
        var before = _fixture.Host.Main.Tabs.TabCount;

        FileTab.RequestCloseForTest(_fixture.Host, "stay.cs");

        Assert.Equal(before, _fixture.Host.Main.Tabs.TabCount);
    }
}

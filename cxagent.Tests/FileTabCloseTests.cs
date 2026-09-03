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

public class FileWatchTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private string Open(string name, string text = "one\n")
    {
        var path = Path.Combine(_fixture.WorkingDirectory, name);
        File.WriteAllText(path, text);
        FileTab.Open(_fixture.Host, FileLoad.TryLoad(path, out _)!);
        return path;
    }

    // A CLEAN BUFFER CATCHES UP. Showing stale content is a lie with no cost to fixing.
    [Fact]
    public void ACleanBuffer_ReloadsInPlace()
    {
        var path = Open("clean-reload.cs");
        File.WriteAllText(path, "two\n");

        FileTab.RaiseChangedForTest(_fixture.Host, path);

        Assert.Equal("two\n", FileTab.ContentForTest(_fixture.Host, "clean-reload.cs"));
    }

    // A MODIFIED BUFFER IS NEVER OVERWRITTEN BY A PROGRAM. The edits stay and the reload becomes
    // something the user asks for.
    [Fact]
    public void AModifiedBuffer_KeepsItsEdits()
    {
        var path = Open("dirty-keep.cs");
        FileTab.SetContentForTest(_fixture.Host, "dirty-keep.cs", "mine\n");
        File.WriteAllText(path, "theirs\n");

        FileTab.RaiseChangedForTest(_fixture.Host, path);

        Assert.Equal("mine\n", FileTab.ContentForTest(_fixture.Host, "dirty-keep.cs"));
    }

    // AND THE SAVE GATE ARMS, so the next save asks before discarding what the agent wrote.
    [Fact]
    public void AModifiedBuffer_ArmsTheSaveGate()
    {
        var path = Open("dirty-gate.cs");
        FileTab.SetContentForTest(_fixture.Host, "dirty-gate.cs", "mine\n");
        File.WriteAllText(path, "theirs\n");

        FileTab.RaiseChangedForTest(_fixture.Host, path);

        Assert.True(FileTab.ExternallyChangedForTest(_fixture.Host, "dirty-gate.cs"));
    }

    // DELETION IS NOT A QUESTION: the buffer is all that is left of the file, and Save recreates it.
    [Fact]
    public void ADeletedFile_KeepsTheBufferAndAsksNothing()
    {
        var path = Open("gone.cs");
        File.Delete(path);

        FileTab.RaiseChangedForTest(_fixture.Host, path);

        Assert.Equal("one\n", FileTab.ContentForTest(_fixture.Host, "gone.cs"));
        Assert.Equal(0, FileTab.PendingConfirmationsForTest(_fixture.Host));
    }
}

public class ReloadFidelityTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    // A RELOAD MUST NOT CHANGE THE TEXT. The watcher fires on any write in the working directory, so
    // this runs on ordinary use — and a reload that prepends a line puts a blank row above line 1 and
    // makes every line number wrong from then on.
    [Fact]
    public void ReloadingDoesNotChangeTheContent()
    {
        var path = Path.Combine(_fixture.WorkingDirectory, "fidelity.cs");
        File.WriteAllText(path, "using System;\nclass A { }\n");
        FileTab.Open(_fixture.Host, FileLoad.TryLoad(path, out _)!);

        Assert.Equal("using System;\nclass A { }\n",
            FileTab.ContentForTest(_fixture.Host, "fidelity.cs"));

        FileTab.RaiseChangedForTest(_fixture.Host, path);

        Assert.Equal("using System;\nclass A { }\n",
            FileTab.ContentForTest(_fixture.Host, "fidelity.cs"));
    }
}

using CxAgent.Core.Jobs.Builtin;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class FileSaveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cxagent-filesave").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // THE PATH AND NOTHING ELSE. A diff would be more useful and more tokens; the model can read the
    // file if it cares.
    [Fact]
    public void SaveMessage_NamesTheFileOnly()
    {
        var path = Path.Combine(_dir, "UI", "ShellWindow.cs");

        var msg = FileTab.SaveMessage(path, _dir);

        Assert.Equal($"[cxagent] the user edited {Path.Combine("UI", "ShellWindow.cs")}", msg);
        Assert.DoesNotContain("\n", msg);
    }

    // A path outside the working directory has no relative form worth showing.
    [Fact]
    public void SaveMessage_KeepsAnOutsidePathAbsolute()
    {
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "x.cs");

        Assert.Contains(Path.GetFullPath(outside), FileTab.SaveMessage(outside, _dir));
    }

    // THE GATE IS THE EXCEPTION, NOT A STEP ON EVERY WRITE. A confirmation on every save would train
    // the user to dismiss it, which is exactly how the one that matters gets clicked through.
    [Fact]
    public void SaveNeedsNoConfirmation_WithoutTheWarning()
        => Assert.False(FileTab.SaveNeedsConfirmationForTest(externallyChanged: false));

    // The mirror of "a modified buffer must never be overwritten by a program": a program's file must
    // not be silently overwritten by a stale buffer.
    [Fact]
    public void SaveAsksFirst_WhenTheAgentChangedItUnderneath()
        => Assert.True(FileTab.SaveNeedsConfirmationForTest(externallyChanged: true));

    // A SAVE MUST NOT CHANGE BYTES THE USER DID NOT EDIT. FileMutation.WriteAsync restores both
    // conventions from the snapshot; this pins that the editor's own load produces a snapshot that
    // actually carries them.
    [Fact]
    public async Task Save_KeepsTheBomAndTheLineEndings()
    {
        var path = Path.Combine(_dir, "a.txt");
        var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat("x\r\ny\r\n"u8.ToArray()).ToArray();
        await File.WriteAllBytesAsync(path, original);

        var loaded = FileLoad.TryLoad(path, out _)!;
        await FileMutation.WriteAsync(path, loaded.Text.Replace("x", "z"), loaded.Snapshot,
            CancellationToken.None);

        var after = await File.ReadAllBytesAsync(path);
        Assert.Equal(0xEF, after[0]);
        Assert.Contains("\r\n", await File.ReadAllTextAsync(path));
    }
}

public class SaveGateTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private string Open(string name)
    {
        var path = Path.Combine(_fixture.WorkingDirectory, name);
        File.WriteAllText(path, "mine\n");
        FileTab.Open(_fixture.Host, FileLoad.TryLoad(path, out _)!);
        return path;
    }

    // THE GATE ASKS RATHER THAN WRITING. The mirror of "a modified buffer is never overwritten by a
    // program": a program's file is not silently overwritten by a stale buffer either.
    [Fact]
    public void SavingAWarnedTabAsksAndWritesNothing()
    {
        var path = Open("gated.txt");
        FileTab.MarkExternallyChangedForTest(_fixture.Host, "gated.txt");
        File.WriteAllText(path, "theirs\n");

        FileTab.RequestSaveForRealTest(_fixture.Host, "gated.txt");

        Assert.Equal(1, FileTab.PendingConfirmationsForTest(_fixture.Host));
        Assert.Equal("theirs\n", File.ReadAllText(path));
    }

    // AND A PLAIN SAVE STILL JUST WRITES.
    [Fact]
    public async Task SavingAnUnwarnedTabWritesWithoutAsking()
    {
        var path = Open("plain.txt");
        FileTab.SetContentForTest(_fixture.Host, "plain.txt", "changed\n");

        await FileTab.SaveForTest(_fixture.Host, "plain.txt");

        Assert.Equal(0, FileTab.PendingConfirmationsForTest(_fixture.Host));
        Assert.Equal("changed\n", File.ReadAllText(path));
    }

    // THE WARNING IS SPENT BY THE WRITE. Left set, it would gate the next save over a conflict that
    // no longer exists.
    [Fact]
    public async Task WritingClearsTheWarning()
    {
        Open("spent.txt");
        FileTab.MarkExternallyChangedForTest(_fixture.Host, "spent.txt");

        await FileTab.SaveForTest(_fixture.Host, "spent.txt");

        Assert.False(FileTab.ExternallyChangedForTest(_fixture.Host, "spent.txt"));
    }
}

public class SeeTheirsTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    // ONE IMPLEMENTATION, TWO WAYS IN: the toolbar button and the save dialog both land here, so the
    // read-only rule is kept in one place rather than forgotten in a second.
    [Fact]
    public void SeeTheirs_OpensTheDiskVersionReadOnly()
    {
        var path = Path.Combine(_fixture.WorkingDirectory, "theirs.txt");
        File.WriteAllText(path, "mine\n");
        FileTab.Open(_fixture.Host, FileLoad.TryLoad(path, out _)!);

        FileTab.SetContentForTest(_fixture.Host, "theirs.txt", "my edit\n");
        File.WriteAllText(path, "their edit\n");

        FileTab.ShowTheirsForTest(_fixture.Host, "theirs.txt");

        // A second tab, showing what is on disk — not what is in the buffer.
        Assert.Contains(_fixture.Host.Main.Tabs.TabTitles, t => t.Contains("on disk"));
        Assert.Equal("my edit\n", FileTab.ContentForTest(_fixture.Host, "theirs.txt"));
    }

    // ASKED FOR TWICE, ONE TAB. The same rule the refusal tabs needed.
    [Fact]
    public void SeeTheirs_Twice_DoesNotOpenTwoTabs()
    {
        var path = Path.Combine(_fixture.WorkingDirectory, "once.txt");
        File.WriteAllText(path, "x\n");
        FileTab.Open(_fixture.Host, FileLoad.TryLoad(path, out _)!);

        FileTab.ShowTheirsForTest(_fixture.Host, "once.txt");
        var after1 = _fixture.Host.Main.Tabs.TabCount;
        FileTab.ShowTheirsForTest(_fixture.Host, "once.txt");

        Assert.Equal(after1, _fixture.Host.Main.Tabs.TabCount);
    }
}

public class DeletedFileSaveTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    // THE TOOLBAR PROMISES "Save recreates it", so Save must recreate it. Deletion must not arm the
    // save gate: that gate protects a version on disk from a stale buffer, and a deleted file has no
    // version to protect — arming it makes Save ask about a conflict that cannot exist and write
    // nothing, while the toolbar says otherwise.
    [Fact]
    public async Task SavingADeletedFileRecreatesIt()
    {
        var path = Path.Combine(_fixture.WorkingDirectory, "recreate.txt");
        File.WriteAllText(path, "still here\n");
        FileTab.Open(_fixture.Host, FileLoad.TryLoad(path, out _)!);

        File.Delete(path);
        FileTab.RaiseChangedForTest(_fixture.Host, path);

        Assert.False(FileTab.ExternallyChangedForTest(_fixture.Host, "recreate.txt"));

        await FileTab.SaveForTest(_fixture.Host, "recreate.txt");

        Assert.True(File.Exists(path));
        Assert.Equal("still here\n", await File.ReadAllTextAsync(path));
    }
}

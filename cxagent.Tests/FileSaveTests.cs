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

using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

[Collection("file-tabs")]
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

[Collection("file-tabs")]
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

[Collection("file-tabs")]
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

[Collection("file-tabs")]
public class FileTabThemeTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    // COLOURS ARE CAPTURED BY VALUE when the editor is built, so a theme switch leaves an open file
    // painted in the outgoing theme unless something goes back and re-colours it. MainWindow's grips
    // and mode line carry the same note; this is the same bug in a new surface.
    [Fact]
    public void AThemeSwitch_RecoloursAnOpenEditor()
    {
        var path = Path.Combine(_fixture.WorkingDirectory, "themed.cs");
        File.WriteAllText(path, "class A { }\n");
        FileTab.Open(_fixture.Host, FileLoad.TryLoad(path, out _)!);

        var before = FileTab.EditorBackgroundForTest(_fixture.Host, "themed.cs");

        // A theme with a different window background, then the window reapplies. Same construction
        // as ColorSchemeTests uses.
        var other = SharpConsoleUI.Themes.Theme.From(new SharpConsoleUI.Themes.ModernGrayTheme())
            .WithName("test-pale")
            .With(t => t.WindowBackgroundColor = new SharpConsoleUI.Color(0xf5, 0xf5, 0xf5))
            .Build();
        ColorScheme.DeriveFrom(other);
        FileTab.ReapplyTheme(_fixture.Host.Main);

        var after = FileTab.EditorBackgroundForTest(_fixture.Host, "themed.cs");

        Assert.NotEqual(before, after);
        Assert.Equal(ColorScheme.ChatSurface, after);
    }
}

[Collection("file-tabs")]
public class ModifiedMarkerTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    // TYPING MARKS THE TAB. Refresh runs on open and on watcher events, so without a content hook the
    // tab reads "3 lines" with no bullet while the user edits — and the close confirmation reads the
    // same state, so unsaved edits would be discarded without a question.
    [Fact]
    public void TypingMarksTheTabModified()
    {
        var path = Path.Combine(_fixture.WorkingDirectory, "marker.txt");
        File.WriteAllText(path, "one\ntwo\n");
        FileTab.Open(_fixture.Host, FileLoad.TryLoad(path, out _)!);

        Assert.DoesNotContain(_fixture.Host.Main.Tabs.TabTitles, t => t.Contains('•'));

        FileTab.SetContentForTest(_fixture.Host, "marker.txt", "one\ntwo\nthree\n");

        Assert.Contains(_fixture.Host.Main.Tabs.TabTitles, t => t.Contains('•'));
    }
}

[Collection("file-tabs")]
public class CloseAnswersTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private string Open(string name)
    {
        var path = Path.Combine(_fixture.WorkingDirectory, name);
        File.WriteAllText(path, "kept\n");
        FileTab.Open(_fixture.Host, FileLoad.TryLoad(path, out _)!);
        return path;
    }

    // DISCARD INJECTS NOTHING AND WRITES NOTHING — nothing happened to the file, so there is nothing
    // to tell the model and nothing to save.
    [Fact]
    public void DiscardClosesAndLeavesTheFileAlone()
    {
        var path = Open("discard.txt");
        FileTab.SetContentForTest(_fixture.Host, "discard.txt", "thrown away\n");
        var before = _fixture.Host.Main.Tabs.TabCount;

        FileTab.RequestCloseForTest(_fixture.Host, "discard.txt");
        FileTab.AnswerForTest(_fixture.Host, "discard.txt", "Discard");

        Assert.Equal(before - 1, _fixture.Host.Main.Tabs.TabCount);
        Assert.Equal("kept\n", File.ReadAllText(path));
    }

    // CANCEL LEAVES EVERYTHING AS IT WAS: the tab stays, the edits stay, the file is untouched.
    [Fact]
    public void CancelLeavesTheTabAndTheEdits()
    {
        var path = Open("cancel.txt");
        FileTab.SetContentForTest(_fixture.Host, "cancel.txt", "still mine\n");
        var before = _fixture.Host.Main.Tabs.TabCount;

        FileTab.RequestCloseForTest(_fixture.Host, "cancel.txt");
        FileTab.AnswerForTest(_fixture.Host, "cancel.txt", "Cancel");

        Assert.Equal(before, _fixture.Host.Main.Tabs.TabCount);
        Assert.Equal("still mine\n", FileTab.ContentForTest(_fixture.Host, "cancel.txt"));
        Assert.Equal("kept\n", File.ReadAllText(path));
    }

    // AND THE GUARD RELEASES, so a cancelled question can be asked again rather than the tab being
    // stuck unable to raise another.
    [Fact]
    public void CancellingReleasesTheGuard()
    {
        Open("again.txt");
        FileTab.SetContentForTest(_fixture.Host, "again.txt", "edited\n");

        FileTab.RequestCloseForTest(_fixture.Host, "again.txt");
        FileTab.AnswerForTest(_fixture.Host, "again.txt", "Cancel");

        Assert.Equal(0, FileTab.PendingConfirmationsForTest(_fixture.Host));

        FileTab.RequestCloseForTest(_fixture.Host, "again.txt");
        Assert.Equal(1, FileTab.PendingConfirmationsForTest(_fixture.Host));
    }
}

[Collection("file-tabs")]
public class ReloadDialogTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private string Open(string name)
    {
        var path = Path.Combine(_fixture.WorkingDirectory, name);
        File.WriteAllText(path, "on disk\n");
        FileTab.Open(_fixture.Host, FileLoad.TryLoad(path, out _)!);
        return path;
    }

    // RELOADING A MODIFIED BUFFER IS THE SAME LOSS AS CLOSING ONE, so it asks the same way.
    [Fact]
    public void ReloadingAModifiedBufferAsksFirst()
    {
        Open("reload-ask.txt");
        FileTab.SetContentForTest(_fixture.Host, "reload-ask.txt", "my edits\n");

        FileTab.RequestReloadForTest(_fixture.Host, "reload-ask.txt");

        Assert.Equal(1, FileTab.PendingConfirmationsForTest(_fixture.Host));
        Assert.Equal("my edits\n", FileTab.ContentForTest(_fixture.Host, "reload-ask.txt"));
    }

    // AND A CLEAN ONE JUST RELOADS — nothing is lost, so there is nothing to ask.
    [Fact]
    public void ReloadingACleanBufferAsksNothing()
    {
        var path = Open("reload-clean.txt");
        File.WriteAllText(path, "changed outside\n");

        FileTab.RequestReloadForTest(_fixture.Host, "reload-clean.txt");

        Assert.Equal(0, FileTab.PendingConfirmationsForTest(_fixture.Host));
        Assert.Equal("changed outside\n", FileTab.ContentForTest(_fixture.Host, "reload-clean.txt"));
    }

    // DISCARD TAKES WHAT IS ON DISK.
    [Fact]
    public void DiscardingOnReloadTakesTheDiskVersion()
    {
        var path = Open("reload-discard.txt");
        FileTab.SetContentForTest(_fixture.Host, "reload-discard.txt", "mine\n");
        File.WriteAllText(path, "theirs\n");

        FileTab.RequestReloadForTest(_fixture.Host, "reload-discard.txt");
        FileTab.AnswerForTest(_fixture.Host, "reload-discard.txt", "Discard");

        Assert.Equal("theirs\n", FileTab.ContentForTest(_fixture.Host, "reload-discard.txt"));
    }
}

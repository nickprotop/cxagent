using CxAgent.Core.Jobs.Builtin;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class FileTabTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private LoadedFile FileAt(string name, string text = "class A { }\n")
    {
        var path = Path.Combine(_fixture.WorkingDirectory, name);
        File.WriteAllText(path, text);
        return new LoadedFile(path, text,
            new FileSnapshot(text, Existed: true, HadBom: false, UsesCrlf: false),
            FileProbe.LanguageFor(path));
    }

    [Fact]
    public void Open_AddsATabTitledForTheFile()
    {
        FileTab.Open(_fixture.Host, FileAt("Program.cs"));

        Assert.Contains(_fixture.Host.Main.Tabs.TabTitles, t => t.Contains("Program.cs"));
    }

    // A SECOND OPEN SWITCHES. Two buffers over one file is the state a save cannot reconcile.
    [Fact]
    public void Open_Twice_DoesNotAddASecondTab()
    {
        var file = FileAt("Once.cs");

        FileTab.Open(_fixture.Host, file);
        var after1 = _fixture.Host.Main.Tabs.TabCount;
        FileTab.Open(_fixture.Host, file);

        Assert.Equal(after1, _fixture.Host.Main.Tabs.TabCount);
    }

    [Fact]
    public void ShowRefusal_OpensATabSayingWhy()
    {
        var path = Path.Combine(_fixture.WorkingDirectory, "a.bin");

        FileTab.ShowRefusal(_fixture.Host, path, "a.bin looks binary, so it is not shown.");

        Assert.Contains(_fixture.Host.Main.Tabs.TabTitles, t => t.Contains("a.bin"));
    }

    // THE EDITOR TAKES THE FIRST KEYSTROKE. A buffer that must be entered before it accepts typing is
    // a mode nothing on screen shows.
    [Fact]
    public void Open_LeavesTheEditorInEditMode()
    {
        FileTab.Open(_fixture.Host, FileAt("Edit.cs"));

        Assert.True(FileTab.EditorIsEditingForTest("Edit.cs"));
    }
}

public class FileSaveRoundTripTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    // THE WIRING, not the pieces. SaveMessage and FileMutation are unit-tested apart; this pins that
    // pressing Save actually reaches them — the failure the unit tests cannot see is a button whose
    // handler was never connected.
    [Fact]
    public async Task Save_WritesTheBufferAndTellsTheModel()
    {
        var path = Path.Combine(_fixture.WorkingDirectory, "Round.cs");
        await File.WriteAllTextAsync(path, "before\n");
        var loaded = FileLoad.TryLoad(path, out _)!;

        FileTab.Open(_fixture.Host, loaded);
        FileTab.SetContentForTest("Round.cs", "after\n");
        await FileTab.SaveForTest(_fixture.Host, "Round.cs");

        Assert.Equal("after\n", await File.ReadAllTextAsync(path));
    }
}

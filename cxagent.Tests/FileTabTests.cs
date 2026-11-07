using CxAgent.Core.Jobs.Builtin;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

[Collection("file-tabs")]
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

    /// <summary>
    /// AN EDITED FILE THE AGENT THEN CHANGED CARRIES BOTH MARKERS: "⚠ name •" — the title says it is still true from
    /// wherever you are looking, through anything else that retitles tabs meanwhile.
    /// </summary>
    [Fact]
    public void An_edited_file_changed_on_disk_shows_both_markers()
    {
        var file = FileAt("Invoice.cs");
        FileTab.Open(_fixture.Host, file);
        FileTab.MarkModifiedForTest(_fixture.Host, "Invoice.cs");
        Assert.Contains(_fixture.Host.Main.Tabs.TabTitles, t => t == "Invoice.cs •");

        File.WriteAllText(file.Path, "class B { }\n");
        FileTab.ChangedOnDiskForTest(_fixture.Host, file.Path);

        Assert.Contains(_fixture.Host.Main.Tabs.TabTitles, t => t == "⚠ Invoice.cs •");
    }

    /// <summary>
    /// A PROMPT RAISED WHILE A FILE TAB IS IN FRONT belongs to the session, and its dot goes on the session's tab — not on
    /// the file's, whose markers survive the prompt coming and going. Seen in a drive: the agent's write asked while
    /// the file was in front, and answering it reset the file tab's "⚠ name •" to a bare "name".
    /// </summary>
    [Fact]
    public void A_prompt_coming_and_going_leaves_a_file_tabs_markers_alone()
    {
        var file = FileAt("Ledger.cs");
        FileTab.Open(_fixture.Host, file);
        FileTab.MarkModifiedForTest(_fixture.Host, "Ledger.cs");
        File.WriteAllText(file.Path, "class C { }\n");
        FileTab.ChangedOnDiskForTest(_fixture.Host, file.Path);

        _fixture.Host.Main.SetWaitingForTest(true);
        Assert.Contains(_fixture.Host.Main.Tabs.TabTitles, t => t == "⚠ Ledger.cs •");
        Assert.Contains(_fixture.Host.Main.Tabs.TabTitles, t => t == "Chat •");

        _fixture.Host.Main.SetWaitingForTest(false);
        Assert.Contains(_fixture.Host.Main.Tabs.TabTitles, t => t == "⚠ Ledger.cs •");
        Assert.Contains(_fixture.Host.Main.Tabs.TabTitles, t => t == "Chat");
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

        Assert.True(FileTab.EditorIsEditingForTest(_fixture.Host, "Edit.cs"));
    }
}

[Collection("file-tabs")]
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
        FileTab.SetContentForTest(_fixture.Host, "Round.cs", "after\n");
        await FileTab.SaveForTest(_fixture.Host, "Round.cs");

        Assert.Equal("after\n", await File.ReadAllTextAsync(path));
    }
}

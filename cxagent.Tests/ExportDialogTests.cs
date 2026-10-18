using CxAgent.Core.Commands;
using CxAgent.Core.Export;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Drivers;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The export dialog, on the headless driver: what it opens with, what its size line says, and that a slow render or
/// a failed write never takes the window with it.
/// </summary>
[Collection(ConfirmDialogCollection.Name)]
public class ExportDialogTests : IDisposable
{
    public void Dispose() => ExportDialog.CloseIfOpen();

    private static ExportResult Sized(int bytes, int messages = 3) =>
        new("x", new ExportStats(messages, 1, bytes));

    private static (ExportDialogHost Host, ConsoleWindowSystem System) Host(Func<ExportDecision, string?>? deliver = null)
    {
        var system = new ConsoleWindowSystem(new HeadlessConsoleDriver(100, 40));
        CxAgentTheme.Install(system, null);
        return (new ExportDialogHost(system, deliver ?? (_ => null), action => action()), system);
    }

    private static ExportDialogRequest Request(Func<ExportOptions, ExportResult> render, bool isAgent = false,
        ExportChoices? choices = null) =>
        new("[bold]Fix it[/] · 18 Oct", isAgent, render, "out.md", Path.GetTempPath(), choices ?? ExportChoices.Default, ExportPrefill.None);

    private static async Task<string> SizeSettles()
    {
        Assert.True(await Poll.Until(() => ExportDialog.SizeLineForTest is { } s && s != "…"), "the size line never settled");
        return ExportDialog.SizeLineForTest!;
    }

    [Fact]
    public async Task ItOpensOnTheRememberedChoices_AndHandTicksTurnItCustom()
    {
        var (host, _) = Host();
        _ = ExportDialog.AskAsync(host, Request(_ => Sized(100), choices: new ExportChoices(ExportOptions.Full, false, false)));

        Assert.Equal(ExportOptions.Full, ExportDialog.PartsForTest);
        ExportDialog.TickForTest(ExportParts.Thinking, false);
        Assert.Contains("Custom", ExportDialog.PresetForTest);

        ExportDialog.ChoosePresetForTest(full: true);
        Assert.Equal(ExportOptions.Full, ExportDialog.PartsForTest);
        await SizeSettles();
    }

    [Fact]
    public async Task TheSizeLine_FollowsTheOptions()
    {
        var (host, _) = Host();
        _ = ExportDialog.AskAsync(host, Request(o => Sized((o.Parts & ExportParts.ToolResults) != 0 ? 2_202_009 : 14_336)));

        Assert.Contains("14 KB", await SizeSettles());
        ExportDialog.ChoosePresetForTest(full: true);
        Assert.True(await Poll.Until(() => ExportDialog.SizeLineForTest?.Contains("2.1 MB") == true));
    }

    /// <summary>
    /// A SLOW RENDER DOES NOT HOLD THE WINDOW: a full export of a long session takes time to build, and the dialog
    /// stays usable — the size line says it is working — while it does.
    /// </summary>
    [Fact]
    public async Task ARenderThatTakesItsTime_LeavesTheDialogResponsive()
    {
        var release = new ManualResetEventSlim(false);
        var (host, _) = Host();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _ = ExportDialog.AskAsync(host, Request(_ => { release.Wait(TimeSpan.FromSeconds(10)); return Sized(100); }));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"opening waited on the render: {clock.Elapsed}");

        var pressed = Task.Run(() => ExportDialog.PressForTest(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false)));
        Assert.True(pressed.Wait(TimeSpan.FromSeconds(2)), "a key waited on the render");
        Assert.Equal("…", ExportDialog.SizeLineForTest);
        Assert.False(ExportDialog.ExportEnabledForTest);

        release.Set();
        await SizeSettles();
    }

    [Fact]
    public async Task ALargeExport_ToTheClipboard_IsWarnedAbout()
    {
        var (host, _) = Host();
        _ = ExportDialog.AskAsync(host, Request(_ => Sized(2_202_009)));
        Assert.DoesNotContain("large for the clipboard", await SizeSettles());

        ExportDialog.ChooseClipboardForTest(true);
        Assert.True(await Poll.Until(() => ExportDialog.SizeLineForTest?.Contains("large for the clipboard") == true));
    }

    [Fact]
    public async Task NothingToExport_CannotBeExported()
    {
        var (host, _) = Host();
        _ = ExportDialog.AskAsync(host, Request(_ => Sized(10, messages: 0)));
        await SizeSettles();

        Assert.False(ExportDialog.ExportEnabledForTest);
    }

    /// <summary>A FAILED WRITE KEEPS THE CHOICES: the dialog stays open with the reason, so a bad path costs a correction.</summary>
    [Fact]
    public async Task AFailedWrite_StaysOpen_SayingWhy()
    {
        var (host, _) = Host(_ => "disk full");
        var asked = ExportDialog.AskAsync(host, Request(_ => Sized(100)));
        await SizeSettles();
        ExportDialog.ChooseClipboardForTest(true);
        await SizeSettles();

        await ExportDialog.ExportForTest();

        Assert.True(ExportDialog.IsOpen);
        Assert.Equal("disk full", ExportDialog.ErrorForTest);
        Assert.False(asked.IsCompleted);
    }

    [Fact]
    public async Task ADeliveredExport_ClosesWithTheDecision()
    {
        ExportDecision? delivered = null;
        var (host, _) = Host(d => { delivered = d; return null; });
        var asked = ExportDialog.AskAsync(host, Request(_ => Sized(100)));
        await SizeSettles();
        ExportDialog.ChooseClipboardForTest(true);
        await SizeSettles();

        await ExportDialog.ExportForTest();

        var decision = await asked;
        Assert.NotNull(decision);
        Assert.True(decision!.ToClipboard);
        Assert.Same(decision, delivered);
        Assert.False(ExportDialog.IsOpen);
    }

    [Fact]
    public async Task AnAgent_IsOfferedNoSubAgents()
    {
        var (host, _) = Host();
        _ = ExportDialog.AskAsync(host, Request(_ => Sized(100), isAgent: true));

        Assert.False(ExportDialog.HasSubAgentBoxForTest);
        await SizeSettles();
    }

    /// <summary>AN AGENT OPENED ON FULL READS FULL: it has no sub-agent box, and that must not make every preset Custom.</summary>
    [Fact]
    public async Task AnAgent_OpenedOnFull_ReadsFull()
    {
        var (host, _) = Host();
        _ = ExportDialog.AskAsync(host, Request(_ => Sized(100), isAgent: true, choices: new ExportChoices(ExportOptions.Full, false, false)));

        Assert.DoesNotContain("Custom", ExportDialog.PresetForTest);
        await SizeSettles();
    }

    /// <summary>A RENDER OR A DELIVERY THAT THROWS IS SAID, and Export is offered again — not left disabled in silence.</summary>
    [Fact]
    public async Task AFailureWhileExporting_IsShown_AndExportComesBack()
    {
        var (host, _) = Host(_ => throw new InvalidOperationException("clipboard gone"));
        _ = ExportDialog.AskAsync(host, Request(_ => Sized(100)));
        await SizeSettles();
        ExportDialog.ChooseClipboardForTest(true);
        await SizeSettles();

        await ExportDialog.ExportForTest();

        Assert.True(ExportDialog.IsOpen);
        Assert.Contains("clipboard gone", ExportDialog.ErrorForTest);
        Assert.True(ExportDialog.ExportEnabledForTest);
    }

    /// <summary>CLOSED WHILE IT WAS STILL RENDERING, NOTHING IS WRITTEN: Escape is the user changing their mind.</summary>
    [Fact]
    public async Task ClosingDuringAnExport_DeliversNothing()
    {
        var delivered = false;
        var slow = new ManualResetEventSlim(true);
        var (host, _) = Host(_ => { delivered = true; return null; });
        _ = ExportDialog.AskAsync(host, Request(_ => { slow.Wait(TimeSpan.FromSeconds(10)); return Sized(100); }));
        await SizeSettles();
        ExportDialog.ChooseClipboardForTest(true);
        await SizeSettles();

        slow.Reset();
        var exporting = ExportDialog.ExportForTest();
        ExportDialog.CloseIfOpen();
        slow.Set();
        await exporting;

        Assert.False(delivered);
    }

    [Fact]
    public async Task Closing_AnswersNothing()
    {
        var (host, _) = Host();
        var asked = ExportDialog.AskAsync(host, Request(_ => Sized(100)));

        Assert.True(ExportDialog.CloseIfOpen());

        Assert.Null(await asked);
        Assert.False(ExportDialog.IsOpen);
    }
}

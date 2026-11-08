using CxAgent.Core.Colleagues;
using CxAgent.Core.Llm;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The panel's Colleagues section — every colleague in the library, the ones here with their state, the others with
/// Bring — and the distilling indicator: one record from Core, drawn by the terminal's panel and the browser's pane.
/// </summary>
public sealed class ColleaguePanelTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("colleague-panel-config-").FullName;
    private readonly string _project = Directory.CreateTempSubdirectory("colleague-panel-proj-").FullName;

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private Session Open()
    {
        var resolution = ResolvedConfig.ForTesting(new MockLlmProvider()) is var r
            ? r with { Model = r.Model! with { ContextWindow = 200_000 }, Catalog = r.Catalog with { Colleagues = true } }
            : null!;
        var session = new Session(_project);
        SessionFactory.Wire(session, resolution, new SharedServices { GlobalInstructionsDir = _config },
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        return session;
    }

    private void Kept(string name, string description) =>
        Library.Create(new ColleagueRecord { Name = name, Type = "general", Created = DateTimeOffset.UtcNow, Description = description });

    private async Task<ColleaguePanel> TwoColleaguesOneHere()
    {
        Kept("Charting", "lays out charts");
        Kept("Parser", "reads invoices");
        var session = Open();
        await session.BringColleagueAsync("Charting");   // joins idle
        return session.Colleagues!.Panel()!;
    }

    [Fact]
    public async Task Panel_lists_every_colleague_here_or_not()
    {
        var panel = await TwoColleaguesOneHere();

        var here = Assert.Single(panel.Rows, r => r.Name == "Charting");
        Assert.Equal("charting", here.Handle);
        Assert.Equal("idle", here.State);
        var away = Assert.Single(panel.Rows, r => r.Name == "Parser");
        Assert.Null(away.Handle);
        Assert.Equal("reads invoices", away.Description);
    }

    [Fact]
    public void An_empty_library_has_no_panel()
    {
        Assert.Null(Open().Colleagues!.Panel());
    }

    /// <summary>
    /// One list of colleagues per front end, in Tasks: the session pane drew a second one, which the user found redundant
    /// (2026-11-08).
    /// </summary>
    [Fact]
    public void The_session_pane_does_not_list_them_again()
    {
        Assert.True(CxAgent.Web.Http.StaticAssets.TryGet("/app.js", out var body, out _));
        var js = System.Text.Encoding.UTF8.GetString(body.Span);

        Assert.DoesNotContain("function drawColleagues(", js);
        Assert.Contains("function drawColleagueCards(", js);
        Assert.Null(typeof(SessionPanel.SessionPanelState).GetProperty("Colleagues"));
    }

    [Fact]
    public void Status_text_names_who_is_distilling()
    {
        Assert.Equal("distilling Charting…", MainWindow.DistillingText(["Charting"]));
        Assert.Equal("distilling Charting, Parser…", MainWindow.DistillingText(["Charting", "Parser"]));
        Assert.Null(MainWindow.DistillingText([]));
    }

    [Fact]
    public async Task Browser_pane_gets_the_same_rows()
    {
        var panel = await TwoColleaguesOneHere();

        var json = CxAgent.Web.WebPanel.Colleagues(panel)!;

        var rows = json["rows"]!.AsArray();
        Assert.Equal(["Charting", "Parser"], rows.Select(r => r!["name"]!.GetValue<string>()).Order());
        Assert.Equal("charting", rows.First(r => r!["name"]!.GetValue<string>() == "Charting")!["handle"]!.GetValue<string>());
        Assert.Null(rows.First(r => r!["name"]!.GetValue<string>() == "Parser")!["handle"]);
        Assert.Null(CxAgent.Web.WebPanel.Colleagues(null));
    }

    [Fact]
    public void The_browser_draws_the_section_bring_and_the_status()
    {
        Assert.True(CxAgent.Web.Http.StaticAssets.TryGet("/app.js", out var body, out _));
        var js = System.Text.Encoding.UTF8.GetString(body.Span);

        Assert.Contains("p.colleagues", js);
        Assert.Contains("colleagueBring", js);
        Assert.Contains("distilling", js);
    }
}

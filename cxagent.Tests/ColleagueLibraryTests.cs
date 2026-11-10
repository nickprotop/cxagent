using CxAgent.Core.Colleagues;
using CxAgent.Core.Llm;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The library — every colleague with what it is, what it knows and its experience, read-only — built once in Core and
/// shown as a tab in the terminal and a page in the browser.
/// </summary>
public sealed class ColleagueLibraryTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("colleague-lib-config-").FullName;
    private readonly string _project = Directory.CreateTempSubdirectory("colleague-lib-proj-").FullName;

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private void Charting()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "explore", Created = DateTimeOffset.UtcNow, Description = "lays out charts", Instance = "local" });
        Library.Update("Charting", r => r with
        {
            ExperienceVersion = 3,
            DistilledThrough = 2,
            Workplaces = [new Workplace("cxgpu-12345678", "/home/dev/source/cxgpu", 2, DateTimeOffset.UtcNow)],
            Skipped = [new SkippedRound(1, "a secret", DateTimeOffset.UtcNow)],
        });
        Library.WriteCore("Charting", "## Lessons\nPanes first.");
        Library.WriteCodebase("Charting", "cxgpu-12345678", "## Architecture\nThree panes.");
        Library.Append("Charting", new PastRound(0, "s", "/home/dev/source/cxgpu", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new Core.Models.ChatMessage { Role = "user", Content = "x" }]));
        Library.Append("Charting", new PastRound(0, "s", "/home/dev/source/cxgpu", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new Core.Models.ChatMessage { Role = "user", Content = "y" }]));
    }

    [Fact]
    public void Build_lists_each_with_facts_and_experience_parts()
    {
        Charting();

        var entry = Assert.Single(LibraryView.Build(Library));

        Assert.Equal("Charting", entry.Name);
        Assert.Equal("lays out charts", entry.Description);
        Assert.Equal(["cxgpu"], entry.Knows);
        Assert.Equal(new LibraryFacts(Version: 3, Rounds: 2, Distilled: 2, Skipped: 1, Type: "explore", Model: "local"), entry.Facts);
        Assert.Equal("Core", entry.Experience[0].Title);
        Assert.Equal("## Lessons\nPanes first.", entry.Experience[0].Markdown);
    }

    [Fact]
    public void Experience_parts_are_named_by_folder()
    {
        Charting();

        var entry = Assert.Single(LibraryView.Build(Library));

        Assert.Equal("cxgpu", entry.Experience[1].Title);
        Assert.Equal("## Architecture\nThree panes.", entry.Experience[1].Markdown);
    }

    [Fact]
    public void The_terminal_tab_lists_them_and_shows_the_one_picked()
    {
        Charting();
        Library.Create(new ColleagueRecord { Name = "Parser", Type = "general", Created = DateTimeOffset.UtcNow.AddDays(-1) });

        var tab = new ColleagueLibraryTab(Library, new ColleagueLibraryTab.Actions(_ => { }, _ => { }, _ => { }, _ => { }));

        Assert.Equal(["Charting", "Parser"], tab.RailNamesForTest);
        Assert.Contains("Panes first.", tab.DetailTextForTest);
        Assert.Contains("Three panes.", tab.DetailTextForTest);
        Assert.Contains("v3", tab.DetailTextForTest);
    }

    [Fact]
    public void The_terminal_tab_brings_and_opens_the_one_picked()
    {
        Charting();
        var brought = new List<string>();
        var opened = new List<string>();
        var tab = new ColleagueLibraryTab(Library, new ColleagueLibraryTab.Actions(brought.Add, opened.Add, _ => { }, _ => { }));

        tab.BringForTest();
        tab.OpenForTest();

        Assert.Equal(["Charting"], brought);
        Assert.Equal(["Charting"], opened);
    }

    [Fact]
    public void The_browser_page_gets_the_same_entries()
    {
        Charting();

        var json = CxAgent.Web.WebLibrary.Json(LibraryView.Build(Library));

        var row = Assert.Single(json)!;
        Assert.Equal("Charting", row["name"]!.GetValue<string>());
        Assert.Equal(3, row["facts"]!["version"]!.GetValue<long>());
        Assert.Equal("cxgpu", row["experience"]![1]!["title"]!.GetValue<string>());
    }

    [Fact]
    public void The_browser_draws_the_library_page()
    {
        Assert.True(CxAgent.Web.Http.StaticAssets.TryGet("/app.js", out var body, out _));
        var js = System.Text.Encoding.UTF8.GetString(body.Span);

        Assert.Contains("openedColleagues", js);
        Assert.Contains("colleagueOpen", js);
        Assert.Contains("colleagueBring", js);
        Assert.Contains("type: \"colleagueForget\"", js);
        Assert.Contains("case \"confirmColleagueForget\"", js);
        Assert.Contains("type: \"colleagueDistill\"", js);
        Assert.Contains("case \"confirmColleagueDistill\"", js);
        Assert.Contains("e.factsLine", js);
    }

    [Fact]
    public void The_terminal_library_has_Forget()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        var forgotten = new List<string>();
        var distilled = new List<string>();
        var tab = new ColleagueLibraryTab(Library, new ColleagueLibraryTab.Actions(_ => { }, _ => { }, forgotten.Add, distilled.Add));

        tab.ForgetForTest();
        tab.DistillForTest();

        Assert.Equal(["Charting"], forgotten);
        Assert.Equal(["Charting"], distilled);
    }
}

using CxAgent.Core.Colleagues;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A colleague's folder: who it is, what it has distilled, and its past — every round, appended, never edited.
/// </summary>
public sealed class ColleagueStoreTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("colleague-store-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_config, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Store() => new(_config);

    private static ColleagueRecord Record(string name) => new()
    {
        Name = name,
        Type = "general",
        Created = DateTimeOffset.UtcNow,
    };

    private static PastRound Round(string text = "worked on it") => new(0, "s1", "/tmp/project",
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        [new ChatMessage { Role = "user", Content = "do it" }, new ChatMessage { Role = "assistant", Content = text }]);

    [Fact]
    public void Create_then_find_is_case_insensitive()
    {
        var store = Store();
        store.Create(Record("Charting"));

        Assert.Equal("Charting", store.Find("charting")?.Name);
        Assert.Single(store.List());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(" Charting")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Name_refusal_covers_unsafe_names(string name) =>
        Assert.NotNull(ColleagueStore.NameRefusal(name));

    [Theory]
    [InlineData("Charting")]
    [InlineData("UI layout-2")]
    [InlineData("db_expert")]
    public void Name_refusal_passes_ordinary_names(string name) =>
        Assert.Null(ColleagueStore.NameRefusal(name));

    [Fact]
    public void Create_refuses_a_name_taken_in_another_case()
    {
        var store = Store();
        store.Create(Record("Charting"));

        Assert.Throws<ArgumentException>(() => store.Create(Record("CHARTING")));
    }

    [Fact]
    public void Create_refuses_an_unsafe_name_before_touching_disk()
    {
        var store = Store();

        Assert.Throws<ArgumentException>(() => store.Create(Record("../escape")));
        Assert.False(Directory.Exists(Path.Combine(_config, "escape")));
    }

    [Fact]
    public void Append_assigns_increasing_seq_and_keeps_every_line()
    {
        var store = Store();
        store.Create(Record("Charting"));

        Assert.Equal(1, store.Append("Charting", Round()));
        Assert.Equal(2, store.Append("Charting", Round()));
        Assert.Equal(3, store.Append("Charting", Round()));

        Assert.Equal(3, File.ReadAllLines(Path.Combine(store.FolderOf("Charting"), "past.jsonl")).Length);
    }

    [Fact]
    public async Task Concurrent_appends_never_interleave()
    {
        var store = Store();
        store.Create(Record("Charting"));
        var big = new string('x', 50_000);

        // SEPARATE STORE INSTANCES, as two sessions in two processes have: only the lock file can order them.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var mine = Store();
            for (var i = 0; i < 25; i++) mine.Append("Charting", Round(big));
        })));

        var rounds = store.Undistilled("Charting");
        Assert.Equal(200, rounds.Count);
        Assert.Equal(Enumerable.Range(1, 200).Select(i => (long)i), rounds.Select(r => r.Seq).Order());
        Assert.All(rounds, r => Assert.Equal(big, r.Messages[1].Content));
    }

    [Fact]
    public void Undistilled_is_everything_after_the_watermark()
    {
        var store = Store();
        store.Create(Record("Charting"));
        for (var i = 0; i < 3; i++) store.Append("Charting", Round());

        store.Save(store.Find("Charting")! with { DistilledThrough = 1 });

        Assert.Equal([2L, 3L], store.Undistilled("Charting").Select(r => r.Seq));
    }

    [Fact]
    public void Experience_round_trips_core_and_codebases()
    {
        var store = Store();
        store.Create(Record("Charting"));

        store.WriteExperience("Charting", core: "## How you work\nCarefully.", codebase: "repo-ab12cd34", text: "## Architecture\nLayers.");

        var experience = store.ReadExperience("Charting");
        Assert.Equal("## How you work\nCarefully.", experience.Core);
        Assert.Equal("## Architecture\nLayers.", experience.Codebases["repo-ab12cd34"]);
    }

    [Fact]
    public void A_damaged_past_line_is_skipped_not_fatal()
    {
        var store = Store();
        store.Create(Record("Charting"));
        store.Append("Charting", Round());
        File.AppendAllText(Path.Combine(store.FolderOf("Charting"), "past.jsonl"), "{not json\n");
        store.Append("Charting", Round());

        Assert.Equal([1L, 2L], store.Undistilled("Charting").Select(r => r.Seq));
    }
}

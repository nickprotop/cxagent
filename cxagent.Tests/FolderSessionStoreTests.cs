using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The resume store's whole surface, against folders — the same questions the row store answered.
/// </summary>
public class FolderSessionStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "fsstore-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly FolderSessionStore _store;

    public FolderSessionStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _paths = new AppPaths(_dir);
        _paths.EnsureCreated();
        _store = new FolderSessionStore(_paths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static List<ChatMessage> Talk(string text) =>
        [new ChatMessage { Role = "user", Content = text }];

    [Fact]
    public void A_saved_turn_comes_back_by_id_with_its_context()
    {
        _store.SaveTurn(new FolderSessionStore.ResumeTurn(
            "agent-1", Talk("fix the parser"), 100, 20, "/src", EditMode.Auto));

        var snap = _store.LoadById("agent-1");

        Assert.NotNull(snap);
        Assert.Equal("agent-1", snap!.AgentId);
        Assert.Equal("fix the parser", snap.Context[0].Content);
        Assert.Equal(100, snap.InputTokens);
        Assert.Equal("/src", snap.WorkingDir);
        Assert.Equal(EditMode.Auto, snap.Edits);
    }

    [Fact]
    public void Saving_twice_replaces_rather_than_accumulates()
    {
        _store.SaveTurn(new FolderSessionStore.ResumeTurn("a", Talk("one"), 1, 1));
        _store.SaveTurn(new FolderSessionStore.ResumeTurn("a", Talk("two"), 2, 2));

        var snap = _store.LoadById("a");

        Assert.Single(snap!.Context);
        Assert.Equal("two", snap.Context[0].Content);
    }

    [Fact]
    public void The_latest_unfinished_is_the_newest_running_session_in_that_folder()
    {
        _store.SaveTurn(new FolderSessionStore.ResumeTurn("old", Talk("old"), 1, 1, "/src"));
        Thread.Sleep(10);
        _store.SaveTurn(new FolderSessionStore.ResumeTurn("new", Talk("new"), 1, 1, "/src"));
        _store.SaveTurn(new FolderSessionStore.ResumeTurn("other", Talk("x"), 1, 1, "/elsewhere"));

        Assert.Equal("new", _store.LoadLatestUnfinished("/src")!.AgentId);
    }

    [Fact]
    public void A_finished_session_is_never_offered_for_resume()
    {
        _store.SaveTurn(new FolderSessionStore.ResumeTurn("a", Talk("done"), 1, 1, "/src"));
        _store.MarkFinished("a");

        Assert.Null(_store.LoadLatestUnfinished("/src"));
    }

    [Fact]
    public void A_uid_prefix_matching_two_sessions_is_reported_ambiguous_never_guessed()
    {
        _store.SaveTurn(new FolderSessionStore.ResumeTurn("abc111", Talk("one"), 1, 1));
        _store.SaveTurn(new FolderSessionStore.ResumeTurn("abc222", Talk("two"), 1, 1));

        var lookup = _store.LoadByUid("abc");

        Assert.True(lookup.IsAmbiguous);
        Assert.Null(lookup.Session);
        Assert.Equal(2, lookup.Ambiguous.Count);
    }

    [Fact]
    public void A_uid_prefix_matching_one_session_resolves_to_it()
    {
        _store.SaveTurn(new FolderSessionStore.ResumeTurn("abc111", Talk("one"), 1, 1));
        _store.SaveTurn(new FolderSessionStore.ResumeTurn("zzz222", Talk("two"), 1, 1));

        var lookup = _store.LoadByUid("abc");

        Assert.False(lookup.IsAmbiguous);
        Assert.Equal("abc111", lookup.Session!.AgentId);
    }

    [Fact]
    public void Listing_a_folder_shows_its_sessions_newest_first()
    {
        _store.SaveTurn(new FolderSessionStore.ResumeTurn("first", Talk("a"), 1, 1, "/src"));
        Thread.Sleep(10);
        _store.SaveTurn(new FolderSessionStore.ResumeTurn("second", Talk("b"), 1, 1, "/src"));

        var rows = _store.List("/src");

        Assert.Equal(new[] { "second", "first" }, rows.Select(r => r.Uid).ToArray());
    }

    [Fact]
    public void The_title_is_the_first_thing_a_person_typed()
    {
        _store.SaveTurn(new FolderSessionStore.ResumeTurn("a", Talk("fix the parser"), 1, 1, "/src"));

        Assert.Equal("fix the parser", _store.List("/src")[0].Title);
    }

    [Fact]
    public void Pruning_removes_finished_sessions_past_retention_and_keeps_running_ones()
    {
        _store.SaveTurn(new FolderSessionStore.ResumeTurn("gone", Talk("old"), 1, 1));
        _store.SaveTurn(new FolderSessionStore.ResumeTurn("kept", Talk("live"), 1, 1));
        _store.MarkFinished("gone");
        var folder = new SessionFolder(_paths, "gone");
        var header = SessionHeader.Read(folder)!;
        SessionHeader.Write(folder, header with { UpdatedAt = DateTimeOffset.UtcNow.AddDays(-90) });

        _store.Prune(TimeSpan.FromDays(30));

        Assert.Null(_store.LoadById("gone"));
        Assert.NotNull(_store.LoadById("kept"));
    }

    [Fact]
    public void A_directory_with_no_readable_header_is_never_swept()
    {
        var orphan = Path.Combine(_paths.LogsDir, "no-header");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "something.log"), "diagnostics");

        _store.Prune(TimeSpan.Zero);

        Assert.True(Directory.Exists(orphan));
    }
}

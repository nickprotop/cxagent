using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Scrollback as JSONL: that a seq written repeatedly folds to its last value, that two sessions can
/// share one agent's file, and that a torn line does not cost the replay.
/// </summary>
public class FolderTranscriptStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ftstore-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly FolderTranscriptStore _store;

    public FolderTranscriptStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _paths = new AppPaths(_dir);
        _paths.EnsureCreated();
        _store = new FolderTranscriptStore(_paths);
        _store.BindAgent("session-1", "agent-1");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Entries_come_back_in_reading_order()
    {
        _store.Append("session-1", 1, "user", "User", "hello");
        _store.Append("session-1", 2, "assistant", "Assistant", "hi");

        var rows = _store.Window("session-1");

        Assert.Equal(new[] { "hello", "hi" }, rows.Select(r => r.Body).ToArray());
    }

    [Fact]
    public void The_last_write_for_a_seq_wins_because_a_message_grows_token_by_token()
    {
        _store.Append("session-1", 1, "assistant", "Assistant", "par");
        _store.Append("session-1", 1, "assistant", "Assistant", "parse");
        _store.Append("session-1", 1, "assistant", "Assistant", "parser");

        var rows = _store.Window("session-1");

        Assert.Single(rows);
        Assert.Equal("parser", rows[0].Body);
    }

    [Fact]
    public void A_window_returns_the_last_n_before_a_sequence_in_reading_order()
    {
        for (var i = 1; i <= 5; i++) _store.Append("session-1", i, "user", "User", $"m{i}");

        var rows = _store.Window("session-1", before: 5, limit: 2);

        Assert.Equal(new[] { "m3", "m4" }, rows.Select(r => r.Body).ToArray());
    }

    [Fact]
    public void Entries_for_another_session_in_the_same_folder_are_not_returned()
    {
        _store.BindAgent("session-2", "agent-1");
        _store.Append("session-1", 1, "user", "User", "mine");
        _store.Append("session-2", 1, "user", "User", "theirs");

        var rows = _store.Window("session-1");

        Assert.Single(rows);
        Assert.Equal("mine", rows[0].Body);
    }

    [Fact]
    public void Forgetting_a_session_drops_its_entries_and_leaves_the_others()
    {
        _store.BindAgent("session-2", "agent-1");
        _store.Append("session-1", 1, "user", "User", "mine");
        _store.Append("session-2", 1, "user", "User", "theirs");

        _store.Forget("session-1");

        Assert.Empty(_store.Window("session-1"));
        Assert.Single(_store.Window("session-2"));
    }

    [Fact]
    public void A_session_nobody_bound_writes_nothing_and_reads_empty()
    {
        _store.Append("unbound", 1, "user", "User", "lost");

        Assert.Empty(_store.Window("unbound"));
    }

    [Fact]
    public void A_corrupt_line_is_skipped_rather_than_failing_the_replay()
    {
        _store.Append("session-1", 1, "user", "User", "good");
        File.AppendAllText(new SessionFolder(_paths, "agent-1").TranscriptPath,
            "{ not json at all" + Environment.NewLine);
        _store.Append("session-1", 2, "user", "User", "also good");

        var rows = _store.Window("session-1");

        Assert.Equal(new[] { "good", "also good" }, rows.Select(r => r.Body).ToArray());
    }
}

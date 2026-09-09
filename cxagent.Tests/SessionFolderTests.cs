using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The directory layout and the atomic write — the two things every store above this depends on.
/// </summary>
public class SessionFolderTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "folder-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public SessionFolderTests()
    {
        Directory.CreateDirectory(_dir);
        _paths = new AppPaths(_dir);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void The_three_files_live_under_the_agents_own_directory()
    {
        var folder = new SessionFolder(_paths, "agent-1");

        Assert.Equal(Path.Combine(_paths.LogsDir, "agent-1"), folder.Dir);
        Assert.Equal(Path.Combine(folder.Dir, "session.json"), folder.HeaderPath);
        Assert.Equal(Path.Combine(folder.Dir, "context.json"), folder.ContextPath);
        Assert.Equal(Path.Combine(folder.Dir, "transcript.jsonl"), folder.TranscriptPath);
    }

    [Fact]
    public void An_atomic_write_replaces_what_was_there_and_leaves_no_temp_behind()
    {
        var folder = new SessionFolder(_paths, "agent-2");
        Directory.CreateDirectory(folder.Dir);

        SessionFolder.WriteAtomic(folder.HeaderPath, "first");
        SessionFolder.WriteAtomic(folder.HeaderPath, "second");

        Assert.Equal("second", File.ReadAllText(folder.HeaderPath));
        Assert.Equal(new[] { "session.json" },
            Directory.GetFiles(folder.Dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Reading_a_file_that_is_not_there_answers_null_rather_than_throwing()
    {
        var folder = new SessionFolder(_paths, "agent-3");

        Assert.Null(SessionFolder.ReadOrNull(folder.ContextPath));
    }

    [Fact]
    public void Listing_finds_every_agent_directory_and_ignores_loose_files()
    {
        Directory.CreateDirectory(Path.Combine(_paths.LogsDir, "agent-a"));
        Directory.CreateDirectory(Path.Combine(_paths.LogsDir, "agent-b"));
        File.WriteAllText(Path.Combine(_paths.LogsDir, "stray.txt"), "x");

        var ids = SessionFolder.AgentIdsUnder(_paths).OrderBy(x => x).ToArray();

        Assert.Equal(new[] { "agent-a", "agent-b" }, ids);
    }

    /// <summary>
    /// A CHILD MUST NOT APPEAR IN A TOP-LEVEL LISTING — it nests inside its parent, and recursing
    /// here would offer it as a resumable session the user never ran.
    /// </summary>
    [Fact]
    public void Listing_does_not_descend_into_a_parents_children()
    {
        Directory.CreateDirectory(Path.Combine(_paths.LogsDir, "parent", "child"));

        Assert.Equal(new[] { "parent" }, SessionFolder.AgentIdsUnder(_paths).ToArray());
    }
}

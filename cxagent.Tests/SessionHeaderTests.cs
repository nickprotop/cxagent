using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The small file every listing reads, and its refusal to throw on a bad one.</summary>
public class SessionHeaderTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "header-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public SessionHeaderTests()
    {
        Directory.CreateDirectory(_dir);
        _paths = new AppPaths(_dir);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// THE MODE IS STORED AS "EditMode". Stored as "Edits" it read like a count of edits made — and a
    /// count nothing could keep honestly, since a model changes files through tools, the shell and plugins.
    /// </summary>
    [Fact]
    public void The_edit_mode_is_written_under_its_own_name()
    {
        var folder = new SessionFolder(_paths, "agent-1");
        SessionHeader.Write(folder, new SessionHeader("agent-1", null, null, 0, 0, SessionEndState.Running,
            DateTimeOffset.UnixEpoch, EditMode: EditMode.Auto));

        var text = File.ReadAllText(folder.HeaderPath);
        Assert.Contains("\"EditMode\":2", text);
        Assert.DoesNotContain("\"Edits\"", text);
    }

    /// <summary>A SESSION SAVED UNDER THE OLD NAME STILL RESUMES IN ITS MODE, rather than quietly in another.</summary>
    [Fact]
    public void A_header_saved_under_the_old_name_keeps_its_mode()
    {
        var folder = new SessionFolder(_paths, "agent-1");
        Directory.CreateDirectory(folder.Dir);
        File.WriteAllText(folder.HeaderPath,
            """{"AgentId":"agent-1","Title":null,"WorkingDir":null,"InputTokens":0,"OutputTokens":0,"State":0,"UpdatedAt":"2026-09-09T10:00:00+00:00","Edits":2,"Thinking":null}""");

        Assert.Equal(EditMode.Auto, SessionHeader.Read(folder)!.EditMode);
    }

    [Fact]
    public void The_new_name_wins_over_the_old_one()
    {
        var folder = new SessionFolder(_paths, "agent-1");
        Directory.CreateDirectory(folder.Dir);
        File.WriteAllText(folder.HeaderPath,
            """{"AgentId":"agent-1","Title":null,"WorkingDir":null,"InputTokens":0,"OutputTokens":0,"State":0,"UpdatedAt":"2026-09-09T10:00:00+00:00","Edits":2,"EditMode":1}""");

        Assert.Equal(EditMode.AcceptEdits, SessionHeader.Read(folder)!.EditMode);
    }

    [Fact]
    public void A_header_round_trips_every_field()
    {
        var folder = new SessionFolder(_paths, "agent-1");
        var written = new SessionHeader("agent-1", "Fix the parser", "/src/app",
            InputTokens: 120, OutputTokens: 34, State: SessionEndState.Running,
            UpdatedAt: DateTimeOffset.Parse("2026-09-09T10:00:00Z"), EditMode: EditMode.Auto);

        SessionHeader.Write(folder, written);
        var read = SessionHeader.Read(folder);

        Assert.Equal(written, read);
    }

    [Fact]
    public void An_absent_header_reads_as_null_rather_than_throwing()
    {
        Assert.Null(SessionHeader.Read(new SessionFolder(_paths, "never-written")));
    }

    [Fact]
    public void A_corrupt_header_reads_as_null_so_a_listing_survives_it()
    {
        var folder = new SessionFolder(_paths, "agent-2");
        Directory.CreateDirectory(folder.Dir);
        File.WriteAllText(folder.HeaderPath, "{ this is not json");

        Assert.Null(SessionHeader.Read(folder));
    }

    [Fact]
    public void Writing_creates_the_directory_when_it_is_not_there_yet()
    {
        var folder = new SessionFolder(_paths, "agent-3");

        SessionHeader.Write(folder, new SessionHeader("agent-3", null, null, 0, 0,
            SessionEndState.Running, DateTimeOffset.UtcNow));

        Assert.True(File.Exists(folder.HeaderPath));
    }
}

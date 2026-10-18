using System.Text.Json;
using CxAgent.Core.Agents;
using CxAgent.Core.Export;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Loading a session for export: its history, the histories it continues, and — for a session from before
/// history.jsonl — what its older files still hold.
/// </summary>
public class ExportSourceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("export-source-").FullName;
    private AppPaths Paths => new(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string FolderOf(string agentId) => Path.Combine(_dir, "logs", agentId);

    private static ChatMessage User(string text) => new() { Role = "user", Content = text, Timestamp = DateTimeOffset.UtcNow };
    private static ChatMessage Assistant(string text) => new() { Role = "assistant", Content = text, Timestamp = DateTimeOffset.UtcNow };

    private HistoryRecorder Recorder(string agentId) => new(Path.Combine(FolderOf(agentId), "history.jsonl"));

    private void Header(string agentId, string title)
    {
        Directory.CreateDirectory(FolderOf(agentId));
        SessionHeader.Write(new SessionFolder(Paths, agentId),
            new SessionHeader(agentId, title, "/work", 100, 20, 1, DateTimeOffset.UtcNow));
    }

    private void HeaderWith(string agentId, int input, int output)
    {
        Directory.CreateDirectory(FolderOf(agentId));
        SessionHeader.Write(new SessionFolder(Paths, agentId),
            new SessionHeader(agentId, "t", "/work", input, output, 1, DateTimeOffset.UtcNow));
    }

    private static List<string?> Contents(ExportSource source) => source.Entries.Select(e => e.Message?.Content).ToList();

    [Fact]
    public void AHistory_LoadsInFileOrder()
    {
        Header("A", "Fix it");
        Recorder("A").Record([User("q"), Assistant("a")]);

        var source = ExportSource.Load(Paths, "A")!;

        Assert.Equal(["q", "a"], Contents(source));
        Assert.True(source.Recorded);
        Assert.Equal("Fix it", source.Header.Title);
    }

    /// <summary>A RESUMED SESSION IS SEVERAL FOLDERS: each once, oldest first.</summary>
    [Fact]
    public void AChain_LoadsEveryFolderOnce_OldestFirst()
    {
        Recorder("OLD").Record([User("first")]);
        var loaded = new List<ChatMessage> { User("first") };
        var next = Recorder("NEW");
        next.ContinuesFrom("OLD", loaded);
        next.Record([.. loaded, User("second")]);
        Header("NEW", "Resumed");

        var source = ExportSource.Load(Paths, "NEW")!;

        Assert.Equal(["first", "second"], Contents(source).Where(c => c is not null));
    }

    /// <summary>A RESUMED SESSION'S SPEND IS ITS NEWEST FOLDER'S: each resume starts from the totals before it.</summary>
    [Fact]
    public void AChainsSpend_IsTheNewestFoldersNotTheirSum()
    {
        Recorder("OLD").Record([User("first")]);
        HeaderWith("OLD", 100, 20);
        var next = Recorder("NEW");
        next.ContinuesFrom("OLD", [User("first")]);
        HeaderWith("NEW", 150, 30);

        var source = ExportSource.Load(Paths, "NEW")!;

        Assert.Equal((150, 30), (source.Header.InputTokens, source.Header.OutputTokens));
    }

    /// <summary>
    /// A SESSION RESUMED AFTER THE UPGRADE CONTINUES ONE FROM BEFORE IT: the older folder has no history, and what it
    /// still holds is exported rather than dropped.
    /// </summary>
    [Fact]
    public void AChainReachingAnOldFolder_ExportsWhatThatFolderHolds()
    {
        Header("OLD", "Old one");
        File.WriteAllText(Path.Combine(FolderOf("OLD"), "context.json"),
            JsonSerializer.Serialize(new List<ChatMessage> { User("from before the upgrade"), Assistant("old answer") }));
        var next = Recorder("NEW");
        var carried = User("from before the upgrade");
        next.ContinuesFrom("OLD", [carried]);
        next.Record([carried, User("after it")]);
        Header("NEW", "Old one");

        var source = ExportSource.Load(Paths, "NEW")!;

        Assert.Equal(["from before the upgrade", "old answer", "after it"], Contents(source).Where(c => c is not null));
    }

    [Fact]
    public void ACycle_StillEnds()
    {
        var a = Recorder("A");
        a.ContinuesFrom("B", []);
        var b = Recorder("B");
        b.ContinuesFrom("A", []);
        b.Record([User("in b")]);

        var source = ExportSource.Load(Paths, "B")!;

        Assert.Single(source.Entries, e => e.Message?.Content == "in b");
    }

    /// <summary>NO HISTORY, NEVER COMPACTED: context.json is the whole conversation, so it is used as it is.</summary>
    [Fact]
    public void AnOldSession_NeverCompacted_LoadsItsContext()
    {
        Header("OLD", "Old one");
        File.WriteAllText(Path.Combine(FolderOf("OLD"), "context.json"),
            JsonSerializer.Serialize(new List<ChatMessage> { User("q"), Assistant("a") }));

        var source = ExportSource.Load(Paths, "OLD")!;

        Assert.False(source.Recorded);
        Assert.Equal(["q", "a"], Contents(source));
        Assert.DoesNotContain(source.Entries, e => e.Kind == ExportEntry.Note);
    }

    /// <summary>
    /// NO HISTORY, COMPACTED: context.json lost the start, so the transcript — what was shown — is used, and says what
    /// is missing.
    /// </summary>
    [Fact]
    public void AnOldSession_Compacted_LoadsItsTranscript_WithANote()
    {
        Header("OLD", "Old one");
        File.WriteAllText(Path.Combine(FolderOf("OLD"), "context.json"), JsonSerializer.Serialize(new List<ChatMessage>
        {
            Assistant(CxAgent.Core.Sessions.SessionCompressor.SummaryMarker + "earlier]"), User("later"),
        }));
        File.WriteAllLines(Path.Combine(FolderOf("OLD"), "transcript.jsonl"),
        [
            """{"s":"S","q":1,"at":"2026-10-18T10:00:00+00:00","k":"user","r":"User","b":"the start"}""",
            """{"s":"S","q":2,"at":"2026-10-18T10:01:00+00:00","k":"assistant","r":"Assistant","b":"partial"}""",
            """{"s":"S","q":2,"at":"2026-10-18T10:01:05+00:00","k":"assistant","r":"Assistant","b":"the answer"}""",
            """{"s":"S","q":-1,"at":"2026-10-18T09:59:00+00:00","k":"system","r":"Info","b":"plugins loaded"}""",
        ]);

        var source = ExportSource.Load(Paths, "OLD")!;

        Assert.False(source.Recorded);
        Assert.Equal(ExportEntry.Note, source.Entries[0].Kind);
        Assert.Contains("not recorded", source.Entries[0].Message!.Content);
        Assert.Equal(["plugins loaded", "the start", "the answer"], Contents(source).Skip(1));
        Assert.Equal("user", source.Entries[2].Message!.Role);
    }

    [Fact]
    public void AChild_IsFoundBesideItsParent_OrNotAtAll()
    {
        Header("P", "Parent");
        Recorder("P").Record([User("go")]);
        new HistoryRecorder(Path.Combine(FolderOf("P"), "KID", "history.jsonl")).Record([User("brief")]);

        var source = ExportSource.Load(Paths, "P")!;
        var entry = source.Entries[0];

        Assert.Equal(["brief"], Contents(source.Child(entry, "KID")!));
        Assert.Null(source.Child(entry, "NOBODY"));
    }

    [Fact]
    public void NoFolder_IsNull() => Assert.Null(ExportSource.Load(Paths, "MISSING"));
}

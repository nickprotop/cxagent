using CxAgent.Core.Agents;
using CxAgent.Core.Colleagues;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using CxAgent.UI.Config;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Renaming a colleague: it keeps its former names, so a session that recorded a worker as <c>colleague:OldName</c> still
/// brings back the colleague (user, 2026-11-09). THE FOLDER NEVER MOVES — other processes may be appending to it — so a
/// name is found by the records, not by the folder's own name.
/// </summary>
public sealed class ColleagueRenameTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("rename-config-").FullName;
    private readonly string _project = Directory.CreateTempSubdirectory("rename-proj-").FullName;
    private readonly SubAgentStore _workers = new();
    private readonly List<WorkerMark> _marks = [];
    private readonly BufferedChatSink _seen = new();

    public ColleagueRenameTests() =>
        Library.Create(new ColleagueRecord { Name = "Charting", Created = DateTimeOffset.UtcNow, Instance = "main" });

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private FolderSessionStore Sessions => new(new AppPaths(_config));

    private string? Rename(string name, string to) => ColleagueRenaming.Rename(Library, Sessions, new RenameOrder(name, to));

    private ColleagueDesk Desk() => new(new ColleagueDeskParts
    {
        Library = Library,
        Workers = _workers,
        Folder = _project,
        SessionId = () => "session",
        NoteWorker = _marks.Add,
        Models = _ => new ModelChoice(new MockLlmProvider(), "main:m", 200_000, "main"),
        Sessions = Sessions,
    });

    [Fact]
    public void A_renamed_colleague_is_found_by_either_name_and_keeps_its_folder()
    {
        var folder = Library.FolderOf("Charting");

        Assert.Null(Rename("Charting", "Plotter"));

        Assert.Equal("Plotter", Library.Find("Charting")!.Name);
        Assert.Equal("Plotter", Library.Find("plotter")!.Name);
        Assert.Equal(["Charting"], Library.Find("Plotter")!.FormerNames);
        Assert.Equal(folder, Library.FolderOf("Plotter"));
        Assert.True(Directory.Exists(folder));
        Assert.Equal(["Plotter"], Library.List().Select(r => r.Name));
    }

    [Fact]
    public void A_round_under_the_old_name_reaches_the_renamed_past()
    {
        Rename("Charting", "Plotter");

        Library.Append("Charting", new PastRound(0, "S", _project, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [new ChatMessage { Role = "user", Content = "x" }]));

        Assert.Equal(1, Library.RoundCount("Plotter"));
        Assert.Single(Directory.EnumerateDirectories(Library.Root));
    }

    [Fact]
    public void An_old_session_origin_still_brings_the_colleague()
    {
        Assert.Null(Rename("Charting", "Plotter"));
        using var desk = Desk();

        var augment = desk.Augment("colleague:Charting");

        Assert.NotNull(augment);
        Assert.Contains("You are Plotter", augment!.Context);
    }

    [Fact]
    public void A_name_held_by_another_now_or_before_is_refused()
    {
        Library.Create(new ColleagueRecord { Name = "Maps", Created = DateTimeOffset.UtcNow });
        Rename("Charting", "Plotter");

        Assert.Contains("already a colleague called 'Maps'", Rename("Plotter", "maps"));
        Assert.Contains("Plotter", Rename("Maps", "Charting"));
        Assert.Throws<ArgumentException>(() => Library.Create(new ColleagueRecord { Name = "Charting", Created = DateTimeOffset.UtcNow }));
        Assert.NotNull(Rename("Plotter", "../x"));
        Assert.Null(Rename("Plotter", "Charting"));
        Assert.Equal("Charting", Library.Find("Plotter")!.Name);
    }

    [Fact]
    public void A_case_only_rename_keeps_the_old_spelling()
    {
        Assert.Null(Rename("Charting", "charting"));

        var record = Library.Find("Charting")!;
        Assert.Equal("charting", record.Name);
        Assert.Equal(["Charting"], record.FormerNames);
    }

    [Fact]
    public void Renaming_waits_for_distilling()
    {
        using var held = DistillingLock.TryTake(Library.Root);

        Assert.Contains("being distilled", Rename("Charting", "Plotter"));
        Assert.Equal("Charting", Library.Find("Charting")!.Name);
    }

    [Fact]
    public void The_one_to_one_is_still_found()
    {
        Sessions.SaveTurn(new FolderSessionStore.ResumeTurn("ONE", [new ChatMessage { Role = "user", Content = "a" }], 1, 1, _project) { Tag = "colleague:Charting" });
        Assert.Null(Rename("Charting", "Plotter"));
        using var desk = Desk();

        var opening = desk.OneToOne("Plotter", out var refusal);

        Assert.Null(refusal);
        Assert.Equal("ONE", opening!.Resume!.AgentId);
    }

    /// <summary>A live incarnation here follows the rename: its panel row, its origin, and what is said when it learns.</summary>
    [Fact]
    public async Task A_live_incarnation_here_follows_the_rename()
    {
        using var desk = Desk();
        // A NEW DESK DRAINS AT ONCE, holding the distilling lock a moment — a rename then is refused, and says try again.
        await desk.Idle();
        var handle = Incarnation("Charting");

        Assert.Contains("Plotter", desk.Rename("Charting", "Plotter"));

        Assert.Equal("colleague:Plotter", _workers.OriginOf(handle));
        Assert.Contains(_marks, m => m.Origin == "colleague:Plotter");
        Assert.Equal(handle, desk.Panel()!.Rows.Single(r => r.Name == "Plotter").Handle);
        await desk.Idle();
    }

    /// <summary>A worker another process still holds under the old name shows as the renamed colleague here too.</summary>
    [Fact]
    public async Task An_old_origin_still_counts_as_here()
    {
        using var desk = Desk();
        await desk.Idle();
        var handle = Incarnation("Charting");
        Assert.Null(Rename("Charting", "Plotter"));

        Assert.Equal(handle, desk.Panel()!.Rows.Single(r => r.Name == "Plotter").Handle);
        Assert.NotNull(desk.CardOf(handle));
    }

    private string Incarnation(string colleague)
    {
        var child = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(), Ledger = new TokenLedger(), MaxTurns = 5,
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
        }).Create();
        var handle = _workers.Keep(child, "charting");
        _workers.SetOrigin(handle, "colleague:" + colleague);
        return handle;
    }

    private Session Open()
    {
        var resolution = ResolvedConfig.ForTesting(new MockLlmProvider()) is var r
            ? r with { Model = r.Model! with { ContextWindow = 200_000 }, Catalog = r.Catalog with { Colleagues = true } }
            : null!;
        var session = new Session(_project);
        SessionFactory.Wire(session, resolution, new SharedServices { GlobalInstructionsDir = _config },
            new SessionPorts { Observer = _seen, ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        return session;
    }

    [Fact]
    public async Task The_verb_renames()
    {
        var session = Open();
        await session.Colleagues!.Idle();

        session.ColleaguesCommand("rename Charting to The Plotter");

        Assert.Equal("The Plotter", Library.Find("Charting")!.Name);
        Assert.Contains(_seen.Notices, n => n.Contains("Charting is now called The Plotter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_lead_is_told_of_a_rename()
    {
        var session = Open();
        await session.Colleagues!.Idle();
        Assert.Single(session.ColleagueIndex());

        Assert.Null(Rename("Charting", "Plotter"));

        var note = session.TakeColleagueNote();
        Assert.Contains("'Charting' is now called 'Plotter'", note);
        Assert.DoesNotContain("forgotten", note);
        Assert.DoesNotContain("was kept", note);
    }

    [Fact]
    public void The_draft_renames_and_keeps_its_models()
    {
        var draft = ColleagueDraft.For(Library.Find("Charting")!);
        draft.NewName = "Plotter";
        draft.Model = null;

        Assert.True(draft.IsDirty);
        Assert.Null(draft.Apply(Library, Sessions));

        var record = Library.Find("Plotter")!;
        Assert.Null(record.Instance);
        Assert.Equal(["Charting"], record.FormerNames);
    }

    [Fact]
    public void A_draft_with_a_taken_name_writes_nothing()
    {
        Library.Create(new ColleagueRecord { Name = "Maps", Created = DateTimeOffset.UtcNow });
        var draft = ColleagueDraft.For(Library.Find("Charting")!);
        draft.NewName = "Maps";
        draft.Model = null;

        Assert.NotNull(draft.Apply(Library, Sessions));

        Assert.Equal("main", Library.Find("Charting")!.Instance);
    }

    [Fact]
    public void The_web_form_carries_the_name()
    {
        var draft = CxAgent.Web.WebConfigForms.SetColleague(ColleagueDraft.For(Library.Find("Charting")!),
            new System.Text.Json.Nodes.JsonObject { ["name"] = "Plotter" });

        Assert.Equal("Plotter", draft.SavedName);
    }
}

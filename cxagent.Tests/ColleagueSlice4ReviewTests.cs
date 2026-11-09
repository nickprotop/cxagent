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
/// What the whole-slice review of slice 4 found: a forgotten worker offered back, an offer answered in one front end left
/// open in the other, renames that only the typed verb followed through, races around the distilling lock, and the
/// smaller gaps — each pinned here.
/// </summary>
public sealed class ColleagueSlice4ReviewTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("s4review-config-").FullName;
    private readonly string _project = Directory.CreateTempSubdirectory("s4review-proj-").FullName;
    private readonly SubAgentStore _workers = new();
    private readonly List<WorkerMark> _marks = [];
    private readonly List<KeepSuggestion> _offered = [];
    private readonly List<string> _told = [];

    public ColleagueSlice4ReviewTests() =>
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private FolderSessionStore Sessions => new(new AppPaths(_config));

    private async Task<ColleagueDesk> Desk()
    {
        var desk = new ColleagueDesk(new ColleagueDeskParts
        {
            Library = Library,
            Workers = _workers,
            Folder = _project,
            SessionId = () => "session",
            NoteWorker = _marks.Add,
            Models = _ => new ModelChoice(new MockLlmProvider(), "main:m", 200_000, "main"),
            Sessions = Sessions,
            Tell = _told.Add,
        });
        desk.KeepSuggested += _offered.Add;
        desk.SuggestDelay = TimeSpan.Zero;
        await desk.Idle();
        return desk;
    }

    private string Worker(string? origin = null)
    {
        var child = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(), Ledger = new TokenLedger(), MaxTurns = 5, ContextWindow = 200_000,
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
        }).Create();
        var handle = _workers.Keep(child, "charting");
        if (origin is not null) _workers.SetOrigin(handle, origin);
        return handle;
    }

    private void Stretch(string handle, string? said = null)
    {
        Assert.True(_workers.TryClaim(handle));
        _workers.AnnounceBegin(handle, "go on");
        if (said is not null) _workers.Find(handle)!.Agent.Agent.Context.Add(new ChatMessage { Role = "assistant", Content = said });
        _workers.EndSend(handle);
    }

    [Fact]
    public async Task A_forgotten_colleague_s_worker_is_never_offered_back()
    {
        using var desk = await Desk();
        var handle = Worker("colleague:Charting");
        _workers.NoteSent(handle, fromUser: true);
        _workers.NoteSent(handle, fromUser: true);

        desk.Forget("Charting");
        Stretch(handle);
        Stretch(handle);

        Assert.Empty(_offered);
    }

    [Fact]
    public async Task A_forget_found_at_a_round_end_offers_nothing_either()
    {
        using var desk = await Desk();
        var handle = Worker("colleague:Charting");
        _workers.NoteSent(handle, fromUser: true);
        _workers.NoteSent(handle, fromUser: true);
        Directory.Delete(Library.FolderOf("Charting"), recursive: true);

        Stretch(handle, "charted");

        Assert.Empty(_offered);
    }

    /// <summary>An offer answered in either front end is answered in both — the desk says so, and both listen.</summary>
    [Fact]
    public async Task Answering_an_offer_is_announced()
    {
        using var desk = await Desk();
        var answered = new List<string>();
        desk.OfferAnswered += answered.Add;
        var first = Worker();
        var second = Worker();

        desk.Dismiss(first);
        desk.KeepOffered(second);

        Assert.Equal([first, second], answered);
    }

    /// <summary>A rename made anywhere — F8, the browser, another process — is followed by an incarnation here at its next round.</summary>
    [Fact]
    public async Task A_rename_not_made_by_this_desk_is_followed_at_the_next_round()
    {
        using var desk = await Desk();
        var handle = Worker("colleague:Charting");
        var draft = ColleagueDraft.For(Library.Find("Charting")!);
        draft.NewName = "Plotter";
        Assert.Null(draft.Apply(Library, Sessions));

        Stretch(handle, "charted the panes with the legend below them");

        Assert.Equal("colleague:Plotter", _workers.OriginOf(handle));
        Assert.Contains(_marks, m => m.Origin == "colleague:Plotter");
        Assert.Equal(1, Library.RoundCount("Plotter"));
    }

    [Fact]
    public async Task What_a_round_taught_is_said_across_a_rename()
    {
        using var desk = await Desk();
        var handle = Worker("colleague:Charting");
        Stretch(handle, "charted the panes with the legend below them, as a long answer about layout " + new string('x', 400));
        Assert.Null(ColleagueRenaming.Rename(Library, Sessions, new RenameOrder("Charting", "Plotter")));

        await desk.Idle();

        Assert.DoesNotContain(_told, t => t.Contains("Charting", StringComparison.Ordinal) && t.Contains("learned", StringComparison.Ordinal));
    }

    [Fact]
    public void A_name_refused_is_found_before_a_save()
    {
        Library.Create(new ColleagueRecord { Name = "Maps", Type = "general", Created = DateTimeOffset.UtcNow });
        var draft = ColleagueDraft.For(Library.Find("Charting")!);
        draft.NewName = "maps";

        Assert.Contains("already a colleague called 'Maps'", draft.RenameProblem(Library, Sessions));
    }

    [Fact]
    public void A_held_lock_is_waited_on_a_moment()
    {
        var held = DistillingLock.TryTake(Library.Root)!;
        _ = Task.Run(async () => { await Task.Delay(300); held.Dispose(); });

        Assert.Null(ColleagueRenaming.Rename(Library, Sessions, new RenameOrder("Charting", "Plotter")));
    }

    [Fact]
    public void Presence_after_a_forget_leaves_no_folder()
    {
        var folder = Library.FolderOf("Charting");
        Library.Retire("Charting");

        Library.MarkPresent("Charting", "a");

        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(Path.Combine(Library.Root)));
    }

    [Fact]
    public void A_new_colleague_has_its_live_folder()
    {
        Assert.True(Directory.Exists(Path.Combine(Library.FolderOf("Charting"), "live")));
    }

    [Fact]
    public void Leftovers_of_a_forget_are_swept()
    {
        var aside = Path.Combine(Library.Root, ".forgotten-0123");
        Directory.CreateDirectory(Path.Combine(aside, "experience"));

        Library.SweepForgotten();

        Assert.False(Directory.Exists(aside));
    }

    [Fact]
    public async Task Freezing_by_the_verb_redraws()
    {
        using var desk = await Desk();
        var redraws = 0;
        desk.StateChanged += () => redraws++;

        desk.Freeze("Charting", true);

        Assert.True(redraws > 0);
        Assert.True(Library.Find("Charting")!.Frozen);
    }

    [Fact]
    public void Rounds_lived_frozen_are_not_counted_as_distilled()
    {
        Library.Update("Charting", r => r with { DistilledThrough = 5, Unlearned = [new SeqRange(2, 3)] });

        var facts = Assert.Single(LibraryView.Build(Library)).Facts;

        Assert.Equal(3, facts.Distilled);
        Assert.Contains("3 distilled", LibraryView.FactsLine(facts));
    }

    [Fact]
    public void Stats_count_a_renamed_colleague_once()
    {
        var at = DateTimeOffset.UtcNow;
        RunRecord Run(string name) => new(Guid.NewGuid().ToString("N"), "s", "distill:" + name, "d", 100, 0, 1, 0, "completed", at, 10, null);
        Assert.Null(ColleagueRenaming.Rename(Library, null, new RenameOrder("Charting", "Plotter")));

        var rows = StatsQuery.Distilling([Run("Charting"), Run("Plotter")], name => Library.Find(name)?.Name ?? name);

        var one = Assert.Single(rows);
        Assert.Equal(("Plotter", 2), (one.Colleague, one.Calls));
    }

    /// <summary>"Not this one" outlives a resume: it is recorded, and the resumed worker is never offered again.</summary>
    [Fact]
    public async Task Not_this_one_is_recorded_for_a_resume()
    {
        using var desk = await Desk();
        var handle = Worker();

        desk.Dismiss(handle);

        Assert.Contains(_marks, m => m.AgentId == _workers.Find(handle)!.Agent.Agent.Id && m.Declined);
        Assert.True(_workers.Declined(handle));
    }
}

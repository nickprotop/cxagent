using System.Diagnostics;
using CxAgent.Core.Agents;
using CxAgent.Core.Colleagues;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Forgetting a colleague: always asked, naming what goes and where it is live; on yes its folder goes, its one-to-one
/// with it (user, 2026-11-09), and its live incarnations carry on as ordinary workers — here at once, elsewhere when
/// their round ends. Sessions keep their own histories.
/// </summary>
public sealed class ColleagueForgetTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("forget-config-").FullName;
    private readonly string _project = Directory.CreateTempSubdirectory("forget-proj-").FullName;
    private readonly SubAgentStore _workers = new();
    private readonly List<WorkerMark> _marks = [];

    public ColleagueForgetTests()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Created = DateTimeOffset.UtcNow });
        Library.Update("Charting", r => r with { Workplaces = [new Workplace("proj-11111111", _project, 1, DateTimeOffset.UtcNow)] });
    }

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
        });
        // A NEW DESK DRAINS AT ONCE, holding the distilling lock a moment; a forget then says try again.
        await desk.Idle();
        return desk;
    }

    private void Round(string said) =>
        Library.Append("Charting", new PastRound(0, "S", _project, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new ChatMessage { Role = "user", Content = said }, new ChatMessage { Role = "assistant", Content = "ok" }]));

    private string Incarnation()
    {
        var child = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(), Ledger = new TokenLedger(), MaxTurns = 5,
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
        }).Create();
        var handle = _workers.Keep(child, "charting");
        _workers.SetOrigin(handle, "colleague:Charting");
        return handle;
    }

    [Fact]
    public async Task Asking_names_what_goes_and_where_it_is_live()
    {
        Round("one");
        Round("two");
        Library.MarkPresent("Charting", "a");
        Library.MarkPresent("Charting", "b");
        using var desk = await Desk();

        var ask = desk.AskForget("charting");

        Assert.Equal("Charting", ask.Name);
        Assert.Contains("Forget Charting for good?", ask.Text);
        Assert.Contains("2 rounds", ask.Text);
        Assert.Contains(_project, ask.Text);
        Assert.Contains("the folders themselves are untouched", ask.Text);
        Assert.Contains("one-to-one", ask.Text);
        Assert.Contains("live in 2 sessions now", ask.Text);
    }

    [Fact]
    public async Task Asking_about_nobody_says_so()
    {
        using var desk = await Desk();

        var ask = desk.AskForget("Nobody");

        Assert.Null(ask.Name);
        Assert.Contains("no colleague called 'Nobody'", ask.Text);
    }

    [Fact]
    public async Task Forget_deletes_the_folder_and_every_one_to_one()
    {
        Sessions.SaveTurn(new FolderSessionStore.ResumeTurn("ONE", [new ChatMessage { Role = "user", Content = "a" }], 1, 1, _project) { Tag = "colleague:Charting" });
        Assert.Null(ColleagueRenaming.Rename(Library, Sessions, new RenameOrder("Charting", "Plotter")));
        Sessions.SaveTurn(new FolderSessionStore.ResumeTurn("TWO", [new ChatMessage { Role = "user", Content = "a" }], 1, 1, _project) { Tag = "colleague:Plotter" });
        using var desk = await Desk();

        Assert.Contains("forgotten", desk.Forget("Plotter"));

        Assert.Null(Library.Find("Plotter"));
        Assert.Null(Library.Find("Charting"));
        Assert.Empty(Directory.EnumerateDirectories(Library.Root));
        Assert.Empty(Sessions.ListTagged("colleague:Charting"));
        Assert.Empty(Sessions.ListTagged("colleague:Plotter"));
    }

    [Fact]
    public async Task Forget_refuses_while_distilling()
    {
        using var desk = await Desk();
        using var held = DistillingLock.TryTake(Library.Root);

        Assert.Contains("being distilled", desk.Forget("Charting"));
        Assert.NotNull(Library.Find("Charting"));
    }

    [Fact]
    public async Task A_live_incarnation_here_becomes_ordinary()
    {
        using var desk = await Desk();
        var handle = Incarnation();

        desk.Forget("Charting");

        Assert.Null(_workers.OriginOf(handle));
        Assert.Contains(_marks, m => m.AgentId == _workers.Find(handle)!.Agent.Agent.Id && m.Origin is null);
    }

    /// <summary>
    /// Another session forgot it: a round ending here keeps nothing, recreates nothing, and the worker carries on plain —
    /// recorded so, so a resume does not bring it back as a colleague nobody has.
    /// </summary>
    /// <summary>
    /// FORGOTTEN ELSEWHERE AND ANOTHER KEPT UNDER ITS NAME while it worked: the round is the forgotten one's, and the new
    /// colleague does not learn from work it never did.
    /// </summary>
    [Fact]
    public async Task A_round_ending_after_its_name_was_given_to_another_feeds_nothing()
    {
        using var desk = await Desk();
        var handle = Incarnation();
        Assert.True(_workers.TryClaim(handle));
        _workers.AnnounceBegin(handle, "chart it");
        _workers.Find(handle)!.Agent.Agent.Context.Add(new ChatMessage { Role = "assistant", Content = "charted" });

        Directory.Delete(Library.FolderOf("Charting"), recursive: true);
        Library.Create(new ColleagueRecord { Name = "Charting", Created = DateTimeOffset.UtcNow.AddMinutes(1) });
        _workers.EndSend(handle);
        await desk.Idle();

        Assert.Empty(Library.Past("Charting"));
        Assert.Null(_workers.OriginOf(handle));
    }

    [Fact]
    public async Task A_round_ending_after_a_forget_elsewhere_keeps_nothing()
    {
        using var desk = await Desk();
        var handle = Incarnation();
        var id = _workers.Find(handle)!.Agent.Agent.Id;
        Assert.True(_workers.TryClaim(handle));
        _workers.AnnounceBegin(handle, "chart it");
        _workers.Find(handle)!.Agent.Agent.Context.Add(new ChatMessage { Role = "assistant", Content = "charted" });

        Directory.Delete(Library.FolderOf("Charting"), recursive: true);
        _workers.EndSend(handle);
        await desk.Idle();

        Assert.Empty(Directory.EnumerateDirectories(Library.Root));
        Assert.Null(_workers.OriginOf(handle));
        Assert.Contains(_marks, m => m.AgentId == id && m.Origin is null);
    }

    [Fact]
    public void A_dead_process_is_not_live()
    {
        using var gone = Process.Start(new ProcessStartInfo("true") { UseShellExecute = false })!;
        gone.WaitForExit();
        var live = Path.Combine(Library.FolderOf("Charting"), "live");
        Directory.CreateDirectory(live);
        File.WriteAllText(Path.Combine(live, $"{gone.Id}-x"), "");

        Assert.Equal(0, Library.LiveSessions("Charting"));
        Assert.False(File.Exists(Path.Combine(live, $"{gone.Id}-x")));
    }

    [Fact]
    public void Presence_never_makes_a_folder()
    {
        Library.MarkPresent("Nobody", "a");

        Assert.False(Directory.Exists(Library.FolderOf("Nobody")));
    }

    [Fact]
    public async Task Forgetting_refuses_while_its_one_to_one_is_open()
    {
        Sessions.SaveTurn(new FolderSessionStore.ResumeTurn("ONE", [new ChatMessage { Role = "user", Content = "a" }], 1, 1, _project) { Tag = "colleague:Charting" });
        using var open = SessionLock.TryAcquire(new AppPaths(_config).LocksDir, "ONE", LockHolder.Terminal);
        using var desk = await Desk();

        Assert.Contains("close it first", desk.Forget("Charting"));
        Assert.NotNull(Library.Find("Charting"));
    }
}

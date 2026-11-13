using CxAgent.Core.Agents;
using CxAgent.Core.Colleagues;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// <c>/colleagues distill</c>: a round distilled badly is distilled again — its last round by default, or from a round on,
/// on its own distilling model or one named for this run — folded into its current experience, after asking (user,
/// 2026-11-10). A frozen colleague is refused: frozen means its experience stays as it is.
/// </summary>
public sealed class ColleagueDistillAgainTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("redistil-config-").FullName;
    private readonly string _project = Directory.CreateTempSubdirectory("redistil-proj-").FullName;
    private readonly MockLlmProvider _own = new("own-model");
    private readonly MockLlmProvider _strong = new("strong-model");
    private readonly List<string> _told = [];
    private int? _ownWindow = 200_000;

    public ColleagueDistillAgainTests()
    {
        Library.Create(new ColleagueRecord { Name = "Care Explorer", Created = DateTimeOffset.UtcNow });
        foreach (var said in new[] { "ROUND-ONE", "ROUND-TWO", "ROUND-THREE" }) Round(said);
        Library.Update("Care Explorer", r => r with { DistilledThrough = 3, ExperienceVersion = 3 });
        Library.WriteCore("Care Explorer", "## Lessons\n- Old lesson. (learned, round 1)");
    }

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private void Round(string said) =>
        Library.Append("Care Explorer", new PastRound(0, "S", _project, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new ChatMessage { Role = "user", Content = said }, new ChatMessage { Role = "assistant", Content = said + new string('c', 600) }]));

    private async Task<ColleagueDesk> Desk()
    {
        var desk = new ColleagueDesk(new ColleagueDeskParts
        {
            Library = Library,
            Workers = new SubAgentStore(),
            Folder = _project,
            SessionId = () => "session",
            NoteWorker = _ => { },
            Models = name => name switch
            {
                null => new ModelChoice(_own, "own:own-model", _ownWindow, "own"),
                "strong" => new ModelChoice(_strong, "strong:strong-model", 200_000, "strong"),
                "small" => new ModelChoice(_strong, "small:tiny", 32_000, "small"),
                _ => null,
            },
            Tell = _told.Add,
        });
        await desk.Idle();
        return desk;
    }

    private static void Answer(MockLlmProvider model) =>
        model.EnqueueResponse(new LlmResponse { Text = "<core>\n## Lessons\n- New lesson. (observed, round 3)\n</core>", StopReason = "end_turn" });

    [Fact]
    public async Task Asking_names_its_last_round_and_the_model()
    {
        using var desk = await Desk();

        var ask = desk.AskDistill(new DistillOrder("care explorer", null, null));

        Assert.Equal(new DistillOrder("Care Explorer", 3, null), ask.Order);
        Assert.Contains("Distil round 3 of Care Explorer again", ask.Text);
        Assert.Contains("its own distilling model", ask.Text);
        Assert.Contains("current experience (v3)", ask.Text);
    }

    [Fact]
    public async Task From_a_round_names_every_round_after_it_and_a_model_named_for_this_run()
    {
        using var desk = await Desk();

        var ask = desk.AskDistill(new DistillOrder("Care Explorer", 2, "strong"));

        Assert.Contains("rounds 2–3", ask.Text);
        Assert.Contains("on strong", ask.Text);
    }

    [Theory]
    [InlineData("Nobody", null, null, "no colleague called 'Nobody'")]
    [InlineData("Care Explorer", 9L, null, "has rounds 1 to 3")]
    [InlineData("Care Explorer", 0L, null, "has rounds 1 to 3")]
    [InlineData("Care Explorer", null, "missing", "no provider entry called 'missing'")]
    [InlineData("Care Explorer", null, "small", "128")]
    public async Task What_cannot_be_distilled_is_refused_without_asking(string name, long? from, string? model, string why)
    {
        using var desk = await Desk();

        var ask = desk.AskDistill(new DistillOrder(name, from, model));

        Assert.Null(ask.Order);
        Assert.Contains(why, ask.Text);
    }

    /// <summary>Its own distilling model, unusable here, is said before asking — not after a yes.</summary>
    [Fact]
    public async Task Its_own_model_unusable_here_is_said_before_asking()
    {
        _ownWindow = null;
        using var desk = await Desk();

        var ask = desk.AskDistill(new DistillOrder("Care Explorer", null, null));

        Assert.Null(ask.Order);
        Assert.Contains("context window is not known", ask.Text);
    }

    [Fact]
    public async Task A_frozen_colleague_is_refused()
    {
        Library.Freeze("Care Explorer");
        using var desk = await Desk();

        var ask = desk.AskDistill(new DistillOrder("Care Explorer", null, null));

        Assert.Null(ask.Order);
        Assert.Contains("frozen", ask.Text);
        Assert.Contains("unfreeze", ask.Text);
    }

    [Fact]
    public async Task Its_last_round_is_distilled_again_into_its_current_experience()
    {
        using var desk = await Desk();
        Answer(_own);

        var said = await desk.DistillAgainAsync(new DistillOrder("Care Explorer", 3, null));

        Assert.Null(said);
        var asked = string.Join("\n", _own.LastMessages!.Select(m => m.Content));
        Assert.Contains("ROUND-THREE", asked);
        Assert.DoesNotContain("ROUND-TWO", asked);
        Assert.Contains("Old lesson.", asked);
        Assert.Equal(4, Library.Find("Care Explorer")!.ExperienceVersion);
        Assert.Equal(3, Library.Find("Care Explorer")!.DistilledThrough);
        Assert.Contains(_told, t => t.Contains("Care Explorer learned from its work", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_model_named_for_this_run_is_used_and_not_kept()
    {
        using var desk = await Desk();
        Answer(_strong);

        Assert.Null(await desk.DistillAgainAsync(new DistillOrder("Care Explorer", 2, "strong")));

        var asked = string.Join("\n", _strong.LastMessages!.Select(m => m.Content));
        Assert.Contains("ROUND-TWO", asked);
        Assert.Contains("ROUND-THREE", asked);
        Assert.Equal(0, _own.ChatCallCount);
        Assert.Null(Library.Find("Care Explorer")!.DistillInstance);
    }

    [Fact]
    public async Task A_distilling_under_way_says_try_again()
    {
        using var desk = await Desk();
        using var held = DistillingLock.TryTake(Library.Root);

        var said = await desk.DistillAgainAsync(new DistillOrder("Care Explorer", 3, null));

        Assert.Contains("try again", said);
        Assert.Equal(3, Library.Find("Care Explorer")!.ExperienceVersion);
    }

    [Fact]
    public async Task Rounds_lived_frozen_stay_unlearned()
    {
        Library.Update("Care Explorer", r => r with { Unlearned = [new SeqRange(2, 2)] });
        using var desk = await Desk();
        Answer(_own);

        await desk.DistillAgainAsync(new DistillOrder("Care Explorer", 1, null));

        var asked = string.Join("\n", _own.LastMessages!.Select(m => m.Content));
        Assert.Contains("ROUND-ONE", asked);
        Assert.DoesNotContain("ROUND-TWO", asked);
    }

    private Session Open()
    {
        var resolution = ResolvedConfig.ForTesting(new MockLlmProvider()) is var r
            ? r with { Model = r.Model! with { ContextWindow = 200_000 }, Catalog = r.Catalog with { Colleagues = true } }
            : null!;
        var session = new Session(_project);
        SessionFactory.Wire(session, resolution, new SharedServices { GlobalInstructionsDir = _config },
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        return session;
    }

    [Theory]
    [InlineData("Care Explorer", "Care Explorer", null, null)]
    [InlineData("care explorer from round 2", "care explorer", 2L, null)]
    [InlineData("Care Explorer on strong", "Care Explorer", null, "strong")]
    [InlineData("Care Explorer from round 2 on strong", "Care Explorer", 2L, "strong")]
    [InlineData("\"Care Explorer\" on strong from round 2", "Care Explorer", 2L, "strong")]
    [InlineData("\"Notes on Charts\" on strong", "Notes on Charts", null, "strong")]
    public void The_verb_reads_the_name_the_round_and_the_model(string typed, string name, long? from, string? model)
    {
        var session = Open();

        Assert.Equal(new DistillOrder(name, from, model), session.DistillOrderOf(typed));
    }

    /// <summary>
    /// A RE-DISTIL SAYS HOW IT ENDED (user, 2026-11-11): its question was left saying "distilling…" for good, and one that
    /// found nothing new said nothing at all — it read as a distil that never finished. One line for both front ends.
    /// </summary>
    [Fact]
    public async Task How_a_distil_again_ended_is_said_in_one_line()
    {
        using var desk = await Desk();
        var before = Library.Find("Care Explorer")!.ExperienceVersion;

        Assert.Equal("Care Explorer distilled again — nothing new to keep, experience still v3.", desk.DistilledAgainLine("Care Explorer", before));
        Library.Update("Care Explorer", r => r with { ExperienceVersion = 4 });
        Assert.Equal("Care Explorer distilled again — its experience is now v4.", desk.DistilledAgainLine("Care Explorer", before));
    }
}

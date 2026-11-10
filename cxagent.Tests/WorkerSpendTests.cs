using CxAgent.Core.Agents;
using CxAgent.Core.Colleagues;
using CxAgent.Core.Config;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Every piece of work a session pays for reaches its readouts — the panel's spend, the session's row in the usage
/// history and a run row per stretch — whoever started it: a worker the lead spawned, one brought by the user, one
/// brought back on resume or spoken to from its own tab, and a colleague's distilling (user, 2026-11-10: a colleague's
/// rounds in a resumed session and its distilling never reached the side panel or /stats).
/// </summary>
public sealed class WorkerSpendTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("worker-spend-").FullName;
    private readonly string _project;
    private readonly MockLlmProvider _model = new("work-model");

    public WorkerSpendTests()
    {
        _project = Path.Combine(_dir, "project");
        Directory.CreateDirectory(_project);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private AppPaths Paths => new(Path.Combine(_dir, "config"));
    private string Config => Paths.ConfigDir;
    private UsageHistoryStore History => new(Paths);
    private ColleagueStore Library => new(Config);

    private Session Open()
    {
        var resolution = ResolvedConfig.ForTesting(_model, "work") is var r
            ? r with { Model = r.Model! with { ContextWindow = 200_000 }, Catalog = r.Catalog with { Colleagues = true } }
            : null!;
        var session = new Session(_project);
        SessionFactory.Wire(session, resolution,
            new SharedServices { GlobalInstructionsDir = Config, History = new UsageHistoryStore(Paths) },
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.FanOut);
        return session;
    }

    private static LlmResponse Spending(string text, int input, int output) =>
        new() { Text = text, StopReason = "end_turn", Usage = new LlmUsage { InputTokens = input, OutputTokens = output } };

    private void Colleague() =>
        Library.Create(new ColleagueRecord { Name = "Care Explorer", Type = "general", Created = DateTimeOffset.UtcNow });

    private static Task<string> Bring(Session session) =>
        session.Colleagues!.BringAsync(new BringOrder("Care Explorer", "look around", null, Background: false, session.Host!.SessionId),
            CancellationToken.None);

    /// <summary>
    /// A worker the lead's own tool did not start — here, brought by the user — is still the session's spend: the panel
    /// hears of it, its stretch is a run row, and the session's row counts it, with no turn of the lead's own.
    /// </summary>
    [Fact]
    public async Task A_worker_the_lead_did_not_spawn_reaches_the_panel_the_runs_and_the_session_row()
    {
        Colleague();
        var session = Open();
        await session.Colleagues!.Idle();
        var repainted = 0;
        session.Host!.TokensUpdated += (_, _) => Interlocked.Increment(ref repainted);
        _model.EnqueueResponse(Spending("looked", 4000, 300));

        await Bring(session);

        Assert.True(await Poll.Until(() => History.RunsSince(DateTimeOffset.MinValue).Count == 1));
        var run = Assert.Single(History.RunsSince(DateTimeOffset.MinValue));
        Assert.Equal(4300, run.InputTokens + run.OutputTokens);
        Assert.True(repainted > 0);
        Assert.Equal(4300, session.Ledger!.SubAgentTokens);
        Assert.True(await Poll.Until(() => History.SessionsSince(DateTimeOffset.MinValue).Any(s => s.SubAgentTokens == 4300)));
    }

    /// <summary>
    /// A re-wire — a /model switch — leaves the outgoing lead behind; it must not account for the session's workers
    /// as well, or every stretch is a run row twice.
    /// </summary>
    [Fact]
    public async Task A_lead_replaced_by_a_rewire_does_not_count_the_work_again()
    {
        Colleague();
        var session = Open();
        var resolution = ResolvedConfig.ForTesting(_model, "work") is var r
            ? r with { Model = r.Model! with { ContextWindow = 200_000 }, Catalog = r.Catalog with { Colleagues = true } }
            : null!;
        SessionFactory.Wire(session, resolution,
            new SharedServices { GlobalInstructionsDir = Config, History = new UsageHistoryStore(Paths) },
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.FanOut);
        await session.Colleagues!.Idle();
        _model.EnqueueResponse(Spending("looked", 4000, 300));

        await Bring(session);

        Assert.True(await Poll.Until(() => History.RunsSince(DateTimeOffset.MinValue).Count >= 1));
        await Task.Delay(200);
        // THE SESSION ROWS, NOT THE RUN ROWS: both leads would file the stretch under the child's own id, and the store
        // upserts by id — the second write hides in the first. A replaced lead that still listened files its own
        // session row beside the live one.
        Assert.Single(History.RunsSince(DateTimeOffset.MinValue));
        Assert.Single(History.SessionsSince(DateTimeOffset.MinValue));
    }

    /// <summary>
    /// Distilling is the session's spend too: on its ledger as a worker's, under the same entry-and-model label every
    /// other figure uses, and in the session's row although the lead took no turn.
    /// </summary>
    [Fact]
    public async Task Distilling_reaches_the_ledger_under_the_entry_label_and_the_session_row()
    {
        Colleague();
        Library.Append("Care Explorer", new PastRound(0, "S", _project, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new ChatMessage { Role = "user", Content = "look" }, new ChatMessage { Role = "assistant", Content = new string('c', 600) }]));
        var session = Open();
        await session.Colleagues!.Idle();
        _model.EnqueueResponse(Spending("<core>\n## Lessons\n- A lesson. (observed, round 1)\n</core>", 1200, 300));

        Assert.Null(await session.Colleagues.DistillAgainAsync(new DistillOrder("Care Explorer", 1, null)));

        Assert.Equal(1500, session.Ledger!.SubAgentTokens);
        Assert.Equal(1500, session.Ledger.ByModel["work:work-model"]);
        Assert.Equal("work:work-model", Assert.Single(History.RunsSince(DateTimeOffset.MinValue)).ModelId);
        Assert.Contains(History.SessionsSince(DateTimeOffset.MinValue), s => s.SubAgentTokens == 1500);
    }
}

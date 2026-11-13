using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The per-turn history the side panel's sparklines draw (user, 2026-11-11): one point per lead model response — its
/// tokens, the workers' spend since the last point, its cache rate, and the context it sent — kept with the session and
/// brought back on resume.
/// </summary>
public sealed class TurnHistoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("turn-history-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static LlmUsage Usage(int input, int output, int? cached = null) =>
        new() { InputTokens = input, OutputTokens = output, CachedInputTokens = cached ?? 0, CacheReported = cached is not null };

    [Fact]
    public void A_point_is_the_lead_s_response_with_the_workers_spend_since_the_last()
    {
        var ledger = new TokenLedger();
        var history = new TurnHistory(Path.Combine(_dir, "turns.jsonl"));

        ledger.Record(Usage(10_000, 500, cached: 8_000), "main:m");
        history.Note(ledger.Read(), window: 200_000);
        ledger.Record(Usage(3_000, 200), "main:w", subAgent: true);
        ledger.Record(Usage(12_000, 400), "main:m");
        history.Note(ledger.Read(), window: 200_000);

        Assert.Collection(history.Points,
            p =>
            {
                Assert.Equal(10_500, p.Tokens);
                Assert.Equal(0, p.WorkerTokens);
                Assert.Equal(0.8, p.Cache!.Value, 3);
                Assert.Equal(10_000, p.Context);
            },
            p =>
            {
                Assert.Equal(12_400, p.Tokens);
                Assert.Equal(3_200, p.WorkerTokens);
                Assert.Null(p.Cache);        // this response reported no cache: a gap, never zero
                Assert.Equal(12_000, p.Context);
                Assert.Equal(200_000, p.Window);
            });
    }

    [Fact]
    public void The_history_is_kept_and_read_back()
    {
        var file = Path.Combine(_dir, "turns.jsonl");
        var ledger = new TokenLedger();
        var history = new TurnHistory(file);
        ledger.Record(Usage(5_000, 100, cached: 1_000), "main:m");
        history.Note(ledger.Read(), window: 100_000);

        var resumed = new TurnHistory(file);
        var point = Assert.Single(resumed.Points);
        Assert.Equal(5_100, point.Tokens);
        Assert.Equal(0.2, point.Cache!.Value, 3);

        // AND IT GOES ON FROM WHERE THE RESUMED LEDGER STARTS: that ledger is seeded with the session's totals, and a
        // point measured from zero would carry the whole past as one turn.
        var seeded = new TokenLedger(5_000, 100);
        resumed.Start(seeded.Read());
        seeded.Record(Usage(6_000, 50), "main:m");
        resumed.Note(seeded.Read(), window: 100_000);
        Assert.Equal(6_050, resumed.Points[^1].Tokens);
    }

    [Fact]
    public void A_damaged_line_is_skipped_not_fatal()
    {
        var file = Path.Combine(_dir, "turns.jsonl");
        File.WriteAllText(file, "{not json\n");

        Assert.Empty(new TurnHistory(file).Points);
    }

    /// <summary>
    /// A WIRED SESSION KEEPS ONE, fed by its lead's responses and filed beside its logs — where a resume, which keeps the
    /// session's id, finds it again.
    /// </summary>
    [Fact]
    public async Task A_session_records_its_lead_s_responses_beside_its_logs()
    {
        var paths = new CxAgent.Core.Storage.AppPaths(Path.Combine(_dir, "config"));
        paths.EnsureCreated();
        var logs = new CxAgent.Core.Storage.LogFileManager(paths);
        var model = new MockLlmProvider("m");
        model.EnqueueResponse(new LlmResponse { Text = "hi", StopReason = "end_turn", Usage = Usage(4_000, 20, cached: 3_000) });
        var session = new Session(Path.Combine(_dir, "work"));
        Directory.CreateDirectory(session.WorkingDirectory);
        SessionFactory.Wire(session, ResolvedConfig.ForTesting(model) is var r ? r with { Model = r.Model! with { ContextWindow = 100_000 } } : null!,
            new SharedServices { GlobalInstructionsDir = paths.ConfigDir, Logs = logs },
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);

        session.Submit("say hi");

        Assert.True(await Poll.Until(() => session.Turns!.Points.Count == 1));
        var point = session.Turns!.Points[0];
        Assert.Equal(4_020, point.Tokens);
        Assert.Equal(0.75, point.Cache!.Value, 3);
        Assert.Equal(100_000, point.Window);
        Assert.Single(new TurnHistory(Path.Combine(logs.DirFor(session.SessionId!), TurnHistory.FileName)).Points);
    }

    /// <summary>
    /// A TURN RECORDS THE WINDOW IT RAN AGAINST: after a /model switch to a 1M model, not the 212k one the session was wired
    /// with — the per-turn context line read 64% beside the gauge's 14%.
    /// </summary>
    [Fact]
    public async Task A_turn_after_a_model_switch_records_the_new_window()
    {
        var paths = new CxAgent.Core.Storage.AppPaths(Path.Combine(_dir, "config"));
        paths.EnsureCreated();
        var small = new MockLlmProvider("small");
        var big = new MockLlmProvider("big");
        big.EnqueueResponse(new LlmResponse { Text = "hi", StopReason = "end_turn", Usage = Usage(4_000, 20, cached: 3_000) });
        var session = new Session(Path.Combine(_dir, "work"));
        Directory.CreateDirectory(session.WorkingDirectory);
        SessionFactory.Wire(session, ResolvedConfig.ForTesting(small) is var r ? r with { Model = r.Model! with { ContextWindow = 212_992 } } : null!,
            new SharedServices { GlobalInstructionsDir = paths.ConfigDir, Logs = new CxAgent.Core.Storage.LogFileManager(paths) },
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        session.Use(new ActiveModel(big, "big", "big", 1_000_000));

        session.Submit("say hi");

        Assert.True(await Poll.Until(() => session.Turns!.Points.Count == 1));
        Assert.Equal(1_000_000, session.Turns!.Points[0].Window);
    }

    /// <summary>
    /// A RESUME STARTS A NEW AGENT, with logs of its own, so the history it continues is carried from the session it
    /// resumes — and from there to the next resume.
    /// </summary>
    [Fact]
    public void A_resume_carries_the_history_into_the_new_session_s_file()
    {
        var before = Path.Combine(_dir, "old", TurnHistory.FileName);
        var after = Path.Combine(_dir, "new", TurnHistory.FileName);
        var ledger = new TokenLedger();
        var past = new TurnHistory(before);
        ledger.Record(Usage(2_000, 10), "main:m");
        past.Note(ledger.Read(), window: 100_000);

        TurnHistory.Carry(before, after);
        var resumed = new TurnHistory(after);

        Assert.Single(resumed.Points);
        TurnHistory.Carry(before, after);   // never twice: the new session's own points are not overwritten
        Assert.Single(new TurnHistory(after).Points);
    }
}

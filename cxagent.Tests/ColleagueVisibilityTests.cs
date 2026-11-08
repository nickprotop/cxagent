using CxAgent.Core.Colleagues;
using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What a session says about its colleagues: one line when it opens, who is distilling while it runs, and what each
/// learned when it ends — through the session's own notices, so both front ends show it with no code of their own.
/// </summary>
public sealed class ColleagueVisibilityTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("colleague-vis-config-").FullName;
    private readonly string _project = Directory.CreateTempSubdirectory("colleague-vis-proj-").FullName;
    private readonly BufferedChatSink _seen = new();
    private readonly Scripted _model = new();

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private Session Open()
    {
        var resolution = ResolvedConfig.ForTesting(_model) is var r
            ? r with { Model = r.Model! with { ContextWindow = 200_000 }, Catalog = r.Catalog with { Colleagues = true } }
            : null!;
        var session = new Session(_project);
        SessionFactory.Wire(session, resolution, new SharedServices { GlobalInstructionsDir = _config },
            new SessionPorts { Observer = _seen, ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        return session;
    }

    private void Charting(params string[] knows)
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow, Description = "lays out charts" });
        // ONE MOMENT FOR ALL, so the line's order — most recently worked first — is the order given here.
        var at = DateTimeOffset.UtcNow;
        Library.Update("Charting", r => r with
        {
            Workplaces = [.. knows.Select(k => new Workplace(k + "-12345678", "/home/dev/source/" + k, 1, at))],
        });
    }

    private string Worker(Session session, string answer = "")
    {
        var child = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = _model, Ledger = new TokenLedger(), MaxTurns = 5, ContextWindow = 200_000,
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
        }).Create();
        child.Agent.Context.Replace(
        [
            new ChatMessage { Role = "user", Content = "lay the chart out in panes" },
            new ChatMessage { Role = "assistant", Content = answer.Length > 0 ? answer : new string('w', 400) },
        ]);
        return session.SubAgents.Keep(child, "chart layout " + Guid.NewGuid().ToString("N")[..6]);
    }

    [Fact]
    public async Task Learned_line_names_what_changed_and_the_version()
    {
        var session = Open();
        var key = Codebase.Key(_project);
        _model.Distil = $"<core>\nPanes first.\n</core>\n<codebase key=\"{key}\">\nThree panes.\n</codebase>\n<description>charts</description>";

        session.Colleagues!.Keep(new KeepOrder(Worker(session), "Charting"));
        await session.Colleagues.Idle();

        var folder = Path.GetFileName(_project);
        Assert.Contains(_seen.Notices, n => n == $"Charting learned from its work: core updated, {folder} added (v1).");
    }

    [Fact]
    public async Task Nothing_new_is_said_plainly()
    {
        var session = Open();
        _model.Distil = "NO CHANGE";

        session.Colleagues!.Keep(new KeepOrder(Worker(session), "Charting"));
        await session.Colleagues.Idle();

        Assert.Contains(_seen.Notices, n => n == "Charting: nothing new to keep from that task.");
    }

    /// <summary>A round another session appended is not this session's to announce, though this one distilled it.</summary>
    [Fact]
    public async Task Another_session_s_round_is_not_said_here()
    {
        Charting();
        Library.Append("Charting", new PastRound(0, "elsewhere", "/home/dev/source/other", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new ChatMessage { Role = "user", Content = "chart it" }, new ChatMessage { Role = "assistant", Content = new string('c', 400) }]));
        _model.Distil = "NO CHANGE";

        var session = Open();
        await session.Colleagues!.Idle();

        Assert.Empty(Library.Undistilled("Charting"));
        Assert.DoesNotContain(_seen.Notices, n => n.StartsWith("Charting", StringComparison.Ordinal));
    }

    /// <summary>
    /// A round this session appended, distilled with nothing to learn, leaves no claim behind: a review found the claim
    /// stayed set, and the next distilling of another session's round for the same colleague was announced here.
    /// </summary>
    [Fact]
    public async Task A_trivial_round_of_its_own_does_not_claim_another_session_s_later_one()
    {
        var session = Open();
        _model.Distil = "NO CHANGE";
        session.Colleagues!.Keep(new KeepOrder(Worker(session, "ok"), "Charting"));   // trivial: no call, no event
        await session.Colleagues.Idle();
        var before = _seen.Notices.Count;

        Library.Append("Charting", new PastRound(0, "elsewhere", "/home/dev/source/other", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new ChatMessage { Role = "user", Content = "chart it" }, new ChatMessage { Role = "assistant", Content = new string('c', 400) }]));
        _model.Distil = "<core>\nPanes first.\n</core>\n<description>charts</description>";
        session.Colleagues.Keep(new KeepOrder(Worker(session), "Parser"));            // this session's own: drains both
        await session.Colleagues.Idle();

        Assert.Empty(Library.Undistilled("Charting"));
        var since = _seen.Notices.Skip(before).ToList();
        Assert.DoesNotContain(since, n => n.StartsWith("Charting", StringComparison.Ordinal));
        Assert.Contains(since, n => n.StartsWith("Parser learned", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Distilling_is_listed_while_it_runs()
    {
        var session = Open();
        _model.Hold = new TaskCompletionSource();
        _model.Distil = "NO CHANGE";
        var changes = 0;
        session.Colleagues!.StateChanged += () => Interlocked.Increment(ref changes);

        session.Colleagues.Keep(new KeepOrder(Worker(session), "Charting"));
        for (var i = 0; i < 100 && session.Colleagues.Distilling.Count == 0; i++) await Task.Delay(20);

        Assert.Equal(["Charting"], session.Colleagues.Distilling);
        _model.Hold.SetResult();
        await session.Colleagues.Idle();
        Assert.Empty(session.Colleagues.Distilling);
        Assert.True(changes >= 2);
    }

    [Fact]
    public void Session_open_line_lists_colleagues_with_what_they_know()
    {
        Charting("ConsoleEx", "lazydotide");

        Open();

        Assert.Contains(_seen.Notices, n => n == "Colleagues: Charting (knows ConsoleEx, lazydotide) — /colleagues to see them.");
    }

    [Fact]
    public void An_empty_library_says_nothing_when_a_session_opens()
    {
        Open();

        Assert.DoesNotContain(_seen.Notices, n => n.StartsWith("Colleagues:", StringComparison.Ordinal));
    }

    /// <summary>The workers answer "done"; asked to distil, the answer this test set — after the hold, if one is set.</summary>
    private sealed class Scripted : ILlmProvider
    {
        public string Distil { get; set; } = "NO CHANGE";
        public TaskCompletionSource? Hold { get; set; }
        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public string ModelId => "scripted-model";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => false;

        public async Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
        {
            var distilling = messages.Any(m => m.Role == "system" && m.Content.StartsWith("You keep the experience", StringComparison.Ordinal));
            if (distilling && Hold is { } hold) await hold.Task;
            return new LlmResponse { Text = distilling ? Distil : "done", StopReason = "end_turn" };
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var r = await ChatAsync(messages, tools, ct);
            yield return new LlmStreamChunk(r.Text, null, true, null, r.StopReason);
        }
    }
}

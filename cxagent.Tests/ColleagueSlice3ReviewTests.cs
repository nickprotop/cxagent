using System.Text.Json;
using CxAgent.Core.Colleagues;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using CxAgent.Core.Web;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What the whole-slice review of slice 3 found, each pinned: recall asking for real while served, an untrusted session
/// reading nothing silently, secrets not split across parts, one recall in a one-to-one, a mis-keyed codebase retried,
/// and the round reads that reach the distiller.
/// </summary>
public sealed class ColleagueSlice3ReviewTests : IDisposable
{
    private readonly string _root = PermissionPolicy.RealFolder(Directory.CreateTempSubdirectory("s3-review-").FullName);
    private readonly string _config;
    private readonly string _project;
    private readonly string _workplace;
    private readonly AppPaths _paths;
    private readonly PermissionRulesStore _rules;

    public ColleagueSlice3ReviewTests()
    {
        _config = Path.Combine(_root, "cfg");
        _project = Path.Combine(_root, "invoice");
        _workplace = Path.Combine(_root, "consoleex");
        foreach (var d in new[] { _config, _project, _workplace }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(_workplace, "a.cs"), "x");
        _paths = new AppPaths(_config);
        _paths.EnsureCreated();
        _rules = new PermissionRulesStore(_paths);
        _rules.SetTrust(_project, TrustState.Trusted);
        _rules.SetTrust(_workplace, TrustState.Trusted);
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        Library.Update("Charting", r => r with
        {
            Workplaces = [new Workplace(Codebase.Key(_project), _project, 1, DateTimeOffset.UtcNow),
                new Workplace(Codebase.Key(_workplace), _workplace, 1, DateTimeOffset.UtcNow.AddDays(-1))],
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private long Round(string folder, string asked, string answered, IReadOnlyList<string>? alsoRead = null) =>
        Library.Append("Charting", new PastRound(0, "SESSION1", folder, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new ChatMessage { Role = "user", Content = asked }, new ChatMessage { Role = "assistant", Content = answered }])
        { AlsoRead = alsoRead ?? [] });

    private static JobParameters Call(params (string Key, object Value)[] values) =>
        new(values.ToDictionary(v => v.Key, v => (object?)v.Value));

    /// <summary>A gate that answers no and keeps what it was asked.</summary>
    private sealed class Recording : IPermissionGate
    {
        public List<PermissionRequest> Asked { get; } = [];
        public Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            lock (Asked) Asked.Add(request);
            return Task.FromResult(PermissionOutcome.ByUser);
        }
    }

    // ---- 1: recall asks, for what it would return ------------------------------------------------------------------

    private RecallTool Tool(ServedFolders served, List<PermissionRequest> asked) =>
        new(new RecallScope(Library, "Charting", served)
        {
            Ask = (request, _, _) => { asked.Add(request); return Task.FromResult(false); },
        });

    private static async Task<string> Run(RecallTool tool, JobParameters call) =>
        (await tool.ExecuteAsync(call, null!, CancellationToken.None)).Output["content"]?.ToString() ?? "";

    [Fact]
    public async Task Recall_asks_before_returning_what_was_read_outside_the_served_folders()
    {
        var outside = Round(_workplace, "lay out panes", "three panes");
        var readThere = Round(_project, "check the library", "it has toolbars", alsoRead: [_workplace]);
        var inside = Round(_project, "total it", "discount then tax");
        var served = new ServedFolders { Roots = [_project] };
        var asked = new List<PermissionRequest>();
        var tool = Tool(served, asked);

        var text = await Run(tool, Call(("action", "read"), ("round", outside)));
        Assert.Single(asked);
        Assert.Equal(_workplace, asked[0].Display);
        Assert.DoesNotContain("three panes", text);

        await Run(tool, Call(("action", "read"), ("round", readThere)));
        Assert.Equal(2, asked.Count);

        Assert.Contains("discount then tax", await Run(tool, Call(("action", "read"), ("round", inside))));
        Assert.Contains("discount then tax", await Run(tool, Call(("action", "search"), ("query", "tax"))));
        Assert.Equal(2, asked.Count);   // a search whose hits are all inside asks nothing

        served.Roots = null;
        Assert.Contains("three panes", await Run(tool, Call(("action", "read"), ("round", outside))));
        Assert.Equal(2, asked.Count);
    }

    private Session Wire(Session session, ILlmProvider model, SessionPorts? ports = null, IPermissionGate? gate = null,
        ServedFolders? served = null)
    {
        var resolution = ResolvedConfig.ForTesting(model) is var r
            ? r with { Model = r.Model! with { ContextWindow = 200_000 }, Catalog = r.Catalog with { Colleagues = true } }
            : null!;
        SessionFactory.Wire(session, resolution,
            new SharedServices { GlobalInstructionsDir = _config, Resume = new FolderSessionStore(_paths), Gate = gate, Served = served ?? new() },
            ports ?? new SessionPorts
            {
                Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel(),
                Policy = new PermissionPolicy(_project, _rules, EditMode.AcceptEdits),
            }, AgentMode.Single);
        return session;
    }

    [Fact]
    public async Task An_incarnation_s_recall_asks_through_the_session_s_gate()
    {
        var outside = Round(_workplace, "lay out panes", "three panes");
        var model = new MockLlmProvider();
        model.EnqueueResponse(LlmResponse.WithToolCall("recall", new { action = "read", round = outside }));
        model.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });
        model.EnqueueResponse(new LlmResponse { Text = "NO CHANGE", StopReason = "end_turn" });
        var gate = new Recording();
        var session = Wire(new Session(_project), model, gate: gate, served: new ServedFolders { Roots = [_project] });
        await session.BringColleagueAsync("Charting");

        await session.SendFromAgentTab("charting", "what did you lay out?");
        await session.Colleagues!.Idle();

        Assert.Contains(gate.Asked, a => a.Kind == PermissionKind.FileRead && a.Display == _workplace);
    }

    // ---- 2: an untrusted session reads nothing silently --------------------------------------------------------------

    [Fact]
    public void An_untrusted_session_reads_no_workplace_silently()
    {
        _rules.SetTrust(_project, TrustState.Untrusted);
        var policy = new PermissionPolicy(_project, _rules, EditMode.AcceptEdits).WithReadAlso(new([_workplace], null));

        Assert.False(policy.IsSilentlyAllowed(new PermissionRequest(PermissionKind.FileRead, Path.Combine(_workplace, "a.cs"), null)));
    }

    // ---- 3: a secret is hidden before a round is cut into parts ------------------------------------------------------

    [Fact]
    public async Task A_secret_on_a_part_boundary_is_hidden_whole()
    {
        const string secret = "sk-abcdefghijklmnopqrstuvwxyz0123456789";
        var header = PastRounds.Render(new PastRound(1, "SESSION1", _project, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new ChatMessage { Role = "user", Content = "k" }, new ChatMessage { Role = "assistant", Content = "" }]), full: true);
        var room = RecallTool.MaxChars - 200;
        var filler = new string('x', room - header.Length - 20);
        var seq = Round(_project, "k", filler + "\nexport OPENAI_API_KEY=" + secret);
        var tool = Tool(new ServedFolders(), []);

        var parts = new List<string>();
        for (var part = 1; part <= 3; part++)
            parts.Add(await Run(tool, Call(("action", "read"), ("round", seq), ("part", part))));

        Assert.DoesNotContain(secret, string.Concat(parts.Select(p => p.Split('\n', 2)[^1])));
    }

    // ---- 4: one recall in a one-to-one, whatever a plugin offers ----------------------------------------------------

    private sealed class Named(string name) : IAgentTool
    {
        public ToolDefinition Definition { get; } = new(name, "x", JsonSerializer.SerializeToElement(new { type = "object" }));
        public PermissionRequest? Gate(JobParameters call) => null;
        public Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct) =>
            Task.FromResult(new JobResult { Success = true, Output = new() { ["content"] = name } });
    }

    [Fact]
    public void A_one_to_one_offers_one_recall_beside_a_plugin_of_that_name()
    {
        var lead = Wire(new Session(_project), new MockLlmProvider());
        var opening = lead.Colleagues!.OneToOne("Charting", out _)!;
        var session = new Session(opening.Folder);
        session.BecomeOneToOne(opening.Setup);

        Wire(session, new MockLlmProvider(), new SessionPorts
        {
            Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel(),
            DynamicTools = () => [new Named("recall")],
        });

        Assert.Single(session.OfferedToolNames(), n => n == "recall");
    }

    // ---- 8: a tool left out is said ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_colleague_tool_left_out_is_said()
    {
        var seen = new BufferedChatSink();
        var session = Wire(new Session(_project), new MockLlmProvider(), new SessionPorts
        {
            Observer = seen, ToolObserver = new BufferedJobPanel(), Tools = [new Named("recall")],
        });

        await session.BringColleagueAsync("Charting");

        Assert.Contains(seen.Notices, n => n.Contains("recall", StringComparison.Ordinal) && n.Contains("left out", StringComparison.Ordinal));
    }

    // ---- 9, 10: the reads a round names --------------------------------------------------------------------------------

    private static ChatMessage Calls(params ToolCall[] calls) => new() { Role = "assistant", Content = "", ToolCalls = [.. calls] };

    private static ToolCall Call(string name, object args) =>
        new() { Id = Guid.NewGuid().ToString("N"), Name = name, Arguments = JsonSerializer.SerializeToElement(args) };

    [Fact]
    public void A_worker_in_a_sub_folder_reading_its_session_folder_reads_nothing_elsewhere()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_project, "sub")).FullName;
        File.WriteAllText(Path.Combine(_project, "invoice.py"), "x");

        var reads = ColleagueDesk.ReadsOf(new RoundReadsRequest([Calls(Call("read_file", new { path = Path.Combine(_project, "invoice.py") }))],
            Library.Find("Charting")!, _project) { ResolveFrom = sub });

        Assert.Empty(reads.Elsewhere);
    }

    [Fact]
    public void Only_reads_count_and_not_the_app_s_own_folder()
    {
        var opt = Directory.CreateDirectory(Path.Combine(_root, "opt")).FullName;

        var reads = ColleagueDesk.ReadsOf(new RoundReadsRequest(
            [Calls(Call("write_file", new { path = Path.Combine(opt, "w.txt"), content = "x" }),
                Call("read_file", new { path = Path.Combine(_config, "logs", "S", "out.log") }))],
            Library.Find("Charting")!, _project) { Ignore = _config });

        Assert.Empty(reads.Elsewhere);
    }

    /// <summary>
    /// A shell command that changes into a workplace reads it: a live round ran "cd ~/source/ConsoleEx && git show
    /// v2.10.9:…" to check the library's source, and with the cd target uncounted, what it learned was not filed under
    /// that codebase — its old, wrong statement there stayed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_command_run_in_a_workplace_reads_it(bool fromHome)
    {
        var cd = fromHome ? "~/" + Path.GetFileName(_workplace) : _workplace;
        var reads = ColleagueDesk.ReadsOf(new RoundReadsRequest(
            [Calls(Call("run_shell", new { command = $"cd {cd} && git show v2.10.9:src/Flows.cs | head -50" }))],
            Library.Find("Charting")!, _project) { Home = _root });

        Assert.Equal([_workplace], reads.Workplaces);
    }

    // ---- 11: a codebase's search finds its worktrees' rounds ---------------------------------------------------------

    [Fact]
    public async Task A_codebase_search_finds_rounds_lived_in_its_worktree()
    {
        Directory.CreateDirectory(Path.Combine(_workplace, ".git", "worktrees", "wt"));
        File.WriteAllText(Path.Combine(_workplace, ".git", "worktrees", "wt", "commondir"), "../..");
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "consoleex-wt")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {Path.Combine(_workplace, ".git", "worktrees", "wt")}");
        Library.Update("Charting", r => r with
        {
            Workplaces = [.. r.Workplaces.Where(w => w.Folder != _workplace), new Workplace(Codebase.Key(_workplace), _workplace, 1, DateTimeOffset.UtcNow)],
        });
        Round(worktree, "toolbars in the worktree", "toolbar spacing");

        var text = await Run(Tool(new ServedFolders(), []), Call(("action", "search"), ("query", "toolbar"), ("codebase", "consoleex")));

        Assert.Contains("toolbars in the worktree", text);   // a hit shows its first matching line
    }

    // ---- 5, 7: the distiller's answer ----------------------------------------------------------------------------------

    private async Task<DistillOutcome> Distil(string answer)
    {
        Round(_project, "chart it", new string('c', 600));
        var model = new MockLlmProvider();
        model.EnqueueResponse(new LlmResponse { Text = answer, StopReason = "end_turn" });
        return await new Distiller(Library).DrainAllAsync(_ => _ => model, CancellationToken.None);
    }

    /// <summary>A key that names a listed codebase, misspelt, is a mistake to retry — not an unlisted codebase to drop.</summary>
    [Fact]
    public async Task A_mis_keyed_listed_codebase_is_retried()
    {
        Assert.Equal(DistillOutcome.Failed, await Distil("<codebase key=\"invoice\">\nTotals round last.\n</codebase>"));

        Assert.Single(Library.Undistilled("Charting"));
    }

    [Fact]
    public async Task A_placeholder_beside_an_unlisted_part_is_retried()
    {
        Assert.Equal(DistillOutcome.Failed, await Distil("<core>\n...\n</core>\n<codebase key=\"elsewhere-12345678\">\nX.\n</codebase>"));

        Assert.Single(Library.Undistilled("Charting"));
    }
}

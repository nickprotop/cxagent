using System.Text.Json;
using CxAgent.Core.Colleagues;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A colleague brought in gets its past a recall away and its workplaces readable without asking; its one-to-one gets
/// recall and nothing more; the browser's served folders hold it from the moment they are set.
/// </summary>
public sealed class ColleagueRecallWiringTests : IDisposable
{
    private readonly string _root = PermissionPolicy.RealFolder(Directory.CreateTempSubdirectory("recall-wiring-").FullName);
    private readonly string _config;
    private readonly string _project;
    private readonly string _workplace;
    private readonly PermissionRulesStore _rules;
    private readonly AppPaths _paths;
    private readonly Core.Web.ServedFolders _served = new();

    public ColleagueRecallWiringTests()
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
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private void Charting(params string[] workplaces)
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Created = DateTimeOffset.UtcNow });
        Library.Update("Charting", r => r with
        {
            Workplaces = [.. workplaces.Select((w, i) => new Workplace(Codebase.Key(w), w, 1, DateTimeOffset.UtcNow.AddDays(-i)))],
        });
    }

    private Session Wire(Session session)
    {
        var resolution = ResolvedConfig.ForTesting(new MockLlmProvider()) is var r
            ? r with { Model = r.Model! with { ContextWindow = 200_000 }, Catalog = r.Catalog with { Colleagues = true } }
            : null!;
        SessionFactory.Wire(session, resolution,
            new SharedServices { GlobalInstructionsDir = _config, Resume = new FolderSessionStore(_paths), Served = _served },
            new SessionPorts
            {
                Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel(),
                Policy = new PermissionPolicy(_project, _rules, EditMode.AcceptEdits),
            }, AgentMode.Single);
        return session;
    }

    private async Task<(Session Session, Agent Charting)> Brought()
    {
        var session = Wire(new Session(_project));
        await session.BringColleagueAsync("Charting");
        return (session, session.SubAgents.Find("charting")!.Agent.Agent);
    }

    private static PermissionRequest Read(string path) => new(PermissionKind.FileRead, path, null);

    [Fact]
    public async Task An_incarnation_is_offered_recall()
    {
        Charting(_workplace);

        var (_, charting) = await Brought();

        Assert.Contains(RecallTool.ToolName, charting.OfferedToolNames());
    }

    [Fact]
    public async Task An_incarnation_reads_its_workplaces_without_asking()
    {
        Charting(_workplace);

        var (session, charting) = await Brought();

        Assert.True(charting.Setup.Guards.Policy!.IsSilentlyAllowed(Read(Path.Combine(_workplace, "a.cs"))));
        Assert.False(session.Policy!.IsSilentlyAllowed(Read(Path.Combine(_workplace, "a.cs"))));
    }

    [Fact]
    public async Task A_workplace_that_is_gone_is_said_not_read()
    {
        var gone = Path.Combine(_root, "moved-away");
        Charting(_workplace, gone);

        var (_, charting) = await Brought();

        var briefing = charting.Setup.Brief.CallerContext!;
        Assert.Contains("moved-away", briefing);
        Assert.Contains("gone", briefing);
    }

    [Fact]
    public void The_one_to_one_offers_recall_and_claims_no_silent_reads()
    {
        Charting(_workplace);
        var lead = Wire(new Session(_project));
        var opening = lead.Colleagues!.OneToOne("Charting", out _)!;
        var session = new Session(opening.Folder);
        session.BecomeOneToOne(opening.Setup);

        Wire(session);

        var tools = session.OfferedToolNames();
        Assert.Contains(RecallTool.ToolName, tools);
        Assert.DoesNotContain(Tool.WriteFile, tools);
        Assert.DoesNotContain(Tool.RunShell, tools);
        Assert.DoesNotContain("without asking", opening.Setup.Briefing);
        Assert.Contains("recall", opening.Setup.Briefing);
    }

    [Fact]
    public void The_version_line_names_recall() =>
        Assert.Contains("recall", ColleagueDesk.VersionLine(8));

    [Theory]
    [InlineData("""{"action":"search","query":"legend panes"}""", "recall · search \"legend panes\"")]
    [InlineData("""{"action":"read","round":9}""", "recall · round 9")]
    [InlineData("""{"action":"experience","codebase":"cxgpu"}""", "recall · experience cxgpu")]
    [InlineData("""{"action":"experience"}""", "recall · experience")]
    public void A_recall_row_says_what_it_did(string arguments, string shown) =>
        Assert.Equal(shown, Agent.DescribeCall(new ToolCall { Name = "recall", Arguments = JsonDocument.Parse(arguments).RootElement.Clone() }, null));

    /// <summary>Served folders set after the bring hold it from then on: they are read at each decision, not at spawn.</summary>
    [Fact]
    public async Task Served_roots_follow_the_web_host()
    {
        Charting(_workplace);
        var (_, charting) = await Brought();
        var policy = charting.Setup.Guards.Policy!;
        Assert.True(policy.IsSilentlyAllowed(Read(Path.Combine(_workplace, "a.cs"))));

        _served.Roots = [_project];

        Assert.False(policy.IsSilentlyAllowed(Read(Path.Combine(_workplace, "a.cs"))));
    }
}

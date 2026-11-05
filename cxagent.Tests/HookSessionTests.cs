using CxAgent.Core.Hooks;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A session's hooks end to end: read when it opens from the user's config and the repository, fired with
/// real commands, problems said once, and a changed file noticed but not applied until reviewed.
/// </summary>
public class HookSessionTests : IDisposable
{
    private readonly string _cfg = Directory.CreateTempSubdirectory("cxagent-hook-sess-cfg").FullName;
    private readonly string _repo = Directory.CreateTempSubdirectory("cxagent-hook-sess-repo").FullName;
    private readonly SessionManager _manager;
    private readonly PermissionRulesStore _rules;

    public HookSessionTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(Path.Combine(_repo, ".cxagent"));
        _manager = SessionManager.Create(new AppPaths(_cfg));
        _rules = new PermissionRulesStore(new AppPaths(_cfg));
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_cfg, recursive: true); } catch (Exception) { }
        try { Directory.Delete(_repo, recursive: true); } catch (Exception) { }
    }

    private void ProjectHooks(string block) =>
        File.WriteAllText(Path.Combine(_repo, ".cxagent", "hooks.json"), $$"""{ "hooks": {{block}} }""");

    private string Marker(string name) => Path.Combine(_repo, name);

    private string SessionStartWriting(string marker) =>
        $$"""{ "SessionStart": [ { "hooks": [ { "type": "command", "command": "echo started > {{marker}}" } ] } ] }""";

    private (Session Session, BufferedChatSink Sink) Open(bool trusted = true, HooksSettings? user = null)
    {
        if (trusted) _rules.SetTrust(_repo, TrustState.Trusted);
        var policy = new PermissionPolicy(_repo, _rules, EditMode.AcceptEdits);
        var sink = new BufferedChatSink();
        var config = ResolvedConfig.ForTesting(new MockLlmProvider());
        if (user is not null) config = config with { Catalog = config.Catalog with { Hooks = user } };
        var session = _manager.Open(_repo, config,
            new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel(), Policy = policy }, AgentMode.Single);
        return (session, sink);
    }

    private static async Task<bool> Until(Func<bool> check)
    {
        for (var i = 0; i < 100 && !check(); i++) await Task.Delay(50);
        return check();
    }

    [Fact]
    public async Task Project_hook_runs_on_open_when_trusted()
    {
        ProjectHooks(SessionStartWriting(Marker("started")));
        var (session, _) = Open();
        Assert.NotNull(session.Hooks);
        Assert.True(await Until(() => File.Exists(Marker("started"))));
    }

    [Fact]
    public async Task Project_hook_does_not_run_untrusted()
    {
        ProjectHooks(SessionStartWriting(Marker("started")));
        Open(trusted: false);
        await Task.Delay(500);
        Assert.False(File.Exists(Marker("started")));
    }

    [Fact]
    public void Session_reports_problems_at_open()
    {
        File.WriteAllText(Path.Combine(_repo, ".cxagent", "hooks.json"), "{ broken");
        var (_, sink) = Open();
        Assert.Contains(sink.Notices, m => m.Contains("hooks.json"));
    }

    [Fact]
    public void Changed_file_is_noticed_once_and_not_applied()
    {
        ProjectHooks("""{ "Stop": [ { "hooks": [ { "type": "command", "command": "true" } ] } ] }""");
        var (session, sink) = Open();
        var before = session.Hooks!.Snapshot.Hooks.Count;
        ProjectHooks("""{ "Stop": [ { "hooks": [ { "type": "command", "command": "true" }, { "type": "command", "command": "false" } ] } ] }""");
        session.CheckHookChanges();
        session.CheckHookChanges();
        Assert.Contains("/hooks review", Assert.Single(sink.Notices, m => m.Contains("changed")));
        Assert.Equal(before, session.Hooks.Snapshot.Hooks.Count);
    }

    [Fact]
    public void ReviewHooks_applies_the_change()
    {
        ProjectHooks("""{ "Stop": [ { "hooks": [ { "type": "command", "command": "true" } ] } ] }""");
        var (session, sink) = Open();
        ProjectHooks("""{ "Stop": [ { "hooks": [ { "type": "command", "command": "true" }, { "type": "command", "command": "false" } ] } ] }""");
        session.ReviewHooks();
        Assert.Equal(2, session.Hooks!.Snapshot.Hooks.Count);
        session.CheckHookChanges();
        Assert.DoesNotContain(sink.Notices, m => m.Contains("changed"));
    }

    [Fact]
    public void User_hooks_come_from_the_resolved_config()
    {
        var block = System.Text.Json.JsonDocument.Parse("""{ "Stop": [ { "hooks": [ { "type": "command", "command": "true" } ] } ] }""").RootElement.Clone();
        var (session, _) = Open(user: new HooksSettings(block));
        Assert.Equal(HookSource.User, Assert.Single(session.Hooks!.Snapshot.Hooks).Source);
    }

    [Fact]
    public async Task Close_fires_SessionEnd()
    {
        var marker = Marker("ended");
        ProjectHooks($$"""{ "SessionEnd": [ { "hooks": [ { "type": "command", "command": "echo $(cat) > {{marker}}" } ] } ] }""");
        var (session, _) = Open();
        Assert.True(_manager.Close(session));
        Assert.True(await Until(() => File.Exists(marker)));
        Assert.Contains("\"reason\":\"exit\"", File.ReadAllText(marker));
    }

    [Fact]
    public void Rewire_keeps_the_reviewed_hooks()
    {
        ProjectHooks("""{ "Stop": [ { "hooks": [ { "type": "command", "command": "true" } ] } ] }""");
        var (session, _) = Open();
        var runner = session.Hooks;
        _manager.Open(session, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel(), Policy = session.Policy });
        Assert.Same(runner, session.Hooks);
    }

    [Fact]
    public void A_resume_into_another_folder_takes_its_hooks_and_its_trust()
    {
        ProjectHooks("""{ "Stop": [ { "hooks": [ { "type": "command", "command": "true" } ] } ] }""");
        var (session, _) = Open();
        Assert.True(session.Hooks!.ProjectTrusted);

        var other = Directory.CreateTempSubdirectory("cxagent-hook-sess-other").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(other, ".git"));
            Directory.CreateDirectory(Path.Combine(other, ".cxagent"));
            File.WriteAllText(Path.Combine(other, ".cxagent", "hooks.json"),
                """{ "hooks": { "PreToolUse": [ { "hooks": [ { "type": "command", "command": "true" } ] } ] } }""");
            Assert.True(session.MoveTo(other));
            // THE OTHER FOLDER IS NOT TRUSTED, and the re-wire brings its own policy, as a resume does.
            var policy = new PermissionPolicy(other, _rules, EditMode.AcceptEdits);
            _manager.Open(session, ResolvedConfig.ForTesting(new MockLlmProvider()),
                new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel(), Policy = policy });

            Assert.Equal(Path.GetFullPath(other), session.Hooks!.Snapshot.RepoRoot);
            Assert.Equal("PreToolUse", Assert.Single(session.Hooks.Snapshot.Hooks).Event);
            Assert.False(session.Hooks.ProjectTrusted);

            // AND A REVIEW THERE STILL JUDGES BY THAT FOLDER'S TRUST.
            session.ReviewHooks();
            Assert.False(session.Hooks.ProjectTrusted);
        }
        finally { Directory.Delete(other, recursive: true); }
    }
}

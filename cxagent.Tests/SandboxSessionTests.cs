using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sandbox;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>A session's policy carries the sandbox its config asks for, and keeps the once-only notice across a
/// re-wire.</summary>
public sealed class SandboxSessionTests : IDisposable
{
    private readonly string _cfg = Directory.CreateTempSubdirectory("sbx-cfg-").FullName;
    private readonly string _work = Directory.CreateTempSubdirectory("sbx-work-").FullName;
    private readonly SessionManager _manager;
    private readonly PermissionRulesStore _rules;

    public SandboxSessionTests()
    {
        _manager = SessionManager.Create(new AppPaths(_cfg));
        _rules = new PermissionRulesStore(new AppPaths(_cfg));
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_cfg, recursive: true); } catch (Exception) { }
        try { Directory.Delete(_work, recursive: true); } catch (Exception) { }
    }

    private (Session Session, PermissionPolicy Policy) Open(SandboxSettings? sandbox)
    {
        var policy = new PermissionPolicy(_work, _rules, EditMode.AcceptEdits);
        var config = ResolvedConfig.ForTesting(new MockLlmProvider());
        config = config with { Catalog = config.Catalog with { Sandbox = sandbox } };
        var session = _manager.Open(_work, config,
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel(), Policy = policy },
            AgentMode.Single);
        return (session, policy);
    }

    [Fact]
    public void Session_policy_carries_the_configured_sandbox()
    {
        var (_, policy) = Open(SandboxSettings.AppDefault with { Network = false });
        Assert.NotNull(policy.Sandbox);
        Assert.True(policy.Sandbox.Settings.Enabled);
        Assert.False(policy.Sandbox.Settings.Network);
    }

    [Fact]
    public void No_sandbox_configured_is_off()
    {
        var (_, policy) = Open(null);
        Assert.Equal(SandboxStatusKind.Off, policy.Sandbox!.Status.Kind);
    }

    private static SandboxState Fake(SandboxStatus status) =>
        new(SandboxSettings.AppDefault, new SandboxHome(Path.GetTempPath(), null, null), () => status);

    [Fact]
    public void Not_contained_is_said_once_per_session()
    {
        var (session, policy, sink) = OpenWithSink(SandboxSettings.AppDefault);
        policy.Sandbox = Fake(new SandboxStatus(SandboxStatusKind.Unavailable, "bubblewrap is not installed — install it"));
        session.NoteSandbox();
        session.NoteSandbox();
        Assert.Single(sink.Notices, n => n.Contains("bubblewrap is not installed"));
    }

    [Fact]
    public void A_failed_sandbox_says_each_command_will_ask()
    {
        var (session, policy, sink) = OpenWithSink(SandboxSettings.AppDefault);
        policy.Sandbox = Fake(new SandboxStatus(SandboxStatusKind.Failed, "userns blocked"));
        session.NoteSandbox();
        Assert.Contains(sink.Notices, n => n.Contains("userns blocked") && n.Contains("asks before"));
    }

    [Fact]
    public void A_working_sandbox_says_nothing()
    {
        var (session, policy, sink) = OpenWithSink(SandboxSettings.AppDefault);
        policy.Sandbox = Fake(SandboxStatus.Active);
        session.NoteSandbox();
        Assert.DoesNotContain(sink.Notices, n => n.Contains("contained"));
    }

    [Fact]
    public void Use_sandbox_replaces_the_settings_and_keeps_the_notice_said()
    {
        var (session, policy, _) = OpenWithSink(SandboxSettings.AppDefault);
        policy.Sandbox!.FallbackNoticeShown = true;
        session.UseSandbox(SandboxSettings.AppDefault with { Enabled = false });
        Assert.False(policy.Sandbox.Settings.Enabled);
        Assert.True(policy.Sandbox.FallbackNoticeShown);
    }

    [Fact]
    public void Describe_names_the_status_and_the_folders()
    {
        var (session, policy, sink) = OpenWithSink(SandboxSettings.AppDefault with { Network = false });
        policy.Sandbox = new SandboxState(SandboxSettings.AppDefault with { Network = false },
            new SandboxHome(Path.GetTempPath(), null, null), () => SandboxStatus.Active);
        session.DescribeSandbox();
        var text = sink.Notices[^1];
        Assert.Contains("active", text);
        Assert.Contains(PathResolverReal(_work), text);
        Assert.Contains("network: off", text);
    }

    [Fact]
    public void Describe_says_off()
    {
        var (session, _, sink) = OpenWithSink(SandboxSettings.Off);
        session.DescribeSandbox();
        Assert.Contains("off", sink.Notices[^1]);
    }

    private static string PathResolverReal(string p) => PermissionPolicy.RealFolder(p);

    private (Session Session, PermissionPolicy Policy, BufferedChatSink Sink) OpenWithSink(SandboxSettings sandbox)
    {
        var policy = new PermissionPolicy(_work, _rules, EditMode.AcceptEdits);
        var sink = new BufferedChatSink();
        var config = ResolvedConfig.ForTesting(new MockLlmProvider());
        config = config with { Catalog = config.Catalog with { Sandbox = sandbox } };
        var session = _manager.Open(_work, config,
            new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel(), Policy = policy }, AgentMode.Single);
        return (session, policy, sink);
    }

    [Fact]
    public void The_config_dir_in_use_is_hidden()
    {
        var (_, policy) = Open(SandboxSettings.AppDefault);
        Assert.Equal(new AppPaths(_cfg).ConfigDir, policy.Sandbox!.Home.ConfigDir);
    }
}

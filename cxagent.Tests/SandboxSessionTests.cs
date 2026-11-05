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

    [Fact]
    public void The_config_dir_in_use_is_hidden()
    {
        var (_, policy) = Open(SandboxSettings.AppDefault);
        Assert.Equal(new AppPaths(_cfg).ConfigDir, policy.Sandbox!.Home.ConfigDir);
    }
}

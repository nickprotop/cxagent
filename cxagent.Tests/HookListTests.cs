using CxAgent.Core.Commands;
using CxAgent.Core.Hooks;
using Xunit;

namespace CxAgent.Tests;

/// <summary>What <c>/hooks</c> says: every hook this session has, where it came from, and whether it fires.</summary>
public class HookListTests
{
    private static HookDefinition H(string ev, HookSource source, string? matcher = null, string type = "command", string? name = null) =>
        new(ev, matcher, type, source) { Command = "./x.sh", Name = name };

    private static HookRunner Runner(bool trusted, params HookDefinition[] hooks) =>
        new(new HookRunnerDeps(new HookSnapshot(hooks, [], "/repo", new Dictionary<string, string>()),
            new HookHandlers(), () => trusted, _ => { }));

    [Fact]
    public void Lists_each_hook_with_source_and_state()
    {
        var text = HookList.Describe(Runner(false,
            H("PreToolUse", HookSource.User, "run_shell", name: "guard"),
            H("PostToolUse", HookSource.Project, name: "lint"),
            H("PreToolUse", HookSource.Local, type: "http", name: "audit"),
            H("Setup", HookSource.User, name: "boot")), []);
        Assert.Contains("guard", text);
        Assert.Contains("run_shell", text);
        Assert.Matches(@"guard.*you.*active", text);
        Assert.Matches(@"lint.*project.*not running: folder not trusted", text);
        Assert.Matches(@"audit.*local.*not supported here", text);
        Assert.Matches(@"boot.*you.*not supported here", text);
    }

    [Fact]
    public void Says_when_there_are_none()
    {
        Assert.Contains("No hooks", HookList.Describe(Runner(true), []));
    }

    [Fact]
    public void Says_which_files_changed()
    {
        var text = HookList.Describe(Runner(true, H("Stop", HookSource.Project)), ["/repo/.cxagent/hooks.json"]);
        Assert.Contains("hooks.json changed", text);
        Assert.Contains("/hooks review", text);
    }

    [Fact]
    public void Hooks_command_is_declared()
    {
        Assert.Contains(SessionCommands.All, c => c.Name == "/hooks");
    }
}

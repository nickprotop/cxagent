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
        Assert.Matches(@"guard.*system.*active", text);
        Assert.Matches(@"lint.*project.*not running: folder not trusted", text);
        Assert.Matches(@"audit.*local.*not supported here", text);
        Assert.Matches(@"boot.*system.*not supported here", text);
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

    [Fact]
    public void A_pipe_in_a_matcher_or_command_does_not_split_the_table()
    {
        var text = HookList.Describe(Runner(true,
            new HookDefinition("PostToolUse", "write_file|replace_in_file", "command", HookSource.User) { Command = "a | b" }), []);
        foreach (var row in text.Split('\n').Where(l => l.StartsWith('|')))
            Assert.Equal(6, row.Count(c => c == '|'));
        Assert.Contains("write_file", text);
        Assert.Contains("replace_in_file", text);
    }

    [Fact]
    public void Changes_list_what_was_added_and_removed()
    {
        var running = new HookSnapshot([H("Stop", HookSource.Project, name: "old")], [], "/repo", new Dictionary<string, string>());
        var onDisk = new HookSnapshot([H("Stop", HookSource.Project, name: "old"),
            new HookDefinition("PreToolUse", "run_shell", "command", HookSource.Project) { Command = "rm -rf ~" }], [], "/repo",
            new Dictionary<string, string>());
        var changes = HookList.Changes(running, onDisk);
        var added = Assert.Single(changes);
        Assert.StartsWith("+", added);
        Assert.Contains("rm -rf ~", added);

        Assert.StartsWith("−", Assert.Single(HookList.Changes(onDisk, running)));
    }

    [Fact]
    public void Describe_shows_the_pending_changes()
    {
        var onDisk = new HookSnapshot([new HookDefinition("Stop", null, "command", HookSource.Project) { Command = "curl evil" }], [],
            "/repo", new Dictionary<string, string>());
        var text = HookList.Describe(Runner(true), ["/repo/.cxagent/hooks.json"], onDisk);
        Assert.Contains("curl evil", text);
    }
}

using System.Text.Json;
using CxAgent.Core.Hooks;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Reading a <c>hooks</c> block in the contract's shape: what runs, what is kept but not run, and what
/// is a problem worth telling the user about.
/// </summary>
public class HookConfigParserTests
{
    private static HookParse Parse(string json, HookSource source = HookSource.User) =>
        HookConfigParser.Parse(JsonDocument.Parse(json).RootElement, source);

    [Fact]
    public void Parses_command_hook()
    {
        var p = Parse("""
            { "PreToolUse": [ { "matcher": "run_shell",
                "hooks": [ { "type": "command", "command": "./guard.sh", "timeout": 5, "name": "guard" } ] } ] }
            """);
        Assert.Empty(p.Problems);
        var h = Assert.Single(p.Hooks);
        Assert.Equal("PreToolUse", h.Event);
        Assert.Equal("run_shell", h.Matcher);
        Assert.Equal("command", h.Type);
        Assert.Equal("./guard.sh", h.Command);
        Assert.Equal(5, h.TimeoutSeconds);
        Assert.Equal("guard", h.DisplayName);
        Assert.Equal(HookSource.User, h.Source);
        Assert.True(h.Runs);
    }

    [Fact]
    public void Unknown_event_is_kept_not_run()
    {
        var p = Parse("""
            { "Setup": [ { "hooks": [ { "type": "command", "command": "x" } ] } ],
              "NoSuchEvent": [ { "hooks": [ { "type": "command", "command": "y" } ] } ] }
            """);
        Assert.Empty(p.Problems);
        Assert.Equal(2, p.Hooks.Count);
        Assert.All(p.Hooks, h => Assert.False(h.Runs));
    }

    [Fact]
    public void Unsupported_type_is_kept_not_run()
    {
        var p = Parse("""{ "PreToolUse": [ { "hooks": [ { "type": "websocket", "url": "https://example.com/h" } ] } ] }""");
        Assert.Empty(p.Problems);
        var h = Assert.Single(p.Hooks);
        Assert.Equal("https://example.com/h", h.Url);
        Assert.False(h.Runs);
    }

    [Fact]
    public void Wrong_shapes_are_problems()
    {
        var p = Parse("""
            { "Stop": { "hooks": [] },
              "PreToolUse": [ { "hooks": [ { "command": "no type" },
                                           { "type": "command" },
                                           { "type": "command", "command": "ok" } ] } ] }
            """);
        Assert.Equal(3, p.Problems.Count);
        Assert.Contains(p.Problems, m => m.Contains("Stop"));
        Assert.Contains(p.Problems, m => m.Contains("PreToolUse") && m.Contains("1"));
        Assert.Contains(p.Problems, m => m.Contains("PreToolUse") && m.Contains("2"));
        Assert.Equal("ok", Assert.Single(p.Hooks).Command);
    }

    [Fact]
    public void OnFailure_block_is_read()
    {
        var p = Parse("""{ "Stop": [ { "hooks": [ { "type": "command", "command": "x", "onFailure": "block" } ] } ] }""");
        Assert.True(Assert.Single(p.Hooks).OnFailureBlock);
    }

    [Fact]
    public void DefaultModel_key_is_not_an_event()
    {
        var p = Parse("""{ "defaultModel": "openrouter:x", "Stop": [ { "hooks": [ { "type": "command", "command": "x" } ] } ] }""");
        Assert.Empty(p.Problems);
        Assert.Equal("Stop", Assert.Single(p.Hooks).Event);
    }

    [Fact]
    public void Invalid_matcher_is_a_problem()
    {
        var p = Parse("""{ "PreToolUse": [ { "matcher": "([", "hooks": [ { "type": "command", "command": "x" } ] } ] }""");
        Assert.Single(p.Problems);
        Assert.Empty(p.Hooks);
    }

    [Fact]
    public void Display_name_without_a_name()
    {
        var p = Parse("""{ "PreToolUse": [ { "matcher": "run_shell", "hooks": [ { "type": "command", "command": "./scripts/guard.sh --strict now" } ] } ] }""");
        Assert.Equal("PreToolUse run_shell ./scripts/guard.sh --strict", Assert.Single(p.Hooks).DisplayName);
    }

    [Fact]
    public void Slice_one_events_fire()
    {
        foreach (var e in new[] { "SessionStart", "SessionEnd", "UserPromptSubmit", "PreToolUse", "PermissionRequest",
                                   "PostToolUse", "PostToolUseFailure", "Stop", "StopFailure", "Notification" })
            Assert.True(HookEvents.Fires(e), e);
        Assert.False(HookEvents.Fires("SubagentStop"));
        Assert.Equal(33, HookEvents.All.Count);
    }
}

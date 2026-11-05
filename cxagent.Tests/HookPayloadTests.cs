using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Hooks;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Which hooks a tool call matches, and what a hook reads on stdin: cxagent's names, with the contract's
/// other spellings beside them so a borrowed script finds what it looks for.
/// </summary>
public class HookPayloadTests
{
    private static readonly HookEventContext Ctx = new("s1", "/logs/s1/history.jsonl", "/repo", "default", "main");

    private static ToolCall Call(string name, string args) =>
        new() { Name = name, Arguments = JsonDocument.Parse(args).RootElement.Clone(), Id = "c1" };

    [Theory]
    [InlineData("Bash", "run_shell", true)]
    [InlineData("run_shell", "run_shell", true)]
    [InlineData("Edit|Write", "replace_in_file", true)]
    [InlineData("Edit|Write", "write_file", true)]
    [InlineData("Edit|Write", "read_file", false)]
    [InlineData("Read", "read_file", true)]
    [InlineData("Bash", "bash_helper", false)]
    [InlineData("csharp_.*", "csharp_definition", true)]
    public void Alias_matcher_matches_cxagent_tool(string matcher, string tool, bool matches) =>
        Assert.Equal(matches, HookNames.Matches(matcher, tool));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    public void Star_and_empty_match_all(string? matcher) => Assert.True(HookNames.Matches(matcher, "anything"));

    [Fact]
    public void Payload_has_common_fields()
    {
        var p = HookPayload.For(Ctx, "Stop", new JsonObject { ["stop_hook_active"] = false });
        Assert.Equal("s1", (string?)p["session_id"]);
        Assert.Equal("/logs/s1/history.jsonl", (string?)p["transcript_path"]);
        Assert.Equal("/repo", (string?)p["cwd"]);
        Assert.Equal("Stop", (string?)p["hook_event_name"]);
        Assert.Equal("default", (string?)p["permission_mode"]);
        Assert.Equal("main", (string?)p["agent"]);
        Assert.False((bool)p["stop_hook_active"]!);
        Assert.False(p.ContainsKey("agent_id"));
    }

    [Fact]
    public void Child_payload_carries_agent_id_and_type()
    {
        var p = HookPayload.For(Ctx with { Agent = "explorer 01J", AgentId = "01J", AgentType = "explorer" }, "Stop", []);
        Assert.Equal("01J", (string?)p["agent_id"]);
        Assert.Equal("explorer", (string?)p["agent_type"]);
    }

    [Fact]
    public void Payload_carries_both_spellings()
    {
        var p = HookPayload.ForTool(Ctx, "PreToolUse", Call("read_file", """{"path":"a.txt"}"""));
        Assert.Equal("read_file", (string?)p["tool_name"]);
        Assert.Equal("c1", (string?)p["tool_use_id"]);
        Assert.Equal("a.txt", (string?)p["tool_input"]!["path"]);
        Assert.Equal("a.txt", (string?)p["tool_input"]!["file_path"]);
    }

    [Fact]
    public void Replace_single_edit_aliases()
    {
        var p = HookPayload.ForTool(Ctx, "PreToolUse",
            Call("replace_in_file", """{"path":"a","edits":[{"pattern":"x","replacement":"y"}]}"""));
        var input = p["tool_input"]!;
        Assert.Equal("x", (string?)input["old_string"]);
        Assert.Equal("y", (string?)input["new_string"]);
        Assert.Equal("a", (string?)input["file_path"]);
    }

    [Fact]
    public void Replace_flat_shape_aliases()
    {
        var p = HookPayload.ForTool(Ctx, "PreToolUse",
            Call("replace_in_file", """{"path":"a","pattern":"x","replacement":"y"}"""));
        Assert.Equal("x", (string?)p["tool_input"]!["old_string"]);
    }

    [Fact]
    public void Replace_many_edits_aliases()
    {
        var p = HookPayload.ForTool(Ctx, "PreToolUse",
            Call("replace_in_file", """{"path":"a","edits":[{"pattern":"x","replacement":"y"},{"pattern":"p","replacement":"q"}]}"""));
        var input = p["tool_input"]!;
        Assert.False(input.AsObject().ContainsKey("old_string"));
        Assert.Equal("p", (string?)input["edits"]![1]!["old_string"]);
        Assert.Equal("q", (string?)input["edits"]![1]!["new_string"]);
        Assert.Equal("p", (string?)input["edits"]![1]!["pattern"]);
    }

    [Fact]
    public void Tool_response_and_error_are_carried()
    {
        var ok = HookPayload.ForTool(Ctx, "PostToolUse", Call("run_shell", """{"command":"ls"}"""),
            response: new JsonObject { ["output"] = "a b" });
        Assert.Equal("a b", (string?)ok["tool_response"]!["output"]);
        var bad = HookPayload.ForTool(Ctx, "PostToolUseFailure", Call("run_shell", """{"command":"ls"}"""), error: "exit 1");
        Assert.Equal("exit 1", (string?)bad["error"]);
    }

    [Fact]
    public void UpdatedInput_maps_back()
    {
        var back = HookNames.ToCxagentInput("replace_in_file",
            JsonNode.Parse("""{"file_path":"a","old_string":"x","new_string":"y"}""")!.AsObject());
        Assert.Equal("""{"path":"a","edits":[{"pattern":"x","replacement":"y"}]}""", back.ToJsonString());
    }

    [Fact]
    public void UpdatedInput_in_cxagent_names_is_kept()
    {
        var back = HookNames.ToCxagentInput("run_shell", JsonNode.Parse("""{"command":"ls -la"}""")!.AsObject());
        Assert.Equal("""{"command":"ls -la"}""", back.ToJsonString());
    }

    [Fact]
    public void UpdatedInput_drops_alias_beside_real_name()
    {
        // A hook that echoes the whole input back, edited, sends both spellings; cxagent's own wins.
        var back = HookNames.ToCxagentInput("read_file", JsonNode.Parse("""{"path":"b","file_path":"a"}""")!.AsObject());
        Assert.Equal("""{"path":"b"}""", back.ToJsonString());
    }

    [Fact]
    public void A_runaway_matcher_counts_as_no_match()
    {
        Assert.False(HookNames.Matches("(a+)+$", new string('a', 40) + "!"));
    }
}

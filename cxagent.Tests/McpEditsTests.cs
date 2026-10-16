using System.Text.Json.Nodes;
using CxAgent.Core.Config;
using Xunit;

namespace CxAgent.Tests;

public class McpEditsTests
{
    private static JsonObject Doc() => JsonNode.Parse("""
        { "providers": {}, "mcp": {
            "github": { "command": ["npx", "-y", "@mcp/github"], "env": { "GITHUB_TOKEN": "t" }, "note": "mine" },
            "docs": { "url": "https://docs.example/mcp", "enabled": false } } }
        """)!.AsObject();

    [Fact]
    public void SetEnabled_LeavesTheCommandAlone()
    {
        var root = Doc();
        McpEdits.SetEnabled(root, "github", false);

        var github = root["mcp"]!["github"]!;
        Assert.False((bool?)github["enabled"]);
        Assert.Equal(3, github["command"]!.AsArray().Count);
        Assert.Equal("mine", (string?)github["note"]);
    }

    // AN ARRAY, NEVER A STRING: the loader reads `command` only as an array and skips a string.
    [Fact]
    public void Add_WritesTheCommandAsAnArray()
    {
        var root = Doc();
        McpEdits.Add(root, "search", new McpServerFields { Command = ["uvx", "mcp-search"] });
        Assert.IsType<JsonArray>(root["mcp"]!["search"]!["command"]);
    }

    [Fact]
    public void Rename_And_Remove()
    {
        var root = Doc();
        McpEdits.Rename(root, "docs", "manual");
        Assert.Null(root["mcp"]!["docs"]);
        Assert.Equal("https://docs.example/mcp", (string?)root["mcp"]!["manual"]!["url"]);

        McpEdits.Remove(root, "manual");
        Assert.Null(root["mcp"]!["manual"]);
    }

    [Fact]
    public void Update_Clear_RemovesEnv()
    {
        var root = Doc();
        McpEdits.Update(root, "github", new McpServerFields { Clear = new HashSet<string> { "env" } });
        Assert.Null(root["mcp"]!["github"]!["env"]);
    }

    [Fact]
    public void Remove_AMissingServer_IsRefused() =>
        Assert.Throws<ConfigEditException>(() => McpEdits.Remove(Doc(), "nope"));
}

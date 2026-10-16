using CxAgent.Core.Llm;
using CxAgent.Core.Mcp;
using CxAgent.UI.Config;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The MCP tab's rows and drafts: state read from the manager, secrets never read back.</summary>
public class McpTabStateTests
{
    private static readonly Dictionary<string, McpServerConfig> Configured = new()
    {
        ["github"] = new(["npx", "-y", "@mcp/github"]),
        ["docs"] = new([], Url: "https://docs.example/mcp"),
        ["search"] = new(["uvx", "mcp-search"]),
        ["old"] = new(["x"], Enabled: false),
    };

    [Fact]
    public void States_ComeFromTheManager()
    {
        var rows = McpRows.For(Configured, [
            new("github", true, 12, null),
            new("docs", true, 0, "401", NeedsAuth: true),
            new("search", true, 0, "exited with code 1"),
            new("old", false, 0, null),
        ]);

        Assert.Equal(["docs", "github", "old", "search"], rows.Select(r => r.Name));
        Assert.Equal(McpState.Running, rows.Single(r => r.Name == "github").State);
        Assert.Equal(12, rows.Single(r => r.Name == "github").Tools);
        Assert.Equal(McpState.NeedsLogin, rows.Single(r => r.Name == "docs").State);
        Assert.Equal(McpState.Failed, rows.Single(r => r.Name == "search").State);
        Assert.Equal("exited with code 1", rows.Single(r => r.Name == "search").Reason);
        Assert.Equal(McpState.Off, rows.Single(r => r.Name == "old").State);
        Assert.Equal("npx -y @mcp/github", rows.Single(r => r.Name == "github").Target);
    }

    [Fact]
    public void AServerTheManagerDoesNotKnowYet_IsNotStarted() =>
        Assert.Equal(McpState.NotStarted, McpRows.For(Configured, []).Single(r => r.Name == "github").State);

    [Fact]
    public void TheCommandLine_IsSplitAsAShellWould() =>
        Assert.Equal(["npx", "-y", "@scope/pkg", "a b"],
            new McpDraft("x", new McpServerConfig(["npx"])) { CommandLine = "npx -y @scope/pkg \"a b\"" }.ToFields().Command);

    [Fact]
    public void EnvValues_AreNeverReadBack()
    {
        var draft = new McpDraft("x", new McpServerConfig(["c"], Environment: new Dictionary<string, string> { ["T"] = "secret" }));
        Assert.Null(draft.NewEnv);
        Assert.Null(draft.ToFields().Env);
    }

    [Fact]
    public void AnUntouchedDraft_WritesNothing()
    {
        var fields = new McpDraft("github", Configured["github"]).ToFields();
        Assert.Null(fields.Command);
        Assert.Null(fields.Url);
        Assert.Empty(fields.Clear);
    }
}

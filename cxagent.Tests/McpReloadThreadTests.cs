using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// /mcp reload does not hold the UI thread while it reconnects.
///
/// <para>A SOURCE CHECK, as PromptStaysPutTests is one: the stall it guards against needs a live stdio server that
/// ignores its stdin closing, and the rule it pins is where the reload runs. Disposing each old server waits up to a
/// second for it to exit; on the UI thread, that is a second of frozen window per server.</para>
/// </summary>
public class McpReloadThreadTests
{
    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }

    [Fact]
    public void TheReload_RunsOnThePool()
    {
        var source = File.ReadAllText(Path.Combine(Repo(), "cxagent", "UI", "McpCommand.cs"));

        Assert.Contains("await Task.Run(() => mcp.ReloadAsync(", source, StringComparison.Ordinal);
    }
}

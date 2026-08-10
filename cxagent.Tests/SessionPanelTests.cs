using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What the Context block reports. The panel exists to answer "will the next turn fit", and for the
/// life of this panel it answered a different question — how much the session had cost — while
/// presenting it as occupancy.
/// </summary>
public class SessionPanelTests
{
    /// <summary>
    /// The percentage is OCCUPANCY over the window, never the cumulative spend over it.
    ///
    /// <para>Measured live (session 01KZKV3NZ4YCV0YMBD0NKV430T, three prompts): the panel displayed
    /// "19,559 tokens · 9% of 213.0k" while the turn logs put the context at 4,441 input tokens —
    /// 2%. 19,559 is the SUM of the four turns' readings, which is a cost, not a size. Every turn
    /// re-sends the whole conversation, so that sum grows quadratically and passes 100% of any
    /// window on a session that is barely started.</para>
    ///
    /// <para>A sum also cannot fall, which is the second half of the same bug: compress, and the
    /// gauge does not move.</para>
    /// </summary>
    [Fact]
    public void Refresh_ShowsOccupancyOverTheWindow_NotTheCumulativeSpend()
    {
        var panel = new SessionPanel();
        panel.Refresh(contextUsed: 4_441, spentTokens: 19_559, contextWindow: 212_992,
            model: "m", endpoint: "", rules: 0);

        Assert.Contains("2%", panel.RenderedText, StringComparison.Ordinal);
        Assert.DoesNotContain("9%", panel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Occupancy is not known until a provider reports usage. No reading, no percentage — a guessed
    /// denominator is worse than none, and 0% reads as "empty" rather than "unknown".
    /// </summary>
    [Fact]
    public void Refresh_OmitsThePercentage_WhenOccupancyIsUnknown()
    {
        var panel = new SessionPanel();
        panel.Refresh(contextUsed: null, spentTokens: 19_559, contextWindow: 212_992,
            model: "m", endpoint: "", rules: 0);

        Assert.DoesNotContain("%", panel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The spend is still shown — it is a real number and "what has this session cost" is a real
    /// question. It just is not the percentage, and it is not labelled as context.
    /// </summary>
    [Fact]
    public void Refresh_StillReportsTheCumulativeSpend()
    {
        var panel = new SessionPanel();
        panel.Refresh(contextUsed: 4_441, spentTokens: 19_559, contextWindow: 212_992,
            model: "m", endpoint: "", rules: 0);

        Assert.Contains("19,559", panel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A percentage needs a denominator. With no configured window the occupancy is still worth
    /// showing as a count; inventing a window to divide by would put a confident figure on a guess.
    /// </summary>
    [Fact]
    public void Refresh_ShowsTheCount_ButNoPercentage_WhenTheWindowIsUnknown()
    {
        var panel = new SessionPanel();
        panel.Refresh(contextUsed: 4_441, spentTokens: 19_559, contextWindow: null,
            model: "m", endpoint: "", rules: 0);

        Assert.Contains("4,441", panel.RenderedText, StringComparison.Ordinal);
        Assert.DoesNotContain("%", panel.RenderedText, StringComparison.Ordinal);
    }

    // ---- MCP servers ---------------------------------------------------------------------------

    private static CxAgent.Core.Mcp.McpServerStatus Server(string name, bool enabled = true,
        int tools = 2, string? error = null) => new(name, enabled, tools, error);

    /// <summary>Connected servers are named with their tool count — the count is what tells the user
    /// the server actually handshook rather than merely started.</summary>
    [Fact]
    public void Refresh_NamesEachConnectedServer_AndItsToolCount()
    {
        var panel = new SessionPanel();
        panel.Refresh(contextUsed: 1, spentTokens: 1, contextWindow: 1000,
            model: "m", endpoint: "", rules: 0,
            mcpServers: [Server("context7", tools: 2)]);

        Assert.Contains("context7", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("2 tools", panel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A server that FAILED is shown, not hidden. Hiding it is indistinguishable from not having
    /// configured it, and the user configured it — silence is the one outcome that gives them
    /// nothing to act on.
    /// </summary>
    [Fact]
    public void Refresh_ShowsAFailedServer_RatherThanOmittingIt()
    {
        var panel = new SessionPanel();
        panel.Refresh(contextUsed: 1, spentTokens: 1, contextWindow: 1000,
            model: "m", endpoint: "", rules: 0,
            mcpServers: [Server("broken", tools: 0, error: "npx: command not found")]);

        Assert.Contains("broken", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("failed", panel.RenderedText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A DISABLED server is absent. It is off on purpose; a line saying so every session is
    /// noise about a decision already made.</summary>
    [Fact]
    public void Refresh_OmitsADisabledServer()
    {
        var panel = new SessionPanel();
        panel.Refresh(contextUsed: 1, spentTokens: 1, contextWindow: 1000,
            model: "m", endpoint: "", rules: 0,
            mcpServers: [Server("off", enabled: false, tools: 0)]);

        Assert.DoesNotContain("off", panel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// NO SERVERS, NO SECTION. The common case is no MCP configured at all, and a heading over an
    /// empty list costs two lines of a panel measured in lines — the same rule the session-id guard
    /// already follows.
    /// </summary>
    [Fact]
    public void Refresh_OmitsTheSectionEntirely_WhenNoServersAreConfigured()
    {
        var panel = new SessionPanel();
        panel.Refresh(contextUsed: 1, spentTokens: 1, contextWindow: 1000,
            model: "m", endpoint: "", rules: 0);

        Assert.DoesNotContain("MCP", panel.RenderedText, StringComparison.OrdinalIgnoreCase);
    }
}

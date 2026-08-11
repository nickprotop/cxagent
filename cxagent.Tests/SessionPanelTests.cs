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

    // ---- agent types ----------------------------------------------------------------------------

    /// <summary>
    /// CONFIGURED TYPES ARE VISIBLE. A user who wrote three types in config has no other way to
    /// confirm the session picked them up — the tool description is read by the model, not by them.
    /// </summary>
    [Fact]
    public void Refresh_ShowsConfiguredAgentTypes()
    {
        var panel = new SessionPanel();
        panel.Refresh(contextUsed: 100, spentTokens: 0, contextWindow: 1000,
            model: "m", endpoint: "", rules: 0,
            agentTypes: ["general", "explore", "review"]);

        Assert.Contains("Agent types", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("explore", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("review", panel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// `general` ALONE IS NOT A SECTION. The catalog always holds it, so showing the catalog would
    /// put a permanent line on every session including the ones that never spawn. What earns a line
    /// is that the user CONFIGURED something — the same rule the MCP block follows, where a section
    /// with nothing to say is absent rather than empty.
    /// </summary>
    [Fact]
    public void Refresh_WithOnlyTheImplicitGeneral_ShowsNoSection()
    {
        var panel = new SessionPanel();
        panel.Refresh(contextUsed: 100, spentTokens: 0, contextWindow: 1000,
            model: "m", endpoint: "", rules: 0,
            agentTypes: ["general"]);

        Assert.DoesNotContain("Agent types", panel.RenderedText, StringComparison.Ordinal);
    }

    // ---- spend by model -------------------------------------------------------------------------

    /// <summary>
    /// TWO MODELS EARN A BREAKDOWN. It appears the moment a second model is involved — today that
    /// means a sub-agent type on another provider instance, which is exactly when "what did that
    /// cost" stops having an obvious answer.
    /// </summary>
    [Fact]
    public void Refresh_WithTwoModels_ShowsSpendForEach()
    {
        var panel = new SessionPanel();
        panel.Refresh(contextUsed: 100, spentTokens: 4_500, contextWindow: 1000,
            model: "m", endpoint: "", rules: 0,
            spendByModel: new Dictionary<string, int> { ["qwen3.6-35b.gguf"] = 4_000, ["qwen3-1b.gguf"] = 500 });

        Assert.Contains("Spend by model", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("qwen3.6-35b", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("qwen3-1b", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("4,000", panel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// ONE MODEL NEEDS NO BREAKDOWN. The session total already says it, and a section repeating that
    /// number under a heading costs space to say nothing — the same rule the MCP and agent-type
    /// blocks follow.
    /// </summary>
    [Fact]
    public void Refresh_WithOneModel_ShowsNoBreakdown()
    {
        var panel = new SessionPanel();
        panel.Refresh(contextUsed: 100, spentTokens: 4_000, contextWindow: 1000,
            model: "m", endpoint: "", rules: 0,
            spendByModel: new Dictionary<string, int> { ["qwen3.6-35b.gguf"] = 4_000 });

        Assert.DoesNotContain("Spend by model", panel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// TWO SIMILAR IDS MUST STILL READ AS TWO. Found live: trimming only the tail rendered
    /// "qwen3.6-35b-a3b-ud-iq4_xs" and "…-iq4_xs-alt" as the SAME string, so the breakdown showed two
    /// rows that told the reader nothing. Local model ids share long prefixes and differ in the
    /// suffix — the quantisation, a variant tag — which is exactly what a tail-trim throws away.
    /// </summary>
    [Fact]
    public void Refresh_WithSimilarModelIds_KeepsThemDistinguishable()
    {
        var panel = new SessionPanel();
        panel.Refresh(contextUsed: 100, spentTokens: 900, contextWindow: 1000,
            model: "m", endpoint: "", rules: 0,
            spendByModel: new Dictionary<string, int>
            {
                ["qwen3.6-35b-a3b-ud-iq4_xs.gguf"] = 600,
                ["qwen3.6-35b-a3b-ud-iq4_xs-alt.gguf"] = 300,
            });

        var lines = panel.RenderedText.Split('\n')
            .Where(l => l.Contains('·') && (l.Contains("600") || l.Contains("300")))
            .Select(l => l.Trim()).ToList();

        Assert.Equal(2, lines.Count);
        Assert.NotEqual(lines[0].Split('·')[0], lines[1].Split('·')[0]);
    }

    /// <summary>A model that spent nothing is not a model that was used — it is a configured type
    /// nobody spawned, and listing it at zero invites the reader to wonder what went wrong.</summary>
    [Fact]
    public void Refresh_IgnoresModelsThatSpentNothing()
    {
        var panel = new SessionPanel();
        panel.Refresh(contextUsed: 100, spentTokens: 4_000, contextWindow: 1000,
            model: "m", endpoint: "", rules: 0,
            spendByModel: new Dictionary<string, int> { ["used.gguf"] = 4_000, ["unused.gguf"] = 0 });

        Assert.DoesNotContain("Spend by model", panel.RenderedText, StringComparison.Ordinal);
        Assert.DoesNotContain("unused", panel.RenderedText, StringComparison.Ordinal);
    }
}

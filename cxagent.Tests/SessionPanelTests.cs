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
        // THE STATUS BAR NOW OWNS THIS. The panel stopped rendering occupancy when its Context block
        // was cut for duplicating `ctx 4% · 9,140/212,992 · 160,084 spent` — but the invariant is
        // about a bug seen in a live session, not about which control shows it, so the test follows
        // the number rather than dying with the block.
        var text = MainWindow.ContextLabelForTest(used: 4_441, spent: 19_559, window: 212_992);

        Assert.Contains("2%", text, StringComparison.Ordinal);
        Assert.DoesNotContain("9%", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Occupancy is not known until a provider reports usage. No reading, no percentage — a guessed
    /// denominator is worse than none, and 0% reads as "empty" rather than "unknown".
    /// </summary>
    [Fact]
    public void Refresh_OmitsThePercentage_WhenOccupancyIsUnknown()
    {
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = null,
            SpentTokens = 19_559,
            ContextWindow = 212_992,
            Endpoint = "",
            Rules = 0,
        });

        Assert.DoesNotContain("%", panel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The spend is still shown — it is a real number and "what has this session cost" is a real
    /// question. It just is not the percentage, and it is not labelled as context.
    /// </summary>
    [Fact]
    public void Refresh_StillReportsTheCumulativeSpend()
    {
        // Also the status bar's now, and still labelled — the point was never where it appears but
        // that it appears SEPARATELY from occupancy and can never be mistaken for "how full".
        var text = MainWindow.ContextLabelForTest(used: 4_441, spent: 19_559, window: 212_992);

        Assert.Contains("19,559", text, StringComparison.Ordinal);
        Assert.Contains("spent", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A percentage needs a denominator. With no configured window the occupancy is still worth
    /// showing as a count; inventing a window to divide by would put a confident figure on a guess.
    /// </summary>
    [Fact]
    public void Refresh_ShowsTheCount_ButNoPercentage_WhenTheWindowIsUnknown()
    {
        var text = MainWindow.ContextLabelForTest(used: 4_441, spent: 19_559, window: null);

        Assert.Contains("4,441", text, StringComparison.Ordinal);
        Assert.DoesNotContain("%", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE PANEL DOES NOT REPEAT THE STATUS BAR. Occupancy, the window, the percentage and the spend
    /// total all appear in `ctx 4% · 9,140/212,992 · 160,084 spent`, which is always on screen — the
    /// panel can be hidden. Four duplicated lines is also how two readouts drift apart, which is the
    /// reason the model block was moved out earlier.
    /// </summary>
    [Fact]
    public void Refresh_DoesNotRepeatWhatTheStatusBarAlreadyShows()
    {
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 9_140,
            SpentTokens = 160_084,
            ContextWindow = 212_992,
            Endpoint = "",
            Rules = 0,
            SpendByModel = new Dictionary<string, int> { ["qwen3.6-35b.gguf"] = 160_084 },
            SplitByModel = new Dictionary<string, (int Input, int Output)>
            {
                ["qwen3.6-35b.gguf"] = (153_100, 6_900),
            },
        });

        var text = panel.RenderedText;

        // Occupancy and the percentage are the status bar's alone.
        Assert.DoesNotContain("9,140", text, StringComparison.Ordinal);
        Assert.DoesNotContain("%", text, StringComparison.Ordinal);

        // The BREAKDOWN stays: the status bar has one total, and what the panel adds is who spent it
        // and how it splits — figures the bar has no room for.
        Assert.Contains("153.1k", text, StringComparison.Ordinal);
        Assert.Contains("6.9k", text, StringComparison.Ordinal);
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
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 1,
            SpentTokens = 1,
            ContextWindow = 1000,
            Endpoint = "",
            Rules = 0,
            McpServers = [Server("context7", tools: 2)],
        });

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
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 1,
            SpentTokens = 1,
            ContextWindow = 1000,
            Endpoint = "",
            Rules = 0,
            McpServers = [Server("broken", tools: 0, error: "npx: command not found")],
        });

        Assert.Contains("broken", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("failed", panel.RenderedText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A DISABLED server is absent. It is off on purpose; a line saying so every session is
    /// noise about a decision already made.</summary>
    [Fact]
    public void Refresh_OmitsADisabledServer()
    {
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 1,
            SpentTokens = 1,
            ContextWindow = 1000,
            Endpoint = "",
            Rules = 0,
            McpServers = [Server("off", enabled: false, tools: 0)],
        });

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
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 1,
            SpentTokens = 1,
            ContextWindow = 1000,
            Endpoint = "",
            Rules = 0,
        });

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
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 100,
            SpentTokens = 0,
            ContextWindow = 1000,
            Endpoint = "",
            Rules = 0,
            AgentTypes = ["general", "explore", "review"],
        });

        Assert.Contains("Agent types", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("explore", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("review", panel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// `general` ALONE IS STILL A SECTION, and this asserted the opposite.
    ///
    /// <para>The old rule was "a line is earned by the user CONFIGURING something", which reads the
    /// panel as a summary of config. It is a summary of what the SESSION CAN DO — and `general` is a
    /// real capability, the one a bare spawn uses. Hiding it meant anyone who had not written a type
    /// of their own saw nothing, and had no way to tell that delegation was available at all.</para>
    /// </summary>
    [Fact]
    public void Refresh_WithOnlyTheImplicitGeneral_StillShowsIt()
    {
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 100,
            SpentTokens = 0,
            ContextWindow = 1000,
            Endpoint = "",
            Rules = 0,
            AgentTypes = ["general"],
        });

        Assert.Contains("Agent types", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("general", panel.RenderedText, StringComparison.Ordinal);
    }

    // ---- spend by model -------------------------------------------------------------------------

    /// <summary>
    /// TWO INSTANCES EARN A BREAKDOWN, and it is keyed by instance:model rather than by model.
    /// Two `providers` entries can serve the SAME model against different endpoints with different
    /// windows — merging them into one row answers nothing about what was actually called.
    /// </summary>
    [Fact]
    public void Refresh_WithTwoModels_ShowsSpendForEach()
    {
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 100,
            SpentTokens = 4_500,
            ContextWindow = 1000,
            Endpoint = "",
            Rules = 0,
            SpendByModel = new Dictionary<string, int> { ["qwen3.6-35b.gguf"] = 4_000, ["qwen3-1b.gguf"] = 500 },
        });

        Assert.Contains("Tokens by instance", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("qwen3.6-35b", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("qwen3-1b", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("4,000", panel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// ONE MODEL STILL GETS A BREAKDOWN, because the breakdown now carries something the total does
    /// not: the ↑/↓ split.
    ///
    /// <para>The old rule — suppress below two models, since a lone row only restates the session
    /// total — was right while the row held nothing but that total. It is what made a whole fan-out
    /// run show no attribution at all: children normally run on the PARENT'S provider, so a
    /// spawn-heavy session has exactly one model id and the section hid itself.</para>
    /// </summary>
    [Fact]
    public void Refresh_WithOneModel_StillShowsTheSplit()
    {
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 100,
            SpentTokens = 4_000,
            ContextWindow = 1000,
            Endpoint = "",
            Rules = 0,
            SpendByModel = new Dictionary<string, int> { ["qwen3.6-35b.gguf"] = 4_000 },
            SplitByModel = new Dictionary<string, (int Input, int Output)>
            {
                ["qwen3.6-35b.gguf"] = (3_800, 200),
            },
        });

        Assert.Contains("Tokens by instance", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("↑3.8k", panel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// EVERY FIGURE IS LABELLED WITH ITS UNIT. Both blocks render bare numbers — "workers · 730" says
    /// nothing about what 730 IS, and the reader's guess (stars? kilobytes? currency?) is as good as
    /// any. The status bar escapes this only because "160,084 spent" has a verb doing the work.
    ///
    /// <para>The unit lives in the HEADING rather than on each line: repeating "tokens" four times
    /// would spend columns this panel does not have to say one thing four times.</para>
    /// </summary>
    [Fact]
    public void Refresh_NamesTheUnit_OnEveryFigureBlock()
    {
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 100,
            SpentTokens = 895,
            ContextWindow = 1000,
            Endpoint = "",
            Rules = 0,
            SpendByModel = new Dictionary<string, int> { ["qwen3.6-35b.gguf"] = 895 },
            SubAgentTokens = 730,

            // WHAT PRODUCTION ACTUALLY FEEDS. SpentTokens is the PARENT'S OWN spend — the status bar
            // is this agent's readout and is fed OwnSpend — while SubAgentTokens is the session-wide
            // worker total. This test used to set SpentTokens to the session total (895) and assert
            // "this agent · 165", i.e. 895 - 730. That encoded a subtraction the app never performs
            // on comparable numbers: in the real wiring both terms came from different scopes, so
            // "this agent" clamped to 0 the moment any worker spent anything, and the test passed
            // while the panel was wrong.
            OwnTokens = 165,
        });

        var text = panel.RenderedText;

        Assert.Contains("Tokens by instance", text, StringComparison.Ordinal);
        Assert.Contains("Tokens by agent", text, StringComparison.Ordinal);

        Assert.Contains("workers · 730", text, StringComparison.Ordinal);
        Assert.Contains("this agent · 165", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE REGRESSION: "this agent" showed 0 whenever workers had spent anything.
    ///
    /// <para>It was rendered as <c>SpentTokens - SubAgentTokens</c>, but those come from different
    /// scopes — SpentTokens is the parent's OWN spend, SubAgentTokens is the session-wide worker
    /// total. On any real fan-out the workers' figure exceeds the parent's own, so the subtraction
    /// went negative and Math.Max clamped it to zero. A measured value cannot drift like a derived
    /// one can.</para>
    /// </summary>
    [Fact]
    public void Refresh_ShowsThisAgentsOwnSpend_EvenWhenWorkersSpentMore()
    {
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            Endpoint = "",
            Rules = 0,

            // The shape of a real fan-out: workers dominate, and the parent's own spend is smaller
            // than theirs — exactly the case the old subtraction rendered as 0.
            SubAgentTokens = 9_000,
            SpentTokens = 1_200,
            OwnTokens = 1_200,
        });

        var text = panel.RenderedText;

        Assert.Contains("workers · 9,000", text, StringComparison.Ordinal);
        Assert.Contains("this agent · 1,200", text, StringComparison.Ordinal);
        Assert.DoesNotContain("this agent · 0", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// NO SPLIT, NO LINE. A provider that reports no usage breakdown — a local llama.cpp build often
    /// does not — must not get "↑0 ↓0", which reads as a measurement of nothing rather than as the
    /// absence of a measurement. Same rule occupancy follows.
    /// </summary>
    [Fact]
    public void Refresh_OmitsTheSplit_WhenTheProviderReportedNone()
    {
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 100,
            SpentTokens = 4_000,
            ContextWindow = 1000,
            Endpoint = "",
            Rules = 0,
            SpendByModel = new Dictionary<string, int> { ["qwen3.6-35b.gguf"] = 4_000 },
        });

        Assert.Contains("4,000", panel.RenderedText, StringComparison.Ordinal);
        Assert.DoesNotContain("↑", panel.RenderedText, StringComparison.Ordinal);
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
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 100,
            SpentTokens = 900,
            ContextWindow = 1000,
            Endpoint = "",
            Rules = 0,
            SpendByModel = new Dictionary<string, int>
            {
                ["qwen3.6-35b-a3b-ud-iq4_xs.gguf"] = 600,
                ["qwen3.6-35b-a3b-ud-iq4_xs-alt.gguf"] = 300,
            },
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
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 100,
            SpentTokens = 4_000,
            ContextWindow = 1000,
            Endpoint = "",
            Rules = 0,
            SpendByModel = new Dictionary<string, int> { ["used.gguf"] = 4_000, ["unused.gguf"] = 0 },
        });

        // The FILTER is the invariant, not the section hiding. This used to also assert the heading
        // was absent — true only as a side effect, since filtering left a single model and a lone
        // model then suppressed the block. The block now earns its place with the ↑/↓ split, so what
        // matters is that a model which spent nothing is not listed at all.
        // "used", not "used.gguf" — Short() strips the extension every local model carries.
        Assert.DoesNotContain("unused", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("used · 4,000", panel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// TWO INSTANCES SERVING ONE MODEL STAY SEPARATE. This is the case the whole key exists for: a
    /// user with `local` and `small` pointed at the same server, different windows, would otherwise
    /// see one merged row and no way to tell which endpoint the spend went to.
    /// </summary>
    [Fact]
    public void Refresh_TwoInstancesOfOneModel_AreNotMerged()
    {
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 100,
            SpentTokens = 3_000,
            ContextWindow = 1000,
            Endpoint = "",
            SpendByModel = new Dictionary<string, int>
            {
                ["local:qwen3"] = 2_000,
                ["small:qwen3"] = 1_000,
            },
        });

        var text = panel.RenderedText;
        Assert.Contains("local:qwen3", text, StringComparison.Ordinal);
        Assert.Contains("small:qwen3", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE INSTANCE SURVIVES TRUNCATION. A long model id is shortened to fit 24 columns, but the
    /// instance is the part the user chose and the part that tells two rows apart — trimming it
    /// would undo the reason the breakdown is keyed this way.
    /// </summary>
    [Fact]
    public void Refresh_ShorteningALongId_KeepsTheInstanceWhole()
    {
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 100,
            SpentTokens = 3_000,
            ContextWindow = 1000,
            Endpoint = "",
            SpendByModel = new Dictionary<string, int>
            {
                ["local:qwen3.6-35b-a3b-ud-iq4_xs.gguf"] = 2_000,
                ["small:qwen3.6-35b-a3b-ud-iq4_xs.gguf"] = 1_000,
            },
        });

        var text = panel.RenderedText;
        Assert.Contains("local:", text, StringComparison.Ordinal);
        Assert.Contains("small:", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// BOTH FIGURES: per instance, and a session total. Per instance because which endpoint is
    /// spending is the actionable fact; a total because "what has this cost me" is the question
    /// actually being asked, and making the reader add rows is arithmetic the panel can do.
    /// </summary>
    [Fact]
    public void Refresh_ShowsCostPerInstance_AndASessionTotal()
    {
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            Endpoint = "", Rules = 0,
            SpendByModel = new Dictionary<string, int>
            {
                ["openrouter:gemini"] = 33_416,
                ["other:model"] = 1_000,
            },
            CostByInstance = new Dictionary<string, decimal>
            {
                ["openrouter:gemini"] = 0.0026m,
                ["other:model"] = 0.0004m,
            },
            TotalCost = 0.0030m,
        });

        var text = panel.RenderedText;

        Assert.Contains("$0.0026", text, StringComparison.Ordinal);
        Assert.Contains("$0.0004", text, StringComparison.Ordinal);
        Assert.Contains("session $0.0030", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// NOTHING REPORTED, NOTHING SHOWN. A local-only session must not read "$0.00" — that claims a
    /// measurement never made, the same way a 0% cache rate would for a provider that never
    /// reported one.
    /// </summary>
    [Fact]
    public void Refresh_WithNoCostReported_ShowsNoCostAtAll()
    {
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            Endpoint = "", Rules = 0,
            SpendByModel = new Dictionary<string, int> { ["local:qwen3"] = 120_400 },
        });

        var text = panel.RenderedText;

        Assert.DoesNotContain("$", text, StringComparison.Ordinal);
        Assert.DoesNotContain("session", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE TOTAL IS THE SUM OF THE ROWS. Saving the reader that addition is the reason the session
    /// line exists, so the arithmetic is the thing most worth pinning: two paid instances, and the
    /// unindented line must equal what they add to.
    /// </summary>
    [Fact]
    public void Refresh_TheSessionTotal_EqualsTheSumOfTheInstanceCosts()
    {
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            Endpoint = "", Rules = 0,
            SpendByModel = new Dictionary<string, int> { ["a:m"] = 10, ["b:m"] = 20 },
            CostByInstance = new Dictionary<string, decimal> { ["a:m"] = 0.0026m, ["b:m"] = 0.0011m },
            TotalCost = 0.0037m,
        });

        Assert.Contains("session $0.0037", panel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>A MIXED SESSION shows a cost against the paid instance and none against the local
    /// one — the honest split rather than an invented zero.</summary>
    [Fact]
    public void Refresh_WithAMixedSession_CostsOnlyTheInstanceThatReported()
    {
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            Endpoint = "", Rules = 0,
            SpendByModel = new Dictionary<string, int>
            {
                ["local:qwen3"] = 120_400,
                ["openrouter:gemini"] = 33_416,
            },
            CostByInstance = new Dictionary<string, decimal> { ["openrouter:gemini"] = 0.0026m },
            TotalCost = 0.0026m,
        });

        var lines = panel.RenderedText.Split('\n');
        var localIndex = Array.FindIndex(lines, l => l.Contains("local:qwen3", StringComparison.Ordinal));

        // The line after the local row is its ↑/↓ split or the next instance — never a cost.
        Assert.DoesNotContain("$", lines[localIndex + 1], StringComparison.Ordinal);
        Assert.Contains("$0.0026", panel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE THRESHOLD, PINNED AT THE FIGURE THAT MOTIVATED IT. Money's own doc comment cites $0.0147
    /// — a real drive's cost — as the case four decimals exist for, but the cutover was written at
    /// $0.01, which routes exactly that value to the two-decimal branch and renders "$0.01". The
    /// comment and the code disagreed for a whole task because every other test sat at $0.0026,
    /// below both thresholds, where the two readings are indistinguishable.
    ///
    /// <para>A test that cannot tell the two apart is not pinning the behaviour it names. This one
    /// sits ABOVE $0.01 and below $1, so it fails under either wrong threshold.</para>
    /// </summary>
    [Fact]
    public void Refresh_WithACostAboveACent_StillShowsFourDecimals()
    {
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            Endpoint = "", Rules = 0,
            SpendByModel = new Dictionary<string, int> { ["openrouter:gemini"] = 33_416 },
            CostByInstance = new Dictionary<string, decimal> { ["openrouter:gemini"] = 0.0147m },
            TotalCost = 0.0147m,
        });

        var text = panel.RenderedText;

        Assert.Contains("$0.0147", text, StringComparison.Ordinal);
        Assert.Contains("session $0.0147", text, StringComparison.Ordinal);
        Assert.DoesNotContain("$0.01 ", text, StringComparison.Ordinal);
    }

    /// <summary>ONCE IT IS REAL MONEY, two decimals — the four-decimal branch is for fractions of a
    /// cent, and "$1.4200" reads as false precision on a figure a user would say aloud as $1.42.
    /// </summary>
    [Fact]
    public void Refresh_WithACostOverADollar_DropsToTwoDecimals()
    {
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            Endpoint = "", Rules = 0,
            SpendByModel = new Dictionary<string, int> { ["openrouter:gemini"] = 33_416 },
            CostByInstance = new Dictionary<string, decimal> { ["openrouter:gemini"] = 1.42m },
            TotalCost = 1.42m,
        });

        Assert.Contains("session $1.42", panel.RenderedText, StringComparison.Ordinal);
    }
}

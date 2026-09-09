using CxAgent.Core.Agents;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What the model is actually told. These assert through the DEFINITIONS rather than the constants
/// behind them, because the definition is what reaches a request — a constant that stopped being
/// used would still pass a test that read it directly.
/// </summary>
public class AgentReachPromptTests
{
    [Fact]
    public void Agent_send_tells_the_model_the_agent_remembers_its_own_work()
    {
        var def = AgentReachTools.Definitions.Single(d => d.Name == "agent_send");

        Assert.Contains("remembers", def.Description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// THE EXPENSIVE MISTAKE IS RESPAWNING, NOT FAILING TO CALL THIS. A description that only states
    /// the capability prevents nothing; naming the wrong move is what earns the schema bytes.
    /// </summary>
    [Fact]
    public void Agent_send_steers_away_from_spawning_a_second_agent_over_the_same_ground()
    {
        var def = AgentReachTools.Definitions.Single(d => d.Name == "agent_send");

        Assert.Contains("same ground", def.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Agent_list_says_nothing_else_reports_what_was_spawned()
    {
        var def = AgentReachTools.Definitions.Single(d => d.Name == "agent_list");

        Assert.Contains("spawned", def.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Agent_send_requires_both_a_name_and_a_prompt()
    {
        var def = AgentReachTools.Definitions.Single(d => d.Name == "agent_send");
        var schema = def.InputSchema.GetRawText();

        Assert.Contains("\"name\"", schema);
        Assert.Contains("\"prompt\"", schema);
    }

    [Fact]
    public void Agent_list_takes_nothing()
    {
        var def = AgentReachTools.Definitions.Single(d => d.Name == "agent_list");

        Assert.DoesNotContain("required", def.InputSchema.GetRawText());
    }
}

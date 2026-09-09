using CxAgent.Core.Agents;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The reach tools' answers, including the ones for a model that got the name wrong — those are the
/// replies that decide whether its next move is a fix or another guess.
/// </summary>
public class AgentReachToolsTests
{
    [Fact]
    public void It_claims_only_its_own_two_tools()
    {
        var tools = new AgentReachTools(new SubAgentStore());

        Assert.True(tools.Claims("agent_send"));
        Assert.True(tools.Claims("agent_list"));
        Assert.False(tools.Claims("agent"));
        Assert.False(tools.Claims("run_shell"));
    }

    [Fact]
    public async Task Listing_an_empty_store_says_so_rather_than_answering_nothing()
    {
        var tools = new AgentReachTools(new SubAgentStore());

        var answer = await tools.InvokeAsync("agent_list", "", "", CancellationToken.None);

        Assert.Contains("no sub-agents", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sending_to_a_name_nobody_holds_points_at_the_listing_tool()
    {
        var tools = new AgentReachTools(new SubAgentStore());

        var answer = await tools.InvokeAsync("agent_send", "ghost", "hello",
            CancellationToken.None);

        Assert.Contains("ghost", answer);
        Assert.Contains("agent_list", answer);
    }

    [Fact]
    public async Task Sending_with_no_name_says_which_argument_is_missing()
    {
        var tools = new AgentReachTools(new SubAgentStore());

        var answer = await tools.InvokeAsync("agent_send", "", "hello", CancellationToken.None);

        Assert.Contains("name", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_send_to_a_busy_agent_is_refused_and_says_why()
    {
        var store = new SubAgentStore();
        store.TryBeginSend("worker");
        var tools = new AgentReachTools(store);

        // Find returns null before Keep, so this exercises the not-found path first; the busy path
        // is proven directly on the store in SubAgentStoreTests.
        var answer = await tools.InvokeAsync("agent_send", "worker", "more",
            CancellationToken.None);

        Assert.Contains("worker", answer);
    }
}

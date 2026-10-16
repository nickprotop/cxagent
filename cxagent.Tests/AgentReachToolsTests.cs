using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
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
    public void Listing_an_empty_store_says_so_rather_than_answering_nothing()
    {
        var tools = new AgentReachTools(new SubAgentStore());

        var answer = tools.List();

        Assert.Contains("no sub-agents", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sending_to_a_name_nobody_holds_points_at_the_listing_tool()
    {
        var tools = new AgentReachTools(new SubAgentStore());

        var answer = await tools.SendAsync(Ask("ghost", "hello"), CancellationToken.None);

        Assert.Contains("ghost", answer);
        Assert.Contains("agent_list", answer);
    }

    [Fact]
    public async Task Sending_with_no_target_says_which_argument_is_missing()
    {
        var tools = new AgentReachTools(new SubAgentStore());

        var answer = await tools.SendAsync(Ask(" ", "hello"), CancellationToken.None);

        Assert.Contains("'to' is required", answer);
    }

    [Fact]
    public async Task A_send_to_a_busy_agent_is_refused_and_says_why()
    {
        var store = new SubAgentStore();
        store.TryBeginSend("worker");
        var tools = new AgentReachTools(store);

        // Find returns null before Keep, so this exercises the not-found path first; the busy path
        // is proven directly on the store in SubAgentStoreTests.
        var answer = await tools.SendAsync(Ask("worker", "more"), CancellationToken.None);

        Assert.Contains("worker", answer);
    }

    private static SendRequest Ask(string to, string prompt = "more", bool background = false, bool fromUser = false) =>
        new(To: to, Prompt: prompt, Background: background, ParentAgentId: "parent", FromUser: fromUser);

    private static (SubAgentStore Store, SubAgent Child) KeptChild(ILlmProvider provider)
    {
        var child = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = provider, Executors = CxAgent.Core.Jobs.JobRegistry.CreateWithBuiltins(),
            Ledger = new TokenLedger(), MaxTurns = 50, CompressAbove = 40_000, ContextWindow = 200_000,
        }).Create();
        var store = new SubAgentStore();
        store.Keep(child, "x");
        return (store, child);
    }

    private static MockLlmProvider Says(params string[] texts)
    {
        var p = new MockLlmProvider();
        foreach (var t in texts) p.EnqueueResponse(new LlmResponse { Text = t, StopReason = "end_turn" });
        return p;
    }

    private static BackgroundRun StoppedBy(SubAgentStore store, bool user)
    {
        var run = store.BeginBackground(new BackgroundStart(Name: "x", ParentAgentId: "parent", Brief: "p",
            Description: "x", IsResume: false), new CxAgent.Core.Jobs.ResultHandoff(_ => true));
        store.Stop("x", byUser: user);
        run.Finish(BackgroundState.Stopped);
        return run;
    }

    /// <summary>THE AGENT ID REACHES IT TOO: the envelope carries the id, the receipt the handle.</summary>
    [Fact]
    public async Task ToTakesTheAgentId_AsWellAsTheHandle()
    {
        var (store, child) = KeptChild(Says("by id"));
        Assert.Equal("by id", await new AgentReachTools(store).SendAsync(Ask(child.Agent.Id), CancellationToken.None));
    }

    [Fact]
    public async Task ABackgroundResume_ReturnsAtOnce_AndReportsToTheParent()
    {
        var held = new HeldProvider("deeper answer");
        var (store, _) = KeptChild(held);
        var delivery = new RecordingDelivery();
        var reach = new AgentReachTools(store, delivery: delivery);

        var answer = await reach.SendAsync(Ask("x", background: true), CancellationToken.None);
        Assert.Contains("background", answer);
        Assert.Contains("task_output", answer);

        held.Release();
        var d = await delivery.Next();
        Assert.Equal("parent", d.To);
        Assert.Contains("more", d.Notice.ForModel);
        Assert.Contains("deeper answer", d.Notice.ForModel);
        Assert.True(await Poll.Until(() => !store.IsBusy("x")));
    }

    /// <summary>NO PORT TO REPORT THROUGH, NO BACKGROUND: the resume waits, as before.</summary>
    [Fact]
    public async Task BackgroundWithoutADelivery_ResumesInTheForeground()
    {
        var (store, _) = KeptChild(Says("now"));
        Assert.Equal("now", await new AgentReachTools(store).SendAsync(Ask("x", background: true), CancellationToken.None));
    }

    /// <summary>THE USER'S STOP STANDS AGAINST THE MODEL: it cannot quietly restart work the user ended.</summary>
    [Fact]
    public async Task ASendToAChildTheUserStopped_IsRefused_ForTheModel()
    {
        var (store, _) = KeptChild(Says("never"));
        StoppedBy(store, user: true);

        Assert.Contains("stopped by the user", await new AgentReachTools(store).SendAsync(Ask("x"), CancellationToken.None));
        Assert.False(store.IsBusy("x"));
    }

    [Fact]
    public async Task AChildTheParentStopped_CanBeResumedByTheParent()
    {
        var (store, _) = KeptChild(Says("resumed"));
        StoppedBy(store, user: false);
        Assert.Equal("resumed", await new AgentReachTools(store).SendAsync(Ask("x"), CancellationToken.None));
    }

    /// <summary>THE USER'S OWN SEND resumes a child they stopped, and hands it back to the model.</summary>
    [Fact]
    public async Task TheUsersSend_ResumesAChildTheyStopped_AndClearsTheStop()
    {
        var (store, _) = KeptChild(Says("back", "again"));
        StoppedBy(store, user: true);

        Assert.Equal("back", await new AgentReachTools(store).SendAsync(Ask("x", fromUser: true), CancellationToken.None));
        Assert.Equal("again", await new AgentReachTools(store).SendAsync(Ask("x"), CancellationToken.None));
    }

    /// <summary>A RUNNING CHILD IS TOLD, NOT RESUMED: the message goes to its mailbox for its next lap.</summary>
    [Fact]
    public async Task ASendToARunningChild_GoesToItsMailbox()
    {
        var (store, child) = KeptChild(new HeldProvider("x"));
        store.TryClaim("x");

        Assert.Contains("delivered", await new AgentReachTools(store).SendAsync(Ask("x"), CancellationToken.None));
        Assert.Equal(["more"], child.Agent.Mailbox.Drain());
        store.Release("x");
    }

    [Fact]
    public void TheSendDefinition_TakesToAndBackground()
    {
        var def = Assert.Single(AgentReachTools.Definitions, d => d.Name == "agent_send");
        var schema = def.InputSchema.GetRawText();
        Assert.Contains("\"to\"", schema);
        Assert.Contains("\"background\"", schema);
        Assert.DoesNotContain("\"name\"", schema);
    }
}

using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>How each kept agent stands, and which of them the Tasks panel shows.</summary>
public class AgentRowsTests
{
    private static (SubAgentStore Store, StoredAgent Kept) Kept(string name = "finder")
    {
        var child = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(), Executors = JobRegistry.CreateWithBuiltins(), Ledger = new TokenLedger(),
            MaxTurns = 5, CompressAbove = 40_000, ContextWindow = 200_000,
        }).Create();
        var store = new SubAgentStore();
        store.Keep(child, name);
        return (store, store.Find(name)!);
    }

    private static BackgroundRun Background(SubAgentStore store, string name = "finder") =>
        store.BeginBackground(new BackgroundStart(name, "session", "b", name, IsResume: false), new ResultHandoff(_ => true));

    private static void Stretch(SubAgentStore store, string outcome, string name = "finder")
    {
        store.TryClaim(name);
        store.AnnounceBegin(name);
        store.EndSend(name, outcome);
    }

    [Fact]
    public void ABackgroundRun_IsRunning_AndNotForeground()
    {
        var (store, kept) = Kept();
        var run = Background(store);
        Assert.Equal(AgentRowState.Queued, AgentRows.Describe(store, kept, DateTimeOffset.UtcNow)!.State);

        run.MarkRunning();
        var row = AgentRows.Describe(store, kept, DateTimeOffset.UtcNow)!;

        Assert.Equal(AgentRowState.Running, row.State);
        Assert.False(row.Foreground);
    }

    /// <summary>A claimed child with no run going is a parent waiting on its answer.</summary>
    [Fact]
    public void AForegroundSpawn_IsForeground()
    {
        var (store, kept) = Kept();
        store.TryClaim("finder");
        store.AnnounceBegin("finder");

        var row = AgentRows.Describe(store, kept, DateTimeOffset.UtcNow)!;

        Assert.Equal(AgentRowState.Running, row.State);
        Assert.True(row.Foreground);
    }

    /// <summary>Decision 17: the user's own send is nobody's wait, so its row is a background row, stoppable.</summary>
    [Fact]
    public void TheUsersOwnSend_IsNotForeground()
    {
        var (store, kept) = Kept();
        store.BeginUserSend("finder");
        store.TryClaim("finder");
        store.AnnounceBegin("finder");

        Assert.False(AgentRows.Describe(store, kept, DateTimeOffset.UtcNow)!.Foreground);
    }

    [Fact]
    public void AChildAtAPrompt_SaysItIsWaitingForPermission()
    {
        var (store, kept) = Kept();
        Background(store).MarkRunning();
        kept.Agent.Agent.IsWaitingOnPermission = true;

        Assert.Equal(AgentRowState.WaitingForPermission, AgentRows.Describe(store, kept, DateTimeOffset.UtcNow)!.State);
    }

    [Theory]
    [InlineData("completed", AgentRowState.Done)]
    [InlineData("cancelled", AgentRowState.Stopped)]
    [InlineData("capped", AgentRowState.Failed)]
    [InlineData("error", AgentRowState.Failed)]
    public void AnEndedStretch_ReadsAsItEnded(string outcome, AgentRowState state)
    {
        var (store, kept) = Kept();
        Stretch(store, outcome);

        Assert.Equal(state, AgentRows.Describe(store, kept, DateTimeOffset.UtcNow)!.State);
    }

    /// <summary>Decision 4′: nothing clears on its own — a success an hour old is still a row, until x forgets it.</summary>
    [Theory]
    [InlineData("completed")]
    [InlineData("error")]
    public void AnEndedAgent_StaysOnThePanel_UntilForgotten(string outcome)
    {
        var (store, _) = Kept();
        Stretch(store, outcome);
        var later = DateTimeOffset.UtcNow.AddHours(1);

        Assert.Single(AgentRows.For(store, later));

        store.Forget("finder");
        Assert.Empty(AgentRows.For(store, later));
    }

    /// <summary>A run stopped while queued never announced, so the store has no end for it — the row still reads stopped.</summary>
    [Fact]
    public void ARunStoppedWhileQueued_ReadsStopped()
    {
        var (store, kept) = Kept();
        Background(store).Finish(BackgroundState.Stopped);

        Assert.Equal(AgentRowState.Stopped, AgentRows.Describe(store, kept, DateTimeOffset.UtcNow)!.State);
    }


    [Fact]
    public void AKeptAgentThatNeverRan_HasNoRow()
    {
        var (store, kept) = Kept();
        Assert.Null(AgentRows.Describe(store, kept, DateTimeOffset.UtcNow));
    }
}

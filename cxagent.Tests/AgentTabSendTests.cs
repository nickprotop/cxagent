using CxAgent.Core.Agents;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A follow-up typed in an agent's tab: the mailbox while it works, a resume when it is idle or stopped; one System
/// row in the parent as a trail, and nothing for the parent model.
/// </summary>
public sealed class AgentTabSendTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tab-send-" + Guid.NewGuid().ToString("N"));
    private SessionManager? _manager;
    private BufferedChatSink? _parent;

    public AgentTabSendTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { _manager?.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private Session Open(params string[] childAnswers)
    {
        _manager = SessionManager.Create(new AppPaths(_dir));
        var s = _manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = _parent = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.FanOut);
        s.SubAgents.Keep(AgentTabTests.Child(AgentTabTests.Says(childAnswers)), "finder");
        return s;
    }

    [Fact]
    public async Task AnIdleChild_IsResumed_AndTheParentGetsOnlyATrail()
    {
        var s = Open("looked again");
        var before = s.Host!.Context.Messages.Count;

        var line = await s.SendFromAgentTab("finder", "look again");

        Assert.Null(line);
        Assert.Contains(s.SubAgents.Find("finder")!.Agent.Agent.Context.Messages, m => m.Content == "look again");
        Assert.Single(_parent!.Notices, n => n.Contains("you → 'finder' (from its tab): look again", StringComparison.Ordinal));
        Assert.DoesNotContain(_parent.Notices, n => n.Contains("looked again", StringComparison.Ordinal));
        Assert.Equal(before, s.Host.Context.Messages.Count);
    }

    [Fact]
    public async Task ABusyChild_IsToldThroughItsMailbox()
    {
        var s = Open();
        Assert.True(s.SubAgents.TryClaim("finder"));

        var line = await s.SendFromAgentTab("finder", "also check y");

        Assert.StartsWith("delivered", line, StringComparison.Ordinal);
        Assert.Contains("also check y", s.SubAgents.Find("finder")!.Agent.Agent.Mailbox.Drain());
        s.SubAgents.Release("finder");
    }

    /// <summary>Core decision 17: the user's own send resumes an agent the user stopped, and hands it back to the model.</summary>
    [Fact]
    public async Task AUserStoppedChild_IsResumed()
    {
        var s = Open("ok");
        var run = s.SubAgents.BeginBackground(new BackgroundStart("finder", "session", "b", "finder", IsResume: false),
            new ResultHandoff(_ => true));
        s.SubAgents.Stop("finder", byUser: true);
        run.Finish(BackgroundState.Stopped);

        Assert.Null(await s.SendFromAgentTab("finder", "carry on"));
        Assert.False(run.StoppedByUser);
    }

    /// <summary>A deleted agent is answered as everywhere else: dismissed, not unknown.</summary>
    [Fact]
    public async Task ADeletedAgent_IsAnsweredDismissed()
    {
        var s = Open();
        s.SubAgents.Forget("finder");

        Assert.Equal(SubAgentStore.Dismissed("finder"), await s.SendFromAgentTab("finder", "look again"));
    }

    /// <summary>REVIEW FOCUS 5: an empty line sends nothing and leaves no trail.</summary>
    [Fact]
    public async Task AnEmptyLine_SendsNothing_AndLeavesNoTrail()
    {
        var s = Open();
        var notices = _parent!.Notices.Count;

        Assert.Null(await s.SendFromAgentTab("finder", "   "));

        Assert.Equal(notices, _parent.Notices.Count);
        Assert.Null(s.SubAgents.LastEnd("finder"));
    }
}

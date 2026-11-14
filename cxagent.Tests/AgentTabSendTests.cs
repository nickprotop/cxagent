using CxAgent.Core.Models;
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

    /// <summary>
    /// A follow-up with a pasted block: the child is sent the text, and its message keeps the line as typed
    /// and the paste, as the main conversation's does. The parent's trail shows the label.
    /// </summary>
    [Fact]
    public async Task APastedFollowUp_SendsTheText_AndKeepsTheLineAsTyped()
    {
        var s = Open("counted");
        var paste = new PastedText("[Pasted text #1 +3 lines]", "a\nb\nc");

        await s.SendFromAgentTab("finder", "count a\nb\nc", "count [Pasted text #1 +3 lines]", Attachments.Of([paste]));

        var sent = Assert.Single(s.SubAgents.Find("finder")!.Agent.Agent.Context.Messages, m => m.Content == "count a\nb\nc");
        Assert.Equal("count [Pasted text #1 +3 lines]", sent.Shown);
        Assert.Equal([paste], sent.Pastes!);
        Assert.Single(_parent!.Notices, n => n.Contains("(from its tab): count [Pasted text #1 +3 lines]", StringComparison.Ordinal));
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

    /// <summary>
    /// A line starting with a slash is a command attempt, never words for the worker (user, 2026-11-11): a known
    /// command is refused as not running in a worker's tab, an unknown one as unknown, and neither reaches the
    /// worker or leaves the parent a trail.
    /// </summary>
    [Theory]
    [InlineData("/clear", "'/clear' does not run in a worker's tab.")]
    [InlineData("  /celar now", "Unknown command '/celar'.")]
    [InlineData("/freeze", "'/freeze' runs only in a colleague's tab.")]
    [InlineData("/unfreeze", "'/unfreeze' runs only in a colleague's tab.")]
    public async Task ASlashLine_IsRefused_AndNeverReachesTheWorker(string typed, string refusal)
    {
        var s = Open("should not answer");
        var notices = _parent!.Notices.Count;

        Assert.Equal(refusal, await s.SendFromAgentTab("finder", typed));

        Assert.DoesNotContain(s.SubAgents.Find("finder")!.Agent.Agent.Context.Messages, m => m.Role == "user");
        Assert.Equal(notices, _parent.Notices.Count);
        Assert.Null(s.SubAgents.LastEnd("finder"));
    }

    /// <summary>A skill or command file the session would run is a known command too.</summary>
    [Fact]
    public async Task ASkillName_IsRefusedAsNotRunningHere()
    {
        var s = Open("should not answer");
        var commands = Path.Combine(_dir, ".cxagent", "commands");
        Directory.CreateDirectory(commands);
        File.WriteAllText(Path.Combine(commands, "review.md"), "Review $ARGUMENTS");
        Assert.NotNull(s.FindTyped("/review x"));

        Assert.Equal("'/review' does not run in a worker's tab.", await s.SendFromAgentTab("finder", "/review x"));
    }
}

using System.Reflection;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// That both paths opening a session go through the same wiring.
///
/// <para>THE DEFECT THIS GUARDS IS SILENT AND WAS FOUND FIVE TIMES. `/sessions new` was written
/// separately from the startup path and subscribed two of the eleven events it needed: no tool rows,
/// no turn recording, no compression notice, no child-worker tracking, no skills. Each surfaced
/// later as its own bug report, because a missing subscription looks exactly like a session that
/// simply has nothing to say.</para>
///
/// <para>READING THE SOURCE, NOT THE BEHAVIOUR, and that is deliberate. Driving eleven events
/// through two composed windows needs a terminal; what actually went wrong was a second call site
/// not saying what the first one said. This asserts the property that was violated — one routine,
/// two callers — which is the thing a future third caller will break.</para>
/// </summary>
public class SessionWiringParityTests
{
    /// <summary>The app's source tree, found from the test assembly rather than the process CWD.</summary>
    private static string Repo()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        while (!Directory.Exists(Path.Combine(dir, "cxagent", "UI")))
        {
            var up = Directory.GetParent(dir)?.FullName;
            Assert.NotNull(up);   // the tests must run from inside the repo
            dir = up!;
        }
        return dir;
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Repo(), relative));

    /// <summary>
    /// NEITHER CALLER SUBSCRIBES SESSION EVENTS ITSELF.
    ///
    /// <para>A `session.X +=` in either file is a subscription that the OTHER path does not have —
    /// which is precisely how the two drifted apart. Both hand the work to SessionWiring, so the
    /// list cannot be nine short on one side without being nine short on both.</para>
    /// </summary>
    [Theory]
    [InlineData("cxagent/UI/NewSessionCommand.cs")]
    public void NeitherPathSubscribesSessionEventsOfItsOwn(string file)
    {
        var source = Read(file);

        Assert.DoesNotContain("session.TokensUpdated +=", source);
        Assert.DoesNotContain("session.ContextUsedUpdated +=", source);
        Assert.DoesNotContain("session.TurnCompleted +=", source);
        Assert.DoesNotContain("session.ToolCallFinished +=", source);
    }

    /// <summary>
    /// AND NEITHER DOES THE COMPOSITION ROOT, for the events that belong to a conversation.
    ///
    /// <para>`session.Changed` was subscribed once at startup, over the startup session — so a
    /// second session's /mode, /model and /clear reached no front end: its mode line kept naming the
    /// provider it was opened with while its gate ran on another, and /clear wiped whichever
    /// transcript was in front rather than its own.</para>
    ///
    /// <para>SCOPED TO WHAT IS PER-CONVERSATION. AppBootstrap still subscribes the queue events
    /// (Cancelled, Drained, Pending), which resolve through the session's own tab and are checked by
    /// their own behaviour rather than by this shape.</para>
    /// </summary>
    [Fact]
    public void TheCompositionRootDoesNotSubscribeChangedItself()
    {
        var source = Read("cxagent/UI/AppBootstrap.cs");

        Assert.DoesNotContain("session.Changed +=", source);
    }

    /// <summary>AND BOTH CALL THE SHARED ROUTINE, so the check above cannot be satisfied by a path
    /// that simply wires nothing at all.</summary>
    [Theory]
    [InlineData("cxagent/UI/NewSessionCommand.cs")]
    [InlineData("cxagent/UI/AppBootstrap.cs")]
    public void BothPathsUseTheSharedWiring(string file)
    {
        var source = Read(file);

        Assert.Contains("SessionWiring.Subscribe(", source);
        Assert.Contains("SessionWiring.Ports(", source);
        Assert.Contains("SessionWiring.Sinks(", source);
    }
}

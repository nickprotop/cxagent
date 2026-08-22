using CxAgent.Core.Llm;
using CxAgent.Core.Jobs;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The one contradiction the system resolves rather than reports: fan-out asked for, spawn tool
/// withheld.
///
/// <para>There is no planner guard. A `WritesAPlanFile` type gets `write_file` by default like every
/// other agent — no built-in ships a `tools` field — so the contradiction only exists if a user
/// writes `-write_file` on it themselves, which this design treats as their call everywhere else.</para>
/// </summary>
public class ToolSelectionGuardTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "toolguard-" + Guid.NewGuid().ToString("N")[..8]);

    public ToolSelectionGuardTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private (Session Session, BufferedChatSink Said) Wire(ToolSelection? selection, AgentMode mode)
    {
        var session = new Session(_dir);
        var paths = new AppPaths(Path.Combine(_dir, "config"));
        var said = new BufferedChatSink();

        SessionFactory.Wire(session,
            ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SharedServices
            {
                Resume = new SqliteSessionStore(paths),
                History = new UsageHistoryStore(paths),
                Logs = new LogFileManager(paths),
            },
            new SessionPorts
            {
                Observer = said,
                ToolObserver = new BufferedJobPanel(),
                ToolSelection = selection,
            },
            mode);

        return (session, said);
    }

    [Fact]
    public void FanOutWithoutTheSpawnToolFallsBackToSingle()
    {
        // RESOLVED, NOT REPORTED. Leaving the mode at fan-out while nothing can delegate keeps the
        // divergence alive for the session's whole life — /mode saying one thing, the tool list
        // another.
        var (session, _) = Wire(new ToolSelection([Tool.Inherited, Tool.Not.Agent]), AgentMode.FanOut);

        Assert.False(session.Mode.CanDelegate);
    }

    [Fact]
    public void AndItSaysSo()
    {
        var (_, said) = Wire(new ToolSelection([Tool.Inherited, Tool.Not.Agent]), AgentMode.FanOut);

        Assert.Contains(said.Notices, m => m.Contains("single mode"));
    }

    [Fact]
    public void FanOutWithTheSpawnToolIsUntouched()
    {
        var (session, said) = Wire(new ToolSelection([Tool.Inherited, Tool.Not.RunShell]), AgentMode.FanOut);

        Assert.True(session.Mode.CanDelegate);
        Assert.DoesNotContain(said.Notices, m => m.Contains("single mode"));
    }

    [Fact]
    public void NoSelectionLeavesFanOutAlone()
    {
        var (session, said) = Wire(null, AgentMode.FanOut);

        Assert.True(session.Mode.CanDelegate);
        Assert.DoesNotContain(said.Notices, m => m.Contains("single mode"));
    }

    // --- The commands that REPORT availability ------------------------------------------
    //
    // Testing the commands alone proved nothing about the session: hardcoding `true` at both call
    // sites in Session.Commands left the whole suite green, which is why these exist. The command
    // tests own the rendering; these own the WIRING.

    [Fact]
    public void SkillsSaysTheToolIsWithheldThroughTheSession()
    {
        var (session, said) = Wire(new ToolSelection([Tool.Inherited, Tool.Not.Skill]), AgentMode.Single);

        session.ListSkills();

        Assert.Contains("not offered", said.Transcript);
    }

    [Fact]
    public void SkillsSaysNothingWhenTheToolIsOffered()
    {
        var (session, said) = Wire(new ToolSelection([Tool.Inherited, Tool.Not.RunShell]), AgentMode.Single);

        session.ListSkills();

        Assert.DoesNotContain("not offered", said.Transcript);
    }

    [Fact]
    public void AgentTypesSayTheAgentCannotSpawnThroughTheSession()
    {
        // SINGLE MODE ON PURPOSE. Fan-out plus a withheld agent tool is the contradiction the guard
        // above resolves, so the mode would be flipped before this ran and the selection would not
        // be the thing under test.
        var (session, said) = Wire(new ToolSelection([Tool.Inherited, Tool.Not.Agent]), AgentMode.Single);

        if (session.ListAgentTypes("") == CxAgent.Core.Commands.CommandStatus.Unknown) return;

        Assert.Contains("cannot spawn", said.Transcript);
    }

    [Fact]
    public void TheStartupBannerAgreesWithTheFallback()
    {
        // FOUND ON A LIVE DRIVE, not by a test. SessionFactory flips the mode and corrects its OWN
        // copy; AppBootstrap had already built the window from the CLI default, and the banner
        // cannot be revised once written. So the notice said "working in single mode" and the status
        // line three rows below read "fan-out" for the session's whole life.
        //
        // This pins the SHARED PREDICATE both call rather than AppBootstrap's ordering, which is UI
        // wiring a Core test cannot reach: if Offers stops answering for `agent`, both the guard and
        // the banner break together and this fails.
        var withheld = new ToolSelection([Tool.Inherited, Tool.Not.Agent]);

        Assert.False(ToolSelection.Offers(withheld, Tool.Agent));
        Assert.True(ToolSelection.Offers(new ToolSelection([Tool.Inherited, Tool.Not.RunShell]), Tool.Agent));
    }

    [Fact]
    public void SingleModeSaysNothingEitherWay()
    {
        // The fallback answers a contradiction. Single mode without the spawn tool is agreement,
        // not contradiction, and a line about it would be noise.
        var (session, said) = Wire(new ToolSelection([Tool.Inherited, Tool.Not.Agent]), AgentMode.Single);

        Assert.False(session.Mode.CanDelegate);
        Assert.DoesNotContain(said.Notices, m => m.Contains("single mode"));
    }
}

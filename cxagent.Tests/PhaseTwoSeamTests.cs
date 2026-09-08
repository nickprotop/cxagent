using System.Text.Json;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Three small seams phase two needs, closed while they were still cheap.
///
/// <para>EACH IS A THING THAT IS FREE NOW AND EXPENSIVE LATER. A DTO field is one line until a client
/// renders without it; an event parameter is one optional argument until every subscriber implements
/// the signature; a log line is one call until somebody asks why a session's tools changed and there
/// is no record at all.</para>
/// </summary>
public class PhaseTwoSeamTests
{
    private static Job Sample() => new()
    {
        Id = "job-1",
        AgentId = "agent-1",
        JobType = "shell",
        DisplayName = "ls",
        Reviewing = true,
        RetryCount = 2,
        ProgressBody = "half way",
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
    };

    /// <summary>
    /// THE SNAPSHOT CARRIES WHAT A ROW RENDERS.
    ///
    /// <para>`Reviewing` is the state a client shows while the gate consults a classifier — exactly
    /// the moment a user wonders why nothing is happening. Dropped from the DTO, a remote client
    /// would lose it silently, along with the retry count.</para>
    /// </summary>
    [Fact]
    public void AJobSnapshotKeepsTheFieldsAClientShows()
    {
        var snapshot = JobSnapshot.Of(Sample());

        Assert.True(snapshot.Outcome.Reviewing);
        Assert.Equal(2, snapshot.Outcome.RetryCount);
        Assert.Equal("half way", snapshot.Progress.Body);
        Assert.NotNull(snapshot.Timing.CreatedAt);
    }

    /// <summary>AND IT STILL SHARES NOTHING WITH THE LIVE JOB — the reason the DTO exists. Mutating
    /// the job after the capture must not rewrite what was captured.</summary>
    [Fact]
    public void ASnapshotDoesNotFollowTheJobItWasTakenFrom()
    {
        var job = Sample();
        var snapshot = JobSnapshot.Of(job);

        job.Reviewing = false;
        job.RetryCount = 99;

        Assert.True(snapshot.Outcome.Reviewing);
        Assert.Equal(2, snapshot.Outcome.RetryCount);
    }

    /// <summary>
    /// CANCELLING SAYS WHO ASKED.
    ///
    /// <para>Cancel returns the queued text to a COMPOSER, and with several front ends attached
    /// there are several. Null is the honest answer for a caller that cannot say — which is every
    /// caller today — but the event has to be ABLE to say, or adding it later changes a signature
    /// every subscriber implements.</para>
    /// </summary>
    [Fact]
    public void CancellingCarriesTheAskerWhenOneIsNamed()
    {
        var session = new Session(Path.GetTempPath());
        (string Text, string? By)? seen = null;
        session.Cancelled += (text, by) => seen = (text, by);

        session.Steer("something queued");
        session.CancelPending(by: "client-7");

        Assert.Equal(("something queued", "client-7"), seen);
    }

    /// <summary>AND NULL WHEN NOBODY SAID, which is the single-front-end case and must stay
    /// unchanged.</summary>
    [Fact]
    public void CancellingWithNoAskerCarriesNull()
    {
        var session = new Session(Path.GetTempPath());
        (string Text, string? By)? seen = null;
        session.Cancelled += (text, by) => seen = (text, by);

        session.Steer("something queued");
        session.CancelPending();

        Assert.Equal(("something queued", (string?)null), seen);
    }
}

/// <summary>
/// That a plugin change is recorded like every other lifecycle event.
///
/// <para>OPEN, RESUME AND CLOSE EACH WROTE A LINE AND THIS DID NOT — yet it is the widest-reaching
/// change the manager makes: one caller's edit rebinds the plugin set of every session in the
/// process and defers it into the busy ones. Without a line, "why did that session's tools change"
/// has no answer anywhere.</para>
/// </summary>
public class PluginChangeLogTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-pluglog-" + Guid.NewGuid().ToString("N"));

    public PluginChangeLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A CONFIG CHANGE LEAVES A LINE, saying how far it reached.</summary>
    [Fact]
    public void ChangingAPluginEntryIsLogged()
    {
        using var manager = SessionManager.Create(new CxAgent.Core.Storage.AppPaths(_dir));
        var session = manager.Open(_dir,
            CxAgent.Core.Llm.ResolvedConfig.ForTesting(new CxAgent.Core.Llm.MockLlmProvider("m")),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);

        // ADD RATHER THAN TOGGLE: SetPluginEnabled refuses a name that is not configured, and
        // returns before it reaches the change path this is about.
        var result = manager.AddPlugin(session, "some-plugin",
            new CxAgent.Core.Llm.PluginConfig(File: "some-plugin.dll"));

        Assert.IsType<PluginChangeResult.Applied>(result);

        var log = Path.Combine(_dir, "logs", "app", "sessions.log");
        Assert.True(File.Exists(log), $"no lifecycle log at {log}");

        var lines = File.ReadAllLines(log);
        Assert.Contains(lines, l => l.Contains("\"what\":\"plugins\"", StringComparison.Ordinal));
    }
}

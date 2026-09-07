using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Which id each store keys on, and why they are not all the same.
///
/// <para>A SESSION WRITES INTO THREE DATABASES, and two id spaces run through them. Nothing in the
/// schemas says which is which — <c>permissions.agent_id</c> holds a SESSION id despite its name —
/// so the division is recorded here, where a change to it fails visibly.</para>
///
/// <para><b>THE DIVISION IS DELIBERATE.</b> A re-wire — `/model`, a resume, a setup flow — replaces
/// the agent and mints a new id, and starts a NEW ledger. So anything MEASURING what was spent has
/// to key on the agent, or two providers' costs sum into one number nobody can split. Anything
/// FOLLOWING a conversation has to key on the session, or it breaks at exactly the moment worth
/// following.</para>
/// </summary>
public class StoreIdentityTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-ids-" + Guid.NewGuid().ToString("N"));

    public StoreIdentityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// A PERMISSION ROW IS FILED UNDER THE SESSION, so a grant can be followed across a `/model`
    /// switch or a resume — which replace the agent and its id.
    /// </summary>
    [Fact]
    public void PermissionRowsAreKeyedByTheSession()
    {
        var store = new UsageHistoryStore(new AppPaths(_dir));

        store.SavePermission(new PermissionRecord(
            SessionId: "session-1", DateTimeOffset.UtcNow, "Shell", "denied", "agent-1",
            WorkingDir: _dir, Subject: "rm -rf /"));

        var row = Assert.Single(store.PermissionsSince(DateTimeOffset.UtcNow.AddHours(-1)));

        Assert.Equal("session-1", row.SessionId);

        // AND THE REQUESTER IS STILL THE AGENT — which is the point of keeping both: the row says
        // WHICH conversation was asked and WHICH agent inside it did the asking.
        Assert.Equal("agent-1", row.Requester);
    }

    /// <summary>
    /// AND A SPEND ROW IS FILED UNDER THE AGENT, because it measures one agent instance. A re-wire
    /// starts a fresh ledger, so a session-keyed row would sum two providers into one figure.
    /// </summary>
    [Fact]
    public void SpendRowsAreKeyedByTheAgent()
    {
        var store = new UsageHistoryStore(new AppPaths(_dir));

        store.SaveSession(new SessionRecord(
            "agent-1", _dir, "some-model", "fan-out", 10, 20, 0, 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var row = Assert.Single(store.SessionsSince(DateTimeOffset.UtcNow.AddHours(-1)));

        Assert.Equal("agent-1", row.AgentId);
    }

    /// <summary>
    /// AND A TRANSCRIPT IS FILED UNDER THE SESSION, for the same reason permissions are: it is the
    /// conversation, and a conversation outlives the agents that ran it.
    /// </summary>
    [Fact]
    public void TranscriptRowsAreKeyedByTheSession()
    {
        var store = new TranscriptStore(new AppPaths(_dir));

        store.Append("session-1", 1, "user", "User", "hello");

        Assert.Single(store.Window("session-1"));
        Assert.Empty(store.Window("agent-1"));
    }
}

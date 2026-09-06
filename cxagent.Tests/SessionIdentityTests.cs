using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A session's own identity, as distinct from its agent's.
///
/// <para>WHY TWO IDS. <see cref="Session.SessionId"/> is the agent's, minted fresh per <c>Agent</c>,
/// so it is replaced by every re-wire and again by a resume — the resume store and the history
/// archive key on it correctly, because a resumed session IS a new agent writing its own rows.
/// Anything naming the SESSION needs one that survives those, and until now there was none.</para>
/// </summary>
public class SessionIdentityTests
{
    [Fact]
    public void ASessionHasAnIdBeforeItHasAnAgent()
    {
        var session = new Session("/tmp/x");

        // The whole point: addressable from the moment it exists, with no host wired.
        Assert.NotEmpty(session.Id);
        Assert.Null(session.SessionId);
    }

    [Fact]
    public void TwoSessionsHaveDifferentIds()
    {
        Assert.NotEqual(new Session("/tmp/x").Id, new Session("/tmp/x").Id);
    }

    /// <summary>
    /// THE SAME FOLDER TWICE IS TWO SESSIONS, and they are told apart by id rather than by path.
    /// A folder resolves to a session by default; asking for a second one on the same path is
    /// deliberate, and then nothing about the path distinguishes them.
    /// </summary>
    [Fact]
    public void TheSameFolderDoesNotMeanTheSameId()
    {
        var a = new Session("/tmp/same");
        var b = new Session("/tmp/same");

        Assert.Equal(a.WorkingDirectory, b.WorkingDirectory);
        Assert.NotEqual(a.Id, b.Id);
    }

    /// <summary>IT IS READ-ONLY. An identity something else can address must not be reassignable by
    /// the session itself — a compile-time property here, pinned so a later refactor that adds a
    /// setter has to argue with this test.</summary>
    [Fact]
    public void TheIdIsStableForTheLifeOfTheSession()
    {
        var session = new Session("/tmp/x");

        var first = session.Id;
        var second = session.Id;

        Assert.Equal(first, second);
        Assert.Null(typeof(Session).GetProperty(nameof(Session.Id))!.SetMethod);
    }
}

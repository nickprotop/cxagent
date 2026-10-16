using CxAgent.Core.Sessions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The token that ends a task_output wait when the user types — every order a steer and a wait can come in.
///
/// <para>THE ORDERS ARE THE POINT: a wait begun after the user typed must end at once, one begun before must
/// end when they type, and one begun after their message was read must not end for it.</para>
/// </summary>
public class UserSpokeTokenTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spoke-" + Guid.NewGuid().ToString("N"));
    public UserSpokeTokenTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void AWaitBegunBeforeTheUserTypes_EndsWhenTheyDo()
    {
        var s = new Session(_dir);
        var token = s.UserSpokeToken();
        Assert.False(token.IsCancellationRequested);

        s.Steer("look here");
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void AWaitBegunWhileAMessageIsUnread_EndsAtOnce()
    {
        var s = new Session(_dir);
        s.Steer("look here");
        Assert.True(s.UserSpokeToken().IsCancellationRequested);
    }

    [Fact]
    public void AWaitBegunAfterTheMessageWasRead_IsNotEndedByIt()
    {
        var s = new Session(_dir);
        s.Steer("look here");
        Assert.NotNull(s.TakeSteer());
        Assert.False(s.UserSpokeToken().IsCancellationRequested);
    }

    [Fact]
    public void ASystemDelivery_EndsNoWait()
    {
        var s = new Session(_dir);
        var token = s.UserSpokeToken();
        s.Inject("a command finished");
        Assert.False(token.IsCancellationRequested);
    }
}

using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What the front end does with a turn's task once it ends: a fault is reported, never rethrown.
///
/// <para>THE TURN IS AWAITED IN AN ASYNC VOID, where an exception reaches no caller and can take the app
/// down. A turn faults on purpose when a subscriber fails after the turn has still read the user's steer,
/// so the fault has to land somewhere the user can read it.</para>
/// </summary>
public class TurnEndsTests
{
    [Fact]
    public async Task AFaultedTurn_IsReported_NotThrown()
    {
        Exception? reported = null;

        await TurnEnds.ObserveAsync(Task.FromException(new InvalidOperationException("subscriber broke")),
            ex => reported = ex);

        Assert.Equal("subscriber broke", reported?.Message);
    }

    /// <summary>ESCAPE IS NOT A FAILURE: a cancelled turn ended because the user asked it to.</summary>
    [Fact]
    public async Task ACancelledTurn_IsNotReported()
    {
        var reported = false;

        await TurnEnds.ObserveAsync(Task.FromCanceled(new CancellationToken(canceled: true)), _ => reported = true);

        Assert.False(reported);
    }

    [Fact]
    public async Task ACompletedTurn_IsNotReported()
    {
        var reported = false;

        await TurnEnds.ObserveAsync(Task.CompletedTask, _ => reported = true);

        Assert.False(reported);
    }

    /// <summary>A REPORTER THAT FAILS IN TURN IS SWALLOWED, for the same reason: nothing above it can catch.</summary>
    [Fact]
    public async Task AReporterThatThrows_DoesNotEscape() =>
        await TurnEnds.ObserveAsync(Task.FromException(new InvalidOperationException("x")),
            _ => throw new InvalidOperationException("reporter broke"));
}

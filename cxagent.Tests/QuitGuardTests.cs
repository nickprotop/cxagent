using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The question asked before quitting or closing a tab with background agents running.</summary>
public class QuitGuardTests
{
    [Fact]
    public void NothingRunning_AsksNothing() => Assert.Null(QuitGuard.Question(0));

    [Fact]
    public void OneRunning_AsksInTheSingular() =>
        Assert.Contains("1 background agent is still running", string.Join(" ", QuitGuard.Question(1)!));

    [Fact]
    public void SeveralRunning_AsksInThePlural() =>
        Assert.Contains("3 background agents are still running", string.Join(" ", QuitGuard.Question(3)!));

    /// <summary>THE COST IS NAMED: stopping loses work that has not reported, which is the reason to ask.</summary>
    [Fact]
    public void TheQuestion_SaysWhatStoppingLoses() =>
        Assert.Contains("not reported", string.Join(" ", QuitGuard.Question(2)!));
}

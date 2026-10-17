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

    /// <summary>A TAB WITH NOTHING RUNNING CLOSES WITHOUT A QUESTION.</summary>
    [Fact]
    public void ClosingATab_WithNothingRunning_AsksNothing() =>
        Assert.Null(QuitGuard.TabQuestion(new TabWork(Agents: 0, Commands: 0)));

    /// <summary>AGENTS ONLY: there is nothing to keep, so keeping is not offered.</summary>
    [Fact]
    public void ClosingATab_WithOnlyAgents_OffersNoKeep()
    {
        var question = QuitGuard.TabQuestion(new TabWork(Agents: 1, Commands: 0))!;

        Assert.Equal(["Cancel", "Close and stop everything"], question.Answers);
        Assert.Contains("1 background agent is still running", string.Join(" ", question.Lines));
        Assert.Contains("not reported", string.Join(" ", question.Lines));
    }

    /// <summary>
    /// COMMANDS ARE ASKED ABOUT TOO, and keeping them is offered by count: a dev server or a watcher is often
    /// meant to outlive the tab, and the user is the one who knows.
    /// </summary>
    [Fact]
    public void ClosingATab_WithCommands_OffersToKeepThem_AfterCancel()
    {
        var question = QuitGuard.TabQuestion(new TabWork(Agents: 0, Commands: 2))!;

        Assert.Equal(["Cancel", "Close and stop everything", "Close, keep 2 commands running"], question.Answers);
        Assert.Contains("2 background commands are still running", string.Join(" ", question.Lines));
        Assert.DoesNotContain("not reported", string.Join(" ", question.Lines));   // no agent's work to lose
    }

    [Fact]
    public void ClosingATab_WithBoth_NamesBoth_InTheSingular()
    {
        var question = QuitGuard.TabQuestion(new TabWork(Agents: 1, Commands: 1))!;

        Assert.Contains("1 agent and 1 command are still running", string.Join(" ", question.Lines));
        Assert.Equal("Close, keep 1 command running", question.Answers[2]);
    }

    /// <summary>
    /// DELETING AN AGENT WITH COMMANDS STILL RUNNING ASKS AS CLOSING A TAB DOES: the same Cancel-first order, and
    /// keeping them offered by count.
    /// </summary>
    [Fact]
    public void DeletingAnAgent_WithCommands_AsksLikeClosingATab()
    {
        var question = QuitGuard.DeleteQuestion("server", commands: 2);

        Assert.Equal(["Cancel", "Delete and stop them", "Delete, keep 2 commands running"], question.Answers);
        Assert.Contains("'server' started 2 background commands that are still running", string.Join(" ", question.Lines));
    }

    [Fact]
    public void DeletingAnAgent_WithOneCommand_SaysItInTheSingular()
    {
        var question = QuitGuard.DeleteQuestion("server", commands: 1);

        Assert.Contains("'server' started 1 background command that is still running", string.Join(" ", question.Lines));
        Assert.Equal("Delete, keep 1 command running", question.Answers[2]);
    }
}

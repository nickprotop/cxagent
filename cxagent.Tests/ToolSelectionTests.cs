using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Agents;
using CxAgent.Core.Jobs;
using CxAgent.Core.Skills;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The selection value type: what a level SAYS, never what it resolves to.
///
/// <para>Every rule here is testable without an agent, a session or a provider, which is the point
/// of the type existing at all — the composition rules are where this feature is subtle, and they
/// should not need a running session to pin.</para>
/// </summary>
public class ToolSelectionTests
{
    private static IReadOnlyList<ToolDefinition> Offered(params string[] names) =>
        [.. names.Select(n => new ToolDefinition(n, n, JsonSerializer.SerializeToElement(new { type = "object" })))];

    private static IReadOnlyList<string> Names(IReadOnlyList<ToolDefinition> tools) =>
        [.. tools.Select(t => t.Name)];

    [Fact]
    public void NullMeansNoOpinionSoTheOtherSideDecides()
    {
        Assert.Equal(["read_file"], ToolSelection.Then(null, new ToolSelection(["read_file"]))!.Terms);
        Assert.Equal(["read_file"], ToolSelection.Then(new ToolSelection(["read_file"]), null)!.Terms);
        Assert.Null(ToolSelection.Then(null, null));
    }

    [Fact]
    public void EmptyMeansNothingAndIsNotNull()
    {
        // The one explicit way to say "no tools". A design where empty meant "everything" would make
        // it unsayable.
        Assert.Empty(Names(new ToolSelection([]).Apply(Offered("read_file"))));
        Assert.Empty(Names(new ToolSelection([]).Apply(Offered("read_file", "grep"))));
    }

    [Fact]
    public void ALaterLevelMayAddBackWhatAnEarlierOneRemoved()
    {
        // COMPOSE ONCE, APPLY ONCE. Then() merges the TERMS; Apply resolves them against S0 exactly
        // one time. Chaining Apply instead — turn.Apply(session.Apply(offered)) — hands the turn a
        // set that already lost grep, so its +grep matches nothing and narrowing-only comes back
        // silently. That mistake is what this test exists to forbid.
        // THE TURN DOES NOT SAY `inherited`. That matters: an `inherited` in the later level re-adds
        // everything by itself, so a test written that way passes even if `+` is never implemented —
        // verified by injecting exactly that and watching the suite stay green. Here `+grep` is the
        // ONLY thing that can bring grep back.
        var session = new ToolSelection(["inherited", "-grep"]);
        var turn = new ToolSelection(["+grep"]);

        var composed = ToolSelection.Then(session, turn);

        Assert.Equal(["read_file", "grep"], Names(composed!.Apply(Offered("read_file", "grep"))));
    }

    [Fact]
    public void APlusIsNotJustABareName()
    {
        // A bare name in a later level would also add — but only because Then appends its terms.
        // This pins that the + form survives composition rather than being filtered on the way in,
        // which is the shape of the bug the injection above exposed.
        var composed = ToolSelection.Then(
            new ToolSelection(["inherited", "-grep"]),
            new ToolSelection(["+grep"]));

        Assert.Contains("+grep", composed!.Terms);
    }

    [Fact]
    public void ASecondInheritedDoesNotUndoTheLevelBeforeIt()
    {
        // THE MOST ORDINARY CONFIG ANYONE WILL WRITE: narrow globally, narrow further per session.
        // Composition puts both levels' terms in one list, so a second `inherited` that reset to the
        // whole set would silently re-add what the manager removed. Found by a levels test, pinned
        // here because it is a property of Apply.
        var composed = ToolSelection.Then(
            new ToolSelection(["inherited", "-run_shell"]),
            new ToolSelection(["inherited", "-write_file"]));

        var kept = Names(composed!.Apply(Offered("read_file", "run_shell", "write_file")));

        Assert.Equal(["read_file"], kept);
    }

    [Fact]
    public void AllResetsToEverythingAndDiscardsWhatCameBefore()
    {
        // THE ONE TERM THAT DELIBERATELY WIDENS. A session under a read-only manager can say `all`
        // and get the full set back — safe because a selection is only ever written in config or
        // code, never by a model.
        var composed = ToolSelection.Then(
            new ToolSelection(["read_file"]),
            new ToolSelection([Tool.All]));

        Assert.Equal(["read_file", "run_shell", "write_file"],
            Names(composed!.Apply(Offered("read_file", "run_shell", "write_file"))));
    }

    [Fact]
    public void AllDiffersFromInheritedOnlyBelowTheTopLevel()
    {
        // At the top they are the same set: nothing has narrowed yet. Below it they diverge, and
        // that divergence is the entire reason `all` exists.
        var offered = Offered("read_file", "run_shell");

        Assert.Equal(Names(new ToolSelection(["inherited"]).Apply(offered)),
                     Names(new ToolSelection([Tool.All]).Apply(offered)));

        var narrowed = new ToolSelection(["inherited", "-run_shell"]);

        Assert.DoesNotContain("run_shell",
            Names(ToolSelection.Then(narrowed, new ToolSelection(["inherited"]))!.Apply(offered)));
        Assert.Contains("run_shell",
            Names(ToolSelection.Then(narrowed, new ToolSelection([Tool.All]))!.Apply(offered)));
    }

    [Fact]
    public void AllIsStillBoundedByS0()
    {
        // It resets the SELECTION, never the structure. A child saying `all` does not acquire
        // ask_user, because ask_user was never in the set it was handed.
        Assert.DoesNotContain("ask_user",
            Names(new ToolSelection([Tool.All]).Apply(Offered("read_file"))));
    }

    [Fact]
    public void ATermAfterAllStillApplies()
    {
        // `all` is a starting point, not a full stop.
        Assert.Equal(["read_file"],
            Names(new ToolSelection([Tool.All, "-run_shell"]).Apply(Offered("read_file", "run_shell"))));
    }

    [Fact]
    public void ChainingApplyIsNotComposition()
    {
        // The same two levels, applied one after the other, CANNOT reopen. Pinned so the difference
        // is a fact the suite knows rather than a sentence in a comment.
        var session = new ToolSelection(["inherited", "-grep"]);
        var turn = new ToolSelection(["inherited", "+grep"]);

        Assert.DoesNotContain("grep", Names(turn.Apply(session.Apply(Offered("read_file", "grep")))));
    }

    [Fact]
    public void NoPlusReachesPastS0()
    {
        // THE REAL FLOOR, and the only one. A + term names a tool that is not offered at all — a
        // child asking for ask_user — and gets nothing. S0 is enforced at construction, not here:
        // Apply can only ever return elements of what it was handed.
        Assert.DoesNotContain("ask_user",
            Names(new ToolSelection(["inherited", "+ask_user"]).Apply(Offered("read_file"))));
    }

    [Fact]
    public void ABareListIsAnExactSet()
        => Assert.Equal(["read_file", "grep"],
            Names(new ToolSelection(["read_file", "grep"]).Apply(Offered("read_file", "grep", "run_shell"))));

    [Fact]
    public void InheritedMinusIsADelta()
        => Assert.Equal(["read_file", "grep"],
            Names(new ToolSelection(["inherited", "-run_shell"]).Apply(Offered("read_file", "grep", "run_shell"))));

    [Fact]
    public void AMinusThatMatchesNothingIsHarmless()
        => Assert.Equal(["read_file"],
            Names(new ToolSelection(["inherited", "-nonexistent"]).Apply(Offered("read_file"))));

    [Fact]
    public void NoToolIsExemptFromOmission()
    {
        // todowrite was exempted in an early draft and the carve-out was withdrawn: a rule that must
        // be remembered rather than derived, and every future tool would raise the question again.
        Assert.DoesNotContain("todowrite",
            Names(new ToolSelection(["read_file"]).Apply(Offered("read_file", "todowrite"))));
    }

    [Fact]
    public void InheritedFollowsTHISCALLS_OfferedSet_NotAnEarlierOne()
    {
        // THE BUG THIS TYPE EXISTS TO PREVENT. The offered set grows after config load — a skills
        // catalog appears, an embedder injects per session — so "inherited" must mean what the agent
        // has NOW. The same selection applied to a later, larger set must pick up the new tools.
        var selection = new ToolSelection(["inherited", "-run_shell"]);

        Assert.Equal(["read_file"], Names(selection.Apply(Offered("read_file", "run_shell"))));

        // A server connected, or a skill appeared. Nothing about the selection changed.
        Assert.Equal(["read_file", "ctx7_docs"],
            Names(selection.Apply(Offered("read_file", "run_shell", "ctx7_docs"))));
    }

    [Fact]
    public void AMalformedTermIsRefused()
    {
        // A + term is valid grammar; this pins that anything else is not. Config catches this at
        // load (see ProviderConfig) so it never reaches a request.
        Assert.Throws<FormatException>(
            () => new ToolSelection(["inherited", "*run_shell"]).Apply(Offered("run_shell")));
    }

    [Fact]
    public void TwoSelectionsWithTheSameTermsAreEqual()
    {
        // A positional record gives the LIST member reference equality, so this needs a hand-written
        // Equals. Without it Session.Submit's ToolsIgnored comparison fires on every mid-turn
        // correction from a caller that rebuilds its selection — the noise the flag must not make.
        Assert.Equal(new ToolSelection(["inherited", "-grep"]), new ToolSelection(["inherited", "-grep"]));
        Assert.NotEqual(new ToolSelection(["inherited", "-grep"]), new ToolSelection(["inherited", "-glob"]));
    }
}

/// <summary>
/// That <see cref="Tool"/>'s constants are the names actually offered.
///
/// <para>A helper whose whole purpose is preventing typos is worthless if IT holds the typo, and
/// worse than worthless if it drifts after a rename — which this repo has now done twice. These
/// compare against the real sources rather than restating the strings.</para>
/// </summary>
public class ToolNameConstantsTests
{
    [Fact]
    public void TheEightBuiltinConstants_AreTheNamesToolBindingsOffers()
    {
        var offered = ToolBindings.NamesFor(Enum.GetValues<BuiltinTool>()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(8, offered.Count);
        foreach (var name in new[]
        {
            Tool.ReadFile, Tool.WriteFile, Tool.ReplaceInFile, Tool.Glob,
            Tool.Grep, Tool.RunShell, Tool.HttpRequest, Tool.WebFetch,
        })
            Assert.Contains(name, offered);
    }

    [Fact]
    public void TheFourFixedConstants_AreTheirToolsOwnNames()
    {
        // Read from the tools themselves. Restating "agent" here would pin the constant against a
        // copy of itself and catch nothing.
        Assert.Equal(Tool.TodoWrite, new TodoTool(new TodoList()).ToolName);
        Assert.Equal(Tool.Skill, new SkillLoader(() => new SkillCatalogResult([], [], null)).ToolName);
        Assert.Equal(Tool.AskUser, new AskUserTool((_, _) => Task.FromResult(new QuestionAnswers([]))).ToolName);

        // Tool.Agent is pinned in SubAgentSpawnerTests.OnlyTheCurrentSpawnNameIsOffered — that file
        // already builds a factory, and a second copy of the scaffolding here would be the thing
        // that rots.
    }

    [Fact]
    public void NotAndAlso_PrefixTheSameNames()
    {
        Assert.Equal("-" + Tool.RunShell, Tool.Not.RunShell);
        Assert.Equal("+" + Tool.Grep, Tool.Also.Grep);
        Assert.Equal("-glob", Tool.Not.Glob);      // and not "-list_files"
        Assert.Equal("+ask_user", Tool.Also.AskUser);
    }

    [Fact]
    public void AConstantSelectionResolvesTheSameAsALiteralOne()
    {
        var byConstant = new ToolSelection([Tool.Inherited, Tool.Not.RunShell]);
        var byLiteral = new ToolSelection(["inherited", "-run_shell"]);

        Assert.Equal(byLiteral, byConstant);
    }
}

using CxAgent.Core.Sessions;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The property four bugs had in common: no allow-exit may pass on a partial examination.
///
/// <para>These tests are deliberately about the PROPERTY, not the five known holes. Pinning the
/// holes is what the previous four fixes did, and it did not stop the fifth — because each fix
/// asserted a specific command and none asserted "everything was looked at". The last group below
/// is the one that matters: it fails for a token shape nobody has thought of yet.</para>
/// </summary>
public class CommandSubjectsTests
{
    // ---- what a decision must look at ------------------------------------------------------------

    [Fact]
    public void AnOrdinaryReadNamesItsPath_AndNothingIsLeftOver()
    {
        var dir = Directory.CreateTempSubdirectory("subj").FullName;
        var file = Path.Combine(dir, "notes.txt");
        File.WriteAllText(file, "x");

        var subjects = CommandSubjects.Of($"cat {file}");

        Assert.True(subjects.FullyExamined);
        Assert.Equal([file], subjects.Paths);
    }

    // A PATTERN IS NOT A FILE, and treating it as one would make `grep TODO .` refuse for want of a
    // file called TODO. Friction with no safety behind it is what gets a gate routed around.
    [Fact]
    public void AGrepPatternIsNotAPath_AndDoesNotCountAsUnexamined()
    {
        var subjects = CommandSubjects.Of("grep TODO-NOT-A-REAL-FILE .");

        Assert.True(subjects.FullyExamined);
        Assert.DoesNotContain("TODO-NOT-A-REAL-FILE", subjects.Paths);
    }

    // A BARE FLAG SELECTS BEHAVIOUR WITHIN A VERB ALREADY VOUCHED FOR. Confining `-la` to the folder
    // would be absurd, so flags are not subjects — but see the flag-VALUE test below.
    [Fact]
    public void ABareFlagIsNotASubject()
    {
        var subjects = CommandSubjects.Of("ls -la");

        Assert.True(subjects.FullyExamined);
        Assert.Empty(subjects.Paths);
    }

    // THE PLAIN SPLIT DROPPED THIS ENTIRELY. `cat "my notes.txt"` became two tokens, neither of
    // which exists, so both were discarded and the command was confined against an empty path list —
    // silent, on a file nothing had checked.
    [Fact]
    public void AQuotedPathWithASpace_IsOneSubject()
    {
        var dir = Directory.CreateTempSubdirectory("subj").FullName;
        var file = Path.Combine(dir, "my notes.txt");
        File.WriteAllText(file, "x");

        var subjects = CommandSubjects.Of($"cat \"{file}\"");

        Assert.True(subjects.FullyExamined);
        Assert.Equal([file], subjects.Paths);
    }

    // ---- the five holes, each as its own regression ----------------------------------------------

    // BUG 5, found by writing CommandSubjects. `--file=/etc/shadow` is a path wearing a flag's
    // clothes: it starts with a dash, so the old collector skipped it, and the policy then confined
    // the one path it could see and returned true. The `-f` spelling was refused correctly, because
    // the path happened to land in a token of its own — the same read, opposite answers.
    [Fact]
    public void AFlagCarryingItsValue_IsExaminedLikeASeparateArgument()
    {
        var withEquals = CommandSubjects.Of("grep --file=/etc/passwd .");
        var withSpace = CommandSubjects.Of("grep -f /etc/passwd .");

        Assert.Contains("/etc/passwd", withEquals.Paths);
        Assert.Contains("/etc/passwd", withSpace.Paths);
    }

    // A TILDE IS A PATH THIS DOES NOT RESOLVE, so it must be reported rather than dropped. Expanding
    // it is the other tempting fix and it is worse: a second path resolver is how the boundary and
    // the prompt come to disagree about what is being read.
    [Fact]
    public void ATilde_IsUnexamined_NotSilentlyIgnored()
    {
        var subjects = CommandSubjects.Of("ls ~/");

        Assert.False(subjects.FullyExamined);
        Assert.Contains(subjects.Unexamined, u => u.Contains("tilde", StringComparison.Ordinal));
    }

    // A GLOB NAMES A SET NOBODY ENUMERATED. What `cat *.pem` reads is decided at run time by the
    // shell, so the string cannot be checked — the classic "inspected one thing, ran another".
    [Fact]
    public void AGlob_IsUnexamined()
    {
        Assert.False(CommandSubjects.Of("cat *.pem").FullyExamined);
        Assert.False(CommandSubjects.Of("cat conf?.yml").FullyExamined);
        Assert.False(CommandSubjects.Of("cat [abc].txt").FullyExamined);
    }

    /// <summary>
    /// A QUOTED PATTERN IS NOT A GLOB, because the shell expands a glob only when it is unquoted.
    ///
    /// <para>MEASURED, not theorised: the first version of this type checked for <c>[</c> anywhere
    /// and refused 43 invocations of ordinary bracketed <c>grep</c> searches in the corpus. Those
    /// commands happened to prompt for an unrelated reason, so nothing broke — but on an in-boundary
    /// search it would have been pure friction, which is what gets a gate routed around.</para>
    /// </summary>
    [Theory]
    [InlineData("grep -n 'Replace.*\\[\\[' notes.txt")]
    [InlineData("grep -rn '[A-Z]+' .")]
    [InlineData("grep \"[0-9]*\" notes.txt")]
    public void AQuotedPattern_IsNotAGlob(string command)
    {
        Assert.True(CommandSubjects.Of(command).FullyExamined, command);
    }

    /// <summary>
    /// A GLOB IN A FLAG VALUE IS A FILTER, NOT A READ TARGET. <c>--include=*.cs</c> narrows what grep
    /// looks at within paths given elsewhere, and those are confined separately — so it cannot name a
    /// file outside them however it expands. A BARE glob is the opposite: it IS the target.
    ///
    /// <para>This pair is why the glob check runs after the flag branch rather than before it.
    /// Checking globs first made the two identical and refused the codebase-search idiom, 30
    /// invocations in the corpus.</para>
    /// </summary>
    [Fact]
    public void AGlobInAFlagValue_IsAFilter_ButABareGlobIsATarget()
    {
        Assert.True(CommandSubjects.Of("grep --include=*.cs -rn TODO .").FullyExamined);
        Assert.False(CommandSubjects.Of("cat *.pem").FullyExamined);
    }

    // AN UNTERMINATED QUOTE MUST NOT BE GUESSED AT. The shell joins it with whatever follows, so any
    // guess here inspects a different command than the one that runs.
    [Fact]
    public void AnUnterminatedQuote_IsUnexamined()
    {
        var subjects = CommandSubjects.Of("cat \"unclosed");

        Assert.False(subjects.FullyExamined);
        Assert.Contains(subjects.Unexamined, u => u.Contains("quote", StringComparison.Ordinal));
    }

    // AN EMPTY COMMAND IS NOT "NOTHING TO CHECK". An empty result reads as full coverage of nothing,
    // which is the exact confusion this type is named after.
    [Fact]
    public void AnEmptyCommand_IsUnexamined_RatherThanTriviallyClean()
    {
        Assert.False(CommandSubjects.Of("").FullyExamined);
        Assert.False(CommandSubjects.Of("   ").FullyExamined);
        Assert.False(CommandSubjects.Of(null).FullyExamined);
    }

    // THE cd IDIOM IS STILL PARSED, and its target reported separately so the caller can confine it.
    // This is the shape the model writes constantly; breaking it would be the friction that makes
    // someone grant `cd*` again.
    [Fact]
    public void ALeadingCd_ReportsItsTarget_AndExaminesTheRest()
    {
        var dir = Directory.CreateTempSubdirectory("subj").FullName;

        var subjects = CommandSubjects.Of($"cd {dir} && ls");

        Assert.True(subjects.FullyExamined);
        Assert.Equal(dir, subjects.ChangesTo);
    }

    // ---- the property itself ---------------------------------------------------------------------

    /// <summary>
    /// THE TEST THAT WOULD HAVE CAUGHT BUG FIVE BEFORE IT SHIPPED, and is meant to catch bug six.
    ///
    /// <para>It asserts no specific command. It says: for a token that names a real file outside the
    /// boundary, in ANY of these spellings, the command must not be silently allowed. Each spelling
    /// is a way a path can hide — a separate argument, a flag value, a quoted string, behind a cd.
    /// A new spelling added to this list is the cheapest possible regression test, and a new token
    /// shape that this file does not list still fails closed, because CommandSubjects reports
    /// anything it cannot classify rather than skipping it.</para>
    /// </summary>
    [Theory]
    [InlineData("cat {0}")]
    [InlineData("grep x {0}")]
    [InlineData("grep --file={0} .")]
    [InlineData("grep -f {0} .")]
    [InlineData("head -c 10 {0}")]
    [InlineData("cat \"{0}\"")]
    [InlineData("cat '{0}'")]
    public void NoSpellingOfAnOutOfBoundaryRead_IsEverSilent(string template)
    {
        var root = Directory.CreateTempSubdirectory("subj-root").FullName;
        var outside = Path.Combine(Directory.CreateTempSubdirectory("subj-out").FullName, "secret");
        File.WriteAllText(outside, "shh");

        var rules = new PermissionRulesStore(new AppPaths(Directory.CreateTempSubdirectory("cfg").FullName));
        rules.SetTrust(root, TrustState.Trusted);
        var policy = new PermissionPolicy(root, rules, EditMode.AcceptEdits);

        var command = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, outside);
        var request = new PermissionRequest(PermissionKind.Shell, command, null) { Policy = policy };

        Assert.False(policy.IsSilentlyAllowed(request), $"silently allowed: {command}");
    }

    /// <summary>
    /// AND THE SAME SPELLINGS INSIDE THE BOUNDARY STAY SILENT. Without this, the test above could be
    /// satisfied by making everything ask — which would "pass" while destroying the behaviour the
    /// boundary exists to provide. The two together are the actual contract.
    /// </summary>
    [Theory]
    [InlineData("cat {0}")]
    [InlineData("grep x {0}")]
    [InlineData("grep --file={0} .")]
    [InlineData("head -c 10 {0}")]
    [InlineData("cat \"{0}\"")]
    public void TheSameSpellingsInsideTheBoundary_StaySilent(string template)
    {
        var root = Directory.CreateTempSubdirectory("subj-root").FullName;
        var inside = Path.Combine(root, "notes.txt");
        File.WriteAllText(inside, "ok");

        var rules = new PermissionRulesStore(new AppPaths(Directory.CreateTempSubdirectory("cfg").FullName));
        rules.SetTrust(root, TrustState.Trusted);
        var policy = new PermissionPolicy(root, rules, EditMode.AcceptEdits);

        var command = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, inside);
        var request = new PermissionRequest(PermissionKind.Shell, command, null) { Policy = policy };

        Assert.True(policy.IsSilentlyAllowed(request), $"needlessly prompted: {command}");
    }

    /// <summary>
    /// A STORED RULE IS HELD TO THE SAME STANDARD AS THE FREE PASS. `cat*` is an honest grant for
    /// reading this project, and it was also permitting `cat /etc/passwd` because a rule matched the
    /// command TEXT. Both doors were fixed separately last time, which is precisely why one of them
    /// still had the flag-value hole — so both are asserted here, together.
    /// </summary>
    [Fact]
    public void AStoredRule_CannotReachOutsideTheBoundary_InAnySpelling()
    {
        var root = Directory.CreateTempSubdirectory("subj-root").FullName;
        var outside = Path.Combine(Directory.CreateTempSubdirectory("subj-out").FullName, "secret");
        File.WriteAllText(outside, "shh");

        var rules = new PermissionRulesStore(new AppPaths(Directory.CreateTempSubdirectory("cfg").FullName));
        rules.SetTrust(root, TrustState.Trusted);
        rules.Add(root, PermissionKind.Shell, "grep*");
        var policy = new PermissionPolicy(root, rules, EditMode.AcceptEdits);

        foreach (var command in new[] { $"grep x {outside}", $"grep --file={outside} ." })
        {
            var request = new PermissionRequest(PermissionKind.Shell, command, "grep*") { Policy = policy };
            Assert.False(policy.IsSilentlyAllowed(request), $"rule reached outside: {command}");
        }
    }
}

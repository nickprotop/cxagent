using System.Linq;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using Xunit;

namespace CxAgent.Tests;

public class PermissionPolicyTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-perm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static PermissionRulesStore EmptyRules() =>
        new PermissionRulesStore(new CxAgent.Core.Storage.AppPaths(MakeTempDir()));

    private static PermissionRulesStore RulesWith(string scope, PermissionKind kind, string rule)
    {
        var store = new PermissionRulesStore(new CxAgent.Core.Storage.AppPaths(MakeTempDir()));
        store.Add(scope, kind, rule);
        return store;
    }

    private static PermissionRequest FileWrite(string path) =>
        new(PermissionKind.FileWrite, path, path);

    private static PermissionRequest FileRead(string path) =>
        new(PermissionKind.FileRead, path, path);

    private static PermissionRequest Shell(string command) =>
        new(PermissionKind.Shell, command, command);

    private static PermissionRequest Http(string url) =>
        new(PermissionKind.Http, url, url);

    private static JobParameters Params(params (string Key, object? Value)[] entries)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in entries) dict[key] = value;
        return new JobParameters(dict);
    }

    [Fact]
    public void FileWrite_UnderTheWorkingFolder_IsSilent_ButOutsideNeedsAPrompt()
    {
        var root = MakeTempDir();
        var rules = EmptyRules();
        rules.SetTrust(root, TrustState.Trusted);
        var policy = new PermissionPolicy(root, rules);
        Assert.True(policy.IsSilentlyAllowed(FileWrite(Path.Combine(root, "notes.txt"))));
        Assert.False(policy.IsSilentlyAllowed(FileWrite("/tmp/elsewhere.txt")));
    }

    /// <summary>
    /// A COMMAND THAT CAN ONLY LOOK IS THE FILE READ THAT ALREADY PASSES. Measured on an agentic
    /// drive: thirteen shell calls in one turn, and the run only finished because approvals were
    /// automated — a gate noisy enough to be routed around is worse than a coarser one that is kept.
    /// </summary>
    [Fact]
    public void AReadOnlyCommand_InATrustedFolder_IsSilent()
    {
        var root = MakeTempDir();
        var rules = EmptyRules();
        rules.SetTrust(root, TrustState.Trusted);
        var policy = new PermissionPolicy(root, rules);

        Assert.True(policy.IsSilentlyAllowed(Shell("ls -la src")));
        Assert.True(policy.IsSilentlyAllowed(Shell("grep -rn TODO .")));
        Assert.True(policy.IsSilentlyAllowed(Shell("cat README.md")));
    }

    /// <summary>
    /// AND EVERYTHING ELSE STILL ASKS. The exemption is a short list of programs that cannot write,
    /// not a judgement about what a command probably does.
    /// </summary>
    [Fact]
    public void AnythingThatCanWrite_StillPrompts_EvenInATrustedFolder()
    {
        var root = MakeTempDir();
        var rules = EmptyRules();
        rules.SetTrust(root, TrustState.Trusted);
        var policy = new PermissionPolicy(root, rules);

        Assert.False(policy.IsSilentlyAllowed(Shell("rm -rf build")));
        Assert.False(policy.IsSilentlyAllowed(Shell("dotnet build")));
        Assert.False(policy.IsSilentlyAllowed(Shell("git status")));

        // A safe verb with a chain is not a safe command — the guard that makes the rest defensible.
        Assert.False(policy.IsSilentlyAllowed(Shell("cat x; rm -rf /")));
        Assert.False(policy.IsSilentlyAllowed(Shell("grep foo > /etc/passwd")));
    }

    /// <summary>
    /// TRUST IS STILL REQUIRED. An untrusted folder prompts for everything — the exemption rides on
    /// the same decision the user already made about file reads, not on the command alone.
    /// </summary>
    [Fact]
    public void AReadOnlyCommand_InAnUntrustedFolder_StillPrompts()
    {
        var root = MakeTempDir();
        var policy = new PermissionPolicy(root, EmptyRules());   // nothing arranged → Unknown

        Assert.False(policy.IsSilentlyAllowed(Shell("ls -la")));
    }

    [Fact]
    public void AnUnclassifiedFolder_HasNoSilentClass_AnUnansweredQuestionIsNotAYes()
    {
        var root = MakeTempDir();
        var policy = new PermissionPolicy(root, EmptyRules());   // nothing arranged → Unknown
        Assert.False(policy.IsSilentlyAllowed(FileWrite(Path.Combine(root, "notes.txt"))));
        Assert.False(policy.IsSilentlyAllowed(FileRead(Path.Combine(root, "notes.txt"))));
    }

    [Fact]
    public void TrustingTheFolder_TurnsTheSilentClassOn_ThereAndOnlyThere()
    {
        // "a folder is a project" — trust in project A says nothing about project B.
        var rootA = MakeTempDir();
        var rootB = MakeTempDir();
        var rules = EmptyRules();
        rules.SetTrust(rootA, TrustState.Trusted);

        Assert.True(new PermissionPolicy(rootA, rules).IsSilentlyAllowed(FileWrite(Path.Combine(rootA, "x"))));
        Assert.False(new PermissionPolicy(rootB, rules).IsSilentlyAllowed(FileWrite(Path.Combine(rootB, "x"))));
    }

    [Fact]
    public void Trust_NeverSilencesAShellCommandThatCouldWrite_OrHttp_OrAnOutOfBoundaryPath()
    {
        // Trust grants exactly the class the boundary can police. `cd / && rm -rf .` escapes the
        // folder in its first six characters — a trust that silenced shell WHOLESALE would grant far
        // more than a folder.
        //
        // NARROWED, NOT ABANDONED. This asserted bare `ls` as its example, and `ls` is now exempt:
        // it cannot write however it is invoked, which makes it the file READ that trust already
        // silences, spelled as a command. The invariant that matters is unchanged and is what the
        // example now shows — anything that could write, or could become something that writes,
        // still asks. See ReadOnlyCommands for why the list is short and hand-checked.
        var root = MakeTempDir();
        var rules = EmptyRules();
        rules.SetTrust(root, TrustState.Trusted);
        var policy = new PermissionPolicy(root, rules);

        Assert.False(policy.IsSilentlyAllowed(Shell("cd / && rm -rf .")));
        Assert.False(policy.IsSilentlyAllowed(Shell("rm -rf build")));
        Assert.False(policy.IsSilentlyAllowed(Http("https://api.example.com")));
        Assert.False(policy.IsSilentlyAllowed(FileWrite("/tmp/outside.txt")));
    }

    [Fact]
    public void AnExplicitRule_StillMatches_InAnUntrustedFolder()
    {
        // Untrusted removes the IMPLICIT class only. A stored rule is the user's own explicit
        // grant — this is also how untrusted-folder noise decays without granting full trust.
        var root = MakeTempDir();
        var rules = RulesWith(root, PermissionKind.FileRead, root + "/");
        rules.SetTrust(root, TrustState.Untrusted);
        Assert.True(new PermissionPolicy(root, rules)
            .IsSilentlyAllowed(FileRead(Path.Combine(root, "src", "a.cs"))));
    }

    [Fact]
    public void APathTheFilesystemRejectsOutright_FailsClosed_RatherThanThrowingOutOfTheProducer()
    {
        // Finding N2. RequestsFor now RESOLVES (the C1/C2 fix), so it performs filesystem calls and
        // can throw where the old string-only code could not. Path.GetFullPath raises
        // ArgumentException — not IOException — for an embedded NUL, and TryResolve's catch list
        // originally omitted it, so the exception escaped the producer entirely.
        //
        // The job executor's blanket catch meant nothing was WRITTEN, so this always failed closed;
        // but it surfaced as a raw "Null character in path" crash instead of a clean denial, and the
        // result lost PermissionDenied = true — so the orchestrator could not tell a refusal from a
        // malfunction.
        var root = MakeTempDir();
        var policy = new PermissionPolicy(root, EmptyRules());

        var requests = PermissionPolicy.RequestsFor("file", Params(
            ("action", "write"), ("path", "/tmp/bad\0name.txt"), ("content", "x")));

        var request = Assert.Single(requests);
        Assert.Null(request.AlwaysRule);                     // unresolvable => no rule can be stated
        Assert.False(policy.IsSilentlyAllowed(request));     // ...and it is never silently allowed
    }

    [Fact]
    public void TheSTORE_MatchesADirectoryRuleAgainstFilesInsideIt()
    {
        // SCOPE: this pins the STORE's matching only — the rule is hand-injected below. It does
        // NOT test that anything ever PRODUCES a directory rule; see
        // TheProducer_BuildsADirectoryRule_NotAPerFileRule for that.
        //
        // The distinction is not pedantry. This test used to be named
        // "ADirectoryRule_TheFormTheAlwaysButtonWRITES_MatchesFilesInsideIt" and claimed exactly
        // the producer property it never exercised. It passed for weeks while the Always button
        // wrote per-FILE rules and the promised "Always allow writes under X/" affordance did not
        // exist at all (finding C1). A test whose name asserts more than its body checks is worse
        // than no test: it answers the question nobody then re-asks.
        var root = MakeTempDir();
        var rules = RulesWith(root, PermissionKind.FileWrite, root + "/");
        rules.SetTrust(root, TrustState.Untrusted);   // no implicit class; the RULE must carry it
        var policy = new PermissionPolicy(root, rules);

        Assert.True(policy.IsSilentlyAllowed(FileWrite(Path.Combine(root, "a.txt"))));
        Assert.True(policy.IsSilentlyAllowed(FileWrite(Path.Combine(root, "sub", "b.txt"))));
        // and must NOT leak to a sibling directory that merely shares the prefix
        Assert.False(policy.IsSilentlyAllowed(FileWrite(root + "X/c.txt")));
    }

    [Fact]
    public void DotDotTraversal_DoesNotDefeatTheBoundary()
    {
        // A boundary that "../../../etc/passwd" walks straight through is theatre.
        var root = MakeTempDir();
        var sneaky = Path.Combine(root, "sub", "..", "..", "..", "etc", "passwd");
        Assert.False(new PermissionPolicy(root, EmptyRules()).IsSilentlyAllowed(FileWrite(sneaky)));
    }

    [Fact]
    public void ASymlinkInsideTheFolder_PointingOutside_IsOutside()
    {
        // GetFullPath is lexical only — a link is the other door, and it must count as where it GOES.
        var root = MakeTempDir();
        var outside = MakeTempDir();
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
        var viaLink = Path.Combine(root, "link", "x.txt");
        Assert.False(new PermissionPolicy(root, EmptyRules()).IsSilentlyAllowed(FileWrite(viaLink)));
    }

    [Fact]
    public void Shell_IsNeverSilentByPath_OnlyByRule()
    {
        // A command string says nothing reliable about what it touches — "cd / && rm -rf ." runs
        // fine from inside cwd. Shell has no in-boundary free pass, full stop.
        var root = MakeTempDir();
        var policy = new PermissionPolicy(root, EmptyRules());
        Assert.False(policy.IsSilentlyAllowed(Shell("ls")));

        var rules = RulesWith(root, PermissionKind.Shell, "ls");
        Assert.True(new PermissionPolicy(root, rules).IsSilentlyAllowed(Shell("ls")));
        Assert.False(new PermissionPolicy(root, rules).IsSilentlyAllowed(Shell("ls -la")));  // exact, not prefix
    }

    [Fact]
    public void AShellJobWithACustomEnv_IsNeverSilencedByACommandRule()
    {
        // env: {LD_PRELOAD: ...} makes `ls` a different program. The same string is not the
        // same command, so the stored rule for the plain string must not cover it — such a
        // request carries AlwaysRule = null and matches nothing.
        var root = MakeTempDir();
        var rules = RulesWith(root, PermissionKind.Shell, "ls");
        var withEnv = PermissionPolicy.RequestsFor("shell", Params(
            ("command", "ls"), ("env", new Dictionary<string, string> { ["LD_PRELOAD"] = "/tmp/evil.so" })))
            .Single();
        Assert.Null(withEnv.AlwaysRule);
        Assert.False(new PermissionPolicy(root, rules).IsSilentlyAllowed(withEnv));
        Assert.Contains("LD_PRELOAD", withEnv.Display);   // the user must SEE what makes it different
    }

    [Fact]
    public void AHandEditedWildcardRule_MatchesAsAPrefix()
    {
        var root = MakeTempDir();
        var rules = RulesWith(root, PermissionKind.Shell, "git status*");
        Assert.True(new PermissionPolicy(root, rules).IsSilentlyAllowed(Shell("git status --short")));
        Assert.False(new PermissionPolicy(root, rules).IsSilentlyAllowed(Shell("git push")));
    }

    [Fact]
    public void CopysTwoLegs_AreCheckedIndependently()
    {
        // copy reads the source and writes the dest; an in-tree source must not smuggle an
        // out-of-tree dest through (nor vice versa).
        var root = MakeTempDir();
        var reqs = PermissionPolicy.RequestsFor("file", Params(
            ("action", "copy"), ("path", Path.Combine(root, "a.txt")), ("dest", "/tmp/out.txt")));
        Assert.Contains(reqs, r => r.Kind == PermissionKind.FileRead);
        Assert.Contains(reqs, r => r.Kind == PermissionKind.FileWrite && r.Display == "/tmp/out.txt");
    }

    // ---- C1: the PRODUCER must mint a directory rule, not a per-file one ----------------------
    //
    // ADirectoryRule_TheFormTheAlwaysButtonWRITES_MatchesFilesInsideIt (above) proves the STORE
    // matches a directory rule if handed one. It never proves anything PRODUCES one — that gap is
    // exactly how C1 shipped. These tests drive the real producer, PermissionPolicy.RequestsFor,
    // and assert the shape of what it actually builds.

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("append")]
    [InlineData("delete")]
    public void TheProducer_BuildsADirectoryRule_NotAPerFileRule(string action)
    {
        var root = MakeTempDir();
        var file = Path.Combine(root, "a.txt");
        var req = PermissionPolicy.RequestsFor("file", Params(("action", action), ("path", file))).Single();

        var expectedDir = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        Assert.Equal(expectedDir, req.AlwaysRule);
        Assert.NotEqual(file, req.AlwaysRule);   // the bug: AlwaysRule used to be the exact file
    }

    [Fact]
    public void TheProducer_BuildsADirectoryRule_ForBothLegsOfCopyAndMove()
    {
        foreach (var action in new[] { "copy", "move" })
        {
            var srcRoot = MakeTempDir();
            var destRoot = MakeTempDir();
            var src = Path.Combine(srcRoot, "a.txt");
            var dest = Path.Combine(destRoot, "b.txt");

            var reqs = PermissionPolicy.RequestsFor("file", Params(
                ("action", action), ("path", src), ("dest", dest)));

            var expectedSrcDir = Path.TrimEndingDirectorySeparator(srcRoot) + Path.DirectorySeparatorChar;
            var expectedDestDir = Path.TrimEndingDirectorySeparator(destRoot) + Path.DirectorySeparatorChar;

            var readReq = reqs.Single(r => r.Kind == PermissionKind.FileRead);
            var writeReq = reqs.Single(r => r.Kind == PermissionKind.FileWrite);
            Assert.Equal(expectedSrcDir, readReq.AlwaysRule);
            Assert.Equal(expectedDestDir, writeReq.AlwaysRule);
        }
    }

    [Fact]
    public void AnAlwaysGrant_ProducedByTheRealProducer_SilencesASiblingFile()
    {
        // The affordance the spec promises, end to end through the real producer (not
        // hand-injected): grant on one file, and a SIBLING in the same directory — never
        // mentioned in the grant — must go silent too.
        var root = MakeTempDir();
        var a = Path.Combine(root, "a.txt");
        var b = Path.Combine(root, "b.txt");

        var grantReq = PermissionPolicy.RequestsFor("file", Params(("action", "write"), ("path", a))).Single();
        var rules = RulesWith(root, PermissionKind.FileWrite, grantReq.AlwaysRule!);
        rules.SetTrust(root, TrustState.Untrusted);   // no implicit class; the RULE must carry it
        var policy = new PermissionPolicy(root, rules);

        var siblingReq = PermissionPolicy.RequestsFor("file", Params(("action", "write"), ("path", b))).Single();
        Assert.True(policy.IsSilentlyAllowed(siblingReq));
    }

    // ---- C2: a directory rule must not be escapable via ".." or a symlink ---------------------

    [Fact]
    public void C2_ADirectoryRule_IsNotDefeatedByDotDotTraversal()
    {
        var root = MakeTempDir();
        var rules = RulesWith(root, PermissionKind.FileWrite, root + Path.DirectorySeparatorChar);
        rules.SetTrust(root, TrustState.Untrusted);
        var policy = new PermissionPolicy(root, rules);

        var victim = Path.Combine(root, "..", "VICTIM.txt");
        // Lexically inside root's parent chain but resolves OUTSIDE root — must NOT be silent.
        Assert.False(policy.IsSilentlyAllowed(FileWrite(victim)));
    }

    [Fact]
    public void C2_ADirectoryRule_IsNotDefeatedByASymlinkPointingOutside()
    {
        var root = MakeTempDir();
        var outside = MakeTempDir();
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);

        var rules = RulesWith(root, PermissionKind.FileWrite, root + Path.DirectorySeparatorChar);
        rules.SetTrust(root, TrustState.Untrusted);
        var policy = new PermissionPolicy(root, rules);

        var viaLink = Path.Combine(root, "link", "victim.txt");
        Assert.False(policy.IsSilentlyAllowed(FileWrite(viaLink)));
    }

    [Fact]
    public void ARequestWithNoAlwaysRule_IsNeverSilentlyAllowedByAStoredRule()
    {
        // The documented fail-closed contract (PermissionRequest's own doc comment): AlwaysRule
        // == null means "cannot be truthfully generalised", and no stored rule may ever match
        // it — this is what FileRequest falls back to when TryResolve fails (mirrors the AShell-
        // JobWithACustomEnv_IsNeverSilencedByACommandRule case for the file side of the contract).
        var root = MakeTempDir();
        var rules = RulesWith(root, PermissionKind.FileWrite, root + Path.DirectorySeparatorChar);
        rules.SetTrust(root, TrustState.Untrusted);
        var policy = new PermissionPolicy(root, rules);

        var unresolved = new PermissionRequest(PermissionKind.FileWrite, Path.Combine(root, "a.txt"), null);
        Assert.False(policy.IsSilentlyAllowed(unresolved));
    }
}

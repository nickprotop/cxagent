using System.Linq;
using CxAgent.Core.Agent;
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
    /// `cd &lt;dir&gt; &amp;&amp; &lt;read-only&gt;` IS THE IDIOM THE MODEL WRITES, and it used to prompt for the
    /// `&amp;&amp;` alone — so `cd /repo &amp;&amp; ls` asked while a bare `ls` did not. Two of three prompts on a
    /// measured drive were this shape.
    /// </summary>
    [Fact]
    public void ACdIntoTheTrustedFolder_FollowedByAReadOnlyCommand_IsSilent()
    {
        var root = MakeTempDir();
        var rules = EmptyRules();
        rules.SetTrust(root, TrustState.Trusted);
        var policy = new PermissionPolicy(root, rules);

        Assert.True(policy.IsSilentlyAllowed(Shell($"cd {root} && ls -la")));
        Assert.True(policy.IsSilentlyAllowed(Shell($"cd {root}/src && grep -rn TODO .")));

        // And `cd` on its own, which changes no files and reads nothing.
        Assert.True(policy.IsSilentlyAllowed(Shell($"cd {root}")));
    }

    /// <summary>
    /// THE ESCAPE THE BOUNDARY EXISTS TO CLOSE. `cat shadow` is read-only by any measure — the only
    /// thing standing between it and /etc is where the `cd` went, which is why the target is checked
    /// rather than stripped and forgotten.
    /// </summary>
    [Fact]
    public void ACdOutOfTheTrustedFolder_StillPrompts_EvenForAReadOnlyCommand()
    {
        var root = MakeTempDir();
        var rules = EmptyRules();
        rules.SetTrust(root, TrustState.Trusted);
        var policy = new PermissionPolicy(root, rules);

        Assert.False(policy.IsSilentlyAllowed(Shell("cd /etc && cat shadow")));
        Assert.False(policy.IsSilentlyAllowed(Shell("cd /etc")));

        // A traversal that LOOKS like it stays inside is resolved, not pattern-matched.
        Assert.False(policy.IsSilentlyAllowed(Shell($"cd {root}/../.. && ls")));
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

    // ---- symlinked DIRECTORIES in the middle of a path ------------------------------------------

    /// <summary>
    /// A TRUSTED policy, which is the only shape where the boundary is load-bearing. An untrusted
    /// folder asks for everything, so a boundary bug is invisible against one — which is exactly how
    /// the escape below survived: the existing symlink test above uses an untrusted folder AND a file
    /// that does not exist, and either of those alone is enough to make it pass.
    /// </summary>
    private static PermissionPolicy TrustedPolicy(string root)
    {
        var rules = EmptyRules();
        rules.SetTrust(root, TrustState.Trusted);
        return new PermissionPolicy(root, rules);
    }

    /// <summary>
    /// AN EXISTING FILE THROUGH A SYMLINKED DIRECTORY IS OUTSIDE. TryResolve used to walk up only to
    /// the deepest EXISTING entry and resolve that; when the file itself exists the walk stops on the
    /// file, which is not a link — its PARENT is — so the link was never followed.
    ///
    /// <para>THE DANGEROUS DIRECTION: this is the OVERWRITE case. A repo with `vendor -> /elsewhere`
    /// — an ordinary layout — let a trusted session rewrite a file outside the folder with no
    /// prompt.</para>
    /// </summary>
    [Fact]
    public void AnExistingFileThroughASymlinkedDirectory_IsOutside()
    {
        var root = MakeTempDir();
        var outside = MakeTempDir();
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
        File.WriteAllText(Path.Combine(outside, "x.txt"), "victim");

        var viaLink = Path.Combine(root, "link", "x.txt");

        Assert.False(TrustedPolicy(root).IsSilentlyAllowed(FileWrite(viaLink)));
    }

    /// <summary>
    /// NESTING BELOW THE LINK DEFEATS IT EVEN FOR A NEW FILE. With a directory under the link, the
    /// deepest existing entry is that directory rather than the link, so the link is skipped again.
    ///
    /// <para>This row says how big the bug was. A fixture where the link IS the deepest existing
    /// entry catches the new-file case and reports the escape as half its true size.</para>
    /// </summary>
    [Fact]
    public void ANewFileNestedBelowASymlinkedDirectory_IsOutside()
    {
        var root = MakeTempDir();
        var outside = MakeTempDir();
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
        Directory.CreateDirectory(Path.Combine(outside, "sub"));

        var viaLink = Path.Combine(root, "link", "sub", "new.txt");

        Assert.False(TrustedPolicy(root).IsSilentlyAllowed(FileWrite(viaLink)));
    }

    /// <summary>
    /// AN ORDINARY IN-BOUNDARY WRITE IS STILL SILENT, existing file or new one.
    ///
    /// <para>The fix must not close the escape by making everything ask — that would "pass" both
    /// tests above while destroying the behaviour the boundary exists to provide.</para>
    /// </summary>
    [Fact]
    public void AnOrdinaryInBoundaryWrite_StaysSilent_AfterTheSymlinkFix()
    {
        var root = MakeTempDir();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "existing.cs"), "// code");

        var policy = TrustedPolicy(root);

        Assert.True(policy.IsSilentlyAllowed(FileWrite(Path.Combine(root, "src", "existing.cs"))));
        Assert.True(policy.IsSilentlyAllowed(FileWrite(Path.Combine(root, "src", "brand-new.cs"))));
    }

    // ---- the edit mode --------------------------------------------------------------------------

    private static PermissionPolicy TrustedPolicy(string root, EditMode edits)
    {
        var rules = EmptyRules();
        rules.SetTrust(root, TrustState.Trusted);
        return new PermissionPolicy(root, rules, edits);
    }

    /// <summary>ALWAYSASK SUPPRESSES THE BOUNDARY FREE PASS — the silent path nobody opted into
    /// per-item — even on a trusted folder, inside the boundary.</summary>
    [Fact]
    public void AlwaysAsk_SuppressesTheInBoundaryFreePass()
    {
        var root = MakeTempDir();

        Assert.False(TrustedPolicy(root, EditMode.AlwaysAsk)
            .IsSilentlyAllowed(FileWrite(Path.Combine(root, "notes.txt"))));
    }

    /// <summary>
    /// BUT IT DOES NOT VOID STORED RULES. A mode that silently disabled every saved "Always allow"
    /// would make the user conclude the rules feature is broken, having never been told the mode did
    /// it. AlwaysAsk suppresses the free pass, not decisions made one at a time.
    /// </summary>
    [Fact]
    public void AlwaysAsk_StillHonoursAStoredRule()
    {
        var root = MakeTempDir();
        var rules = EmptyRules();
        rules.SetTrust(root, TrustState.Trusted);
        var target = Path.Combine(root, "notes.txt");
        rules.Add(root, PermissionKind.FileWrite, target);

        Assert.True(new PermissionPolicy(root, rules, EditMode.AlwaysAsk)
            .IsSilentlyAllowed(FileWrite(target)));
    }

    /// <summary>
    /// READS KEEP THEIR FREE PASS UNDER ALWAYSASK. The axis is named edits; prompting to read a file
    /// inside a trusted folder would break every ordinary investigation for no safety gain.
    /// </summary>
    [Fact]
    public void AlwaysAsk_DoesNotAffectReads()
    {
        var root = MakeTempDir();

        Assert.True(TrustedPolicy(root, EditMode.AlwaysAsk)
            .IsSilentlyAllowed(FileRead(Path.Combine(root, "notes.txt"))));
    }

    /// <summary>
    /// TRUST FLOORS THE WIDENING. AcceptEdits on an UNTRUSTED folder still asks — a mode may add
    /// friction, never remove it below what the folder's trust permits.
    ///
    /// <para>The rule most likely to be broken by a later refactor, because it reads like an
    /// exception rather than the invariant it is.</para>
    /// </summary>
    [Fact]
    public void AcceptEdits_OnAnUntrustedFolder_StillAsks()
    {
        var root = MakeTempDir();

        Assert.False(new PermissionPolicy(root, EmptyRules(), EditMode.AcceptEdits)
            .IsSilentlyAllowed(FileWrite(Path.Combine(root, "notes.txt"))));
    }

    /// <summary>The default NAMES WHAT CXAGENT ALREADY DID. This is the test that says the axis
    /// shipped as a pure addition rather than a behaviour change.</summary>
    [Fact]
    public void AcceptEdits_IsTheDefault_AndMatchesTheOldBehaviour()
    {
        var root = MakeTempDir();
        var rules = EmptyRules();
        rules.SetTrust(root, TrustState.Trusted);

        // WorkingMode.Default, not new WorkingMode(): a record struct's parameterless constructor
        // zero-initialises and ignores the parameter defaults, so `new WorkingMode()` is AlwaysAsk.
        // That ordering is deliberate — see EditMode.AlwaysAsk — and Default is the only thing that
        // states the session default.
        Assert.Equal(EditMode.AcceptEdits, WorkingMode.Default.Edits);
        Assert.True(new PermissionPolicy(root, rules)
            .IsSilentlyAllowed(FileWrite(Path.Combine(root, "notes.txt"))));
    }

    /// <summary>
    /// IN-CWD IS SCOPE, NOT SAFETY. .git/hooks/* executes on the next git command and .git/config
    /// carries core.pager and core.fsmonitor, while a user reading "accept edits" pictures source
    /// files.
    /// </summary>
    [Theory]
    [InlineData(".git/hooks/pre-commit")]
    [InlineData(".git/config")]
    [InlineData(".vscode/tasks.json")]
    [InlineData(".claude/settings.json")]
    [InlineData(".idea/workspace.xml")]
    public void AcceptEdits_StillAsksForExecutableConfig(string relative)
    {
        var root = MakeTempDir();
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        Assert.False(TrustedPolicy(root, EditMode.AcceptEdits).IsSilentlyAllowed(FileWrite(path)));
    }

    /// <summary>READS ARE NOT EDITS. The deny-list guards writes; reading .git/config to answer a
    /// question about the repo is ordinary and must not start prompting.</summary>
    [Fact]
    public void TheExecutableConfigDenyList_DoesNotAffectReads()
    {
        var root = MakeTempDir();
        var path = Path.Combine(root, ".git", "config");

        Assert.True(TrustedPolicy(root, EditMode.AcceptEdits).IsSilentlyAllowed(FileRead(path)));
    }

    /// <summary>
    /// SHELL IS UNCHANGED IN EVERY MODE. The write-command list was cut deliberately — a verb-only
    /// check cannot bound where a write command writes — and this says it stayed out.
    /// </summary>
    [Theory]
    [InlineData(EditMode.AlwaysAsk)]
    [InlineData(EditMode.AcceptEdits)]
    public void Shell_BehavesIdentically_UnderEveryEditMode(EditMode mode)
    {
        var root = MakeTempDir();
        var policy = TrustedPolicy(root, mode);

        Assert.True(policy.IsSilentlyAllowed(Shell("ls")));         // read-only verb, trusted: silent
        Assert.False(policy.IsSilentlyAllowed(Shell("mkdir x")));   // not read-only: asks, every mode
    }

    /// <summary>
    /// TRUST FLOORS AUTO TOO. The classifier runs only after IsSilentlyAllowed has said no, so the
    /// floor it must respect is exposed separately — and on an untrusted folder that predicate is
    /// false, which is what stops a classifier's ALLOW from widening past a decision the user made.
    ///
    /// <para>Caught in a live drive: without this the gate consulted the classifier on an untrusted
    /// folder and would have returned true on an allow — the one power no mode may have.</para>
    /// </summary>
    [Fact]
    public void AllowsSilentWrites_IsFalse_OnAnUntrustedFolder()
    {
        var root = MakeTempDir();

        Assert.False(new PermissionPolicy(root, EmptyRules(), EditMode.Auto)
            .AllowsSilentWrites(FileWrite(Path.Combine(root, "notes.txt"))));
    }

    /// <summary>...and false for the executable-config directories, so auto cannot silently write a
    /// git hook either.</summary>
    [Fact]
    public void AllowsSilentWrites_IsFalse_ForExecutableConfig()
    {
        var root = MakeTempDir();
        var policy = TrustedPolicy(root, EditMode.Auto);

        Assert.False(policy.AllowsSilentWrites(FileWrite(Path.Combine(root, ".git", "hooks", "pre-commit"))));
        Assert.True(policy.AllowsSilentWrites(FileWrite(Path.Combine(root, "src.cs"))));
    }

    /// <summary>AUTO IS NEVER SILENT BY POLICY ALONE — it must reach the classifier, which is the one
    /// answer the policy cannot give.</summary>
    [Fact]
    public void Auto_IsNotSilentlyAllowed_EvenInBoundaryAndTrusted()
    {
        var root = MakeTempDir();

        Assert.False(TrustedPolicy(root, EditMode.Auto)
            .IsSilentlyAllowed(FileWrite(Path.Combine(root, "notes.txt"))));
    }

    /// <summary>
    /// MCP IS NOT A FILE WRITE. It has no path and no boundary — RuleSubject returns AlwaysRule for
    /// it — and "accept edits" is a name broad enough that a later reader could think otherwise.
    /// </summary>
    [Theory]
    [InlineData(EditMode.AlwaysAsk)]
    [InlineData(EditMode.AcceptEdits)]
    public void EditMode_NeverWidensMcp(EditMode mode)
    {
        var root = MakeTempDir();

        Assert.False(TrustedPolicy(root, mode).IsSilentlyAllowed(
            new PermissionRequest(PermissionKind.Mcp, "server/tool", "server/tool")));
    }
}

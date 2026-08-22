using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The gate on what a classifier verdict may CHANGE. Every row of the effect table gets a test,
/// including the negative ones — the security property here is what the classifier CANNOT do, and
/// a property nothing pins is a property that comes back off the next refactor.
/// </summary>
public class ReviewEffectTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-effect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static PermissionRulesStore EmptyRules() =>
        new PermissionRulesStore(new CxAgent.Core.Storage.AppPaths(MakeTempDir()));

    private static PermissionRequest FileWrite(string path) =>
        new(PermissionKind.FileWrite, path, path);

    private static PermissionRequest FileRead(string path) =>
        new(PermissionKind.FileRead, path, path);

    private static PermissionPolicy TrustedAuto(string root, PermissionRulesStore rules)
    {
        rules.SetTrust(root, TrustState.Trusted);
        return new PermissionPolicy(root, rules, EditMode.Auto);
    }

    // ---- MayApprove -------------------------------------------------------------------------------

    [Fact]
    public void AFileWriteInATrustedBoundary_MayBeApproved()
    {
        var root = MakeTempDir();
        var policy = TrustedAuto(root, EmptyRules());

        Assert.Equal(ReviewEffect.MayApprove,
            policy.EffectFor(FileWrite(Path.Combine(root, "a.txt"))));
    }

    [Fact]
    public void AFileReadInATrustedBoundary_MayBeApproved()
    {
        // UNREACHABLE FROM THE GATE, AND STILL PINNED. IsSilentlyAllowed lets every trusted
        // in-boundary read through in EVERY mode, so a read never arrives at the classifier line.
        // The arm exists because EffectFor answers a question about a request, not about a code
        // path: if the read free pass is ever narrowed, the answer here must still be the safe one
        // rather than whatever a missing arm happens to fall into.
        var root = MakeTempDir();
        var policy = TrustedAuto(root, EmptyRules());

        Assert.Equal(ReviewEffect.MayApprove,
            policy.EffectFor(FileRead(Path.Combine(root, "a.txt"))));
    }

    // ---- The floor: trust, and only auto -----------------------------------------------------------

    [Fact]
    public void AnUntrustedFolder_IsNotReviewedAtAll()
    {
        // TRUST IS THE FLOOR AND IS CHECKED HERE. This is the guard the old AllowsSilentWrites call
        // provided; losing it would let a classifier ALLOW widen past a decision the user made.
        var root = MakeTempDir();
        var policy = new PermissionPolicy(root, EmptyRules(), EditMode.Auto);

        Assert.Equal(ReviewEffect.None, policy.EffectFor(FileWrite(Path.Combine(root, "a.txt"))));
    }

    [Fact]
    public void AnExplicitlyUntrustedFolder_IsNotReviewedAtAll()
    {
        // Unknown (above) and Untrusted are different states and both must floor. Unknown is the
        // one that arrives by default, so it is the one a refactor is likeliest to mishandle.
        var root = MakeTempDir();
        var rules = EmptyRules();
        rules.SetTrust(root, TrustState.Untrusted);
        var policy = new PermissionPolicy(root, rules, EditMode.Auto);

        Assert.Equal(ReviewEffect.None, policy.EffectFor(FileWrite(Path.Combine(root, "a.txt"))));
    }

    [Fact]
    public void AnUntrustedFolder_DoesNotEvenAnnotate()
    {
        // NOT "UNTRUSTED MEANS NO SILENCING" BUT "UNTRUSTED MEANS NO CLASSIFIER". MayAnnotate looks
        // harmless — it cannot let anything through — and that is exactly the argument that would
        // move egress above the trust check. It sends the action's text to a model, which is itself
        // a thing an untrusted folder has not been granted.
        var root = MakeTempDir();
        var policy = new PermissionPolicy(root, EmptyRules(), EditMode.Auto);

        Assert.Equal(ReviewEffect.None,
            policy.EffectFor(new PermissionRequest(PermissionKind.Http, "POST https://x.dev/a", "https://x.dev")));
    }

    [Fact]
    public void EffectIsNoneOutsideAutoMode()
    {
        var root = MakeTempDir();
        var rules = EmptyRules();
        rules.SetTrust(root, TrustState.Trusted);

        foreach (var mode in new[] { EditMode.AlwaysAsk, EditMode.AcceptEdits })
            Assert.Equal(ReviewEffect.None,
                new PermissionPolicy(root, rules, mode).EffectFor(FileWrite(Path.Combine(root, "a.txt"))));
    }

    // ---- The structural checks the classifier may never overrule -------------------------------------

    [Fact]
    public void AWriteOutsideTheBoundary_IsNotReviewedAtAll()
    {
        // THE CLASSIFIER MAY NEVER OVERRIDE A PATH CHECK. It may disagree with a verb or an
        // operator; the boundary is not its to argue with, so the request never reaches it.
        var root = MakeTempDir();
        var outside = MakeTempDir();
        var policy = TrustedAuto(root, EmptyRules());

        Assert.Equal(ReviewEffect.None,
            policy.EffectFor(FileWrite(Path.Combine(outside, "a.txt"))));
    }

    [Fact]
    public void AWriteToAnExecutableConfigDir_IsNotReviewedAtAll()
    {
        // .git/hooks and friends execute on the next ordinary command, so a write there is not the
        // in-boundary write the trust decision was about. AllowsSilentWrites already excludes them
        // and EffectFor inherits that exclusion rather than restating it.
        var root = MakeTempDir();
        var policy = TrustedAuto(root, EmptyRules());

        Assert.Equal(ReviewEffect.None,
            policy.EffectFor(FileWrite(Path.Combine(root, ".git", "config"))));
    }

    // ---- MayAnnotate ---------------------------------------------------------------------------------

    [Fact]
    public void AnHttpRequest_MayOnlyAnnotate()
    {
        // AN ALLOW MUST NOT SILENCE EGRESS. http_request exists to send data off the machine and there
        // is no in-boundary version of it to carve out, so a verdict shapes the PROMPT and never the
        // outcome.
        var root = MakeTempDir();
        var policy = TrustedAuto(root, EmptyRules());

        Assert.Equal(ReviewEffect.MayAnnotate,
            policy.EffectFor(new PermissionRequest(PermissionKind.Http, "POST https://x.dev/a", "https://x.dev")));
    }

    [Fact]
    public void AnMcpCall_MayOnlyAnnotate()
    {
        var root = MakeTempDir();
        var policy = TrustedAuto(root, EmptyRules());

        Assert.Equal(ReviewEffect.MayAnnotate,
            policy.EffectFor(new PermissionRequest(PermissionKind.Mcp, "mcp:files_read", "mcp:files_read")));
    }

    [Fact]
    public void AnInjectedTool_MayOnlyAnnotate()
    {
        var root = MakeTempDir();
        var policy = TrustedAuto(root, EmptyRules());

        Assert.Equal(ReviewEffect.MayAnnotate,
            policy.EffectFor(new PermissionRequest(PermissionKind.Tool, "tool show_diff", "tool show_diff")));
    }

    // ---- Shell ----------------------------------------------------------------------------------------

    [Fact]
    public void ShellOutsideTheConfinement_IsNotReviewed()
    {
        // REWRITTEN BY TASK 13, WHICH THIS ASSERTION EXISTED TO FORCE. Shell is now MayApprove — but
        // only inside the confinement, and `rm -rf /` is outside it: `/` is a real path outside the
        // boundary, so the answer here is unchanged even though the arm above it is new.
        //
        // THE CLAUSES THEMSELVES LIVE IN ShellApprovalTests, one test per clause. This row stays
        // because the effect TABLE is what this file pins, and "a shell command can still be
        // unreviewable" is a row of it.
        var root = MakeTempDir();
        var policy = TrustedAuto(root, EmptyRules());

        Assert.Equal(ReviewEffect.None,
            policy.EffectFor(new PermissionRequest(PermissionKind.Shell, "rm -rf /", "rm -rf *", Subject: "rm -rf /")));
    }

    [Fact]
    public void ShellInsideTheConfinement_MayBeApproved()
    {
        // THE OTHER ROW OF THE SAME TABLE, and the one that is new. Without it this file would record
        // shell as unreviewable, which stopped being true.
        var root = MakeTempDir();
        var policy = TrustedAuto(root, EmptyRules());

        Assert.Equal(ReviewEffect.MayApprove,
            policy.EffectFor(new PermissionRequest(PermissionKind.Shell, "dotnet build 2>&1 | tail",
                null, Subject: "dotnet build 2>&1 | tail")));
    }

    // ---- The hazard the checklist names ----------------------------------------------------------------

    [Fact]
    public void AKindWithNoArm_FallsTowardNone()
    {
        // RuleSubject's `_ => null` arm silently broke "Always" for PermissionKind.Tool — no CS8509,
        // no failing test, just a button that stopped working. This switch has the same shape and a
        // worse failure mode: an unhandled kind falling toward MayApprove would hand the classifier
        // silencing power over a kind nobody had thought about. A cast integer stands in for that
        // future kind, because there is no other way to express "a value this switch has never seen".
        var root = MakeTempDir();
        var policy = TrustedAuto(root, EmptyRules());

        Assert.Equal(ReviewEffect.None,
            policy.EffectFor(new PermissionRequest((PermissionKind)999, "whatever", "whatever")));
    }

    // ---- The gate honours the effect ---------------------------------------------------------------
    //
    // EffectFor BEING RIGHT IS ONLY HALF THE PROPERTY. The bug this design exists to prevent lives at
    // the CALL SITE — a verdict reaching a `return true` it was not entitled to — so these drive the
    // real gate and assert on what it returned, not on what the predicate said.

    [Fact]
    public async Task AnAllowSilencesAFileWrite()
    {
        var root = MakeTempDir();
        var rules = EmptyRules();
        var gate = AutoGate(root, rules, "ALLOW: ordinary source edit", out var script);

        Assert.True((await gate.RequestAsync(FileWrite(Path.Combine(root, "a.cs")), CancellationToken.None)).Allowed);
        Assert.Equal(0, script.ShownCount);
    }

    [Fact]
    public async Task AnAllowDoesNotSilenceEgress()
    {
        // THE BUG THE ENUM EXISTS TO PREVENT, pinned at the gate. A bool guard admitting http for
        // review would let this ALLOW return true and send data off the machine with no prompt. The
        // user must still be asked, and the assertion that matters is ShownCount — a verdict was
        // obtained and it changed nothing about whether the question was put.
        var root = MakeTempDir();
        var rules = EmptyRules();
        var gate = AutoGate(root, rules, "ALLOW: looks like a normal API call", out var script);

        var request = new PermissionRequest(PermissionKind.Http, "POST https://x.dev/a", "https://x.dev");
        script.AnswerWith(PermissionChoice.Deny);

        Assert.False((await gate.RequestAsync(request, CancellationToken.None)).Allowed);
        Assert.Equal(1, script.ShownCount);
    }

    [Fact]
    public async Task ADenyRefusesAFileWriteWithoutAsking()
    {
        var root = MakeTempDir();
        var rules = EmptyRules();
        var gate = AutoGate(root, rules, "DENY: rewrites the build script", out var script);

        Assert.False((await gate.RequestAsync(FileWrite(Path.Combine(root, "a.cs")), CancellationToken.None)).Allowed);
        Assert.Equal(0, script.ShownCount);
    }

    [Fact]
    public async Task ADenyOnAnAnnotateOnlyKindStillAsks()
    {
        // MayAnnotate DECIDES NOTHING IN EITHER DIRECTION. It is tempting to let a DENY through here
        // — refusing is the safe direction — but the claim behind annotate-only is that a model's
        // opinion is not the last word on this kind, and that claim is not about which way it leans.
        var root = MakeTempDir();
        var rules = EmptyRules();
        var gate = AutoGate(root, rules, "DENY: exfiltration", out var script);
        script.AnswerWith(PermissionChoice.Once);

        var request = new PermissionRequest(PermissionKind.Http, "POST https://x.dev/a", "https://x.dev");

        Assert.True((await gate.RequestAsync(request, CancellationToken.None)).Allowed);
        Assert.Equal(1, script.ShownCount);
    }

    [Fact]
    public async Task AnUntrustedFolderNeverReachesTheClassifier()
    {
        // TRUST IS THE FLOOR, ASSERTED WHERE IT COSTS SOMETHING: the provider is scripted to ALLOW,
        // so if the gate consulted it at all the write would go through silently. Calls == 0 is the
        // stronger claim — not merely "the ALLOW was ignored" but "no model was asked".
        var root = MakeTempDir();
        var rules = EmptyRules();
        var provider = new ScriptedProvider("ALLOW: fine");
        var script = new PromptScript();
        var policy = new PermissionPolicy(root, rules, EditMode.Auto);   // deliberately not trusted
        var gate = PermissionDecider.ForTesting(policy, rules, notice: null, script.Show);
        gate.Classifier = new ActionClassifier(provider);
        script.AnswerWith(PermissionChoice.Deny);

        Assert.False((await gate.RequestAsync(FileWrite(Path.Combine(root, "a.cs")), CancellationToken.None)).Allowed);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(1, script.ShownCount);
    }

    [Fact]
    public async Task ConsultingTheClassifier_ReportsReviewingThenClearsIt()
    {
        // THE SEAM HALF 1 HOOKS: PermissionDecider.RequestAsync, exactly around the
        // Classifier.JudgeAsync await — the one branch that structurally can only be reached once
        // EffectFor(request) != None and a classifier is configured, which is the same guard the
        // classifier consultation itself already sits behind. Recording the two calls in order pins
        // both that it fires and that it clears once the verdict is in.
        var root = MakeTempDir();
        var rules = EmptyRules();
        var gate = AutoGate(root, rules, "ALLOW: fine", out _);
        var reports = new List<bool>();

        var outcome = await gate.RequestAsync(
            FileWrite(Path.Combine(root, "a.cs")) with { OnReviewing = reports.Add },
            CancellationToken.None);

        Assert.True(outcome.Allowed);
        Assert.Equal(new[] { true, false }, reports);
    }

    [Fact]
    public async Task AnUntrustedFolder_NeverReportsReviewing()
    {
        // THE CONTROL a "always report reviewing" non-fix would fail: on the trust floor the gate
        // never reaches the classifier at all (see AnUntrustedFolderNeverReachesTheClassifier
        // above), so OnReviewing must never fire either — a stored rule or a silent in-boundary
        // pass must not flash "reviewing…" on a row that never asked a model anything.
        var root = MakeTempDir();
        var rules = EmptyRules();
        var provider = new ScriptedProvider("ALLOW: fine");
        var script = new PromptScript();
        var policy = new PermissionPolicy(root, rules, EditMode.Auto);   // deliberately not trusted
        var gate = PermissionDecider.ForTesting(policy, rules, notice: null, script.Show);
        gate.Classifier = new ActionClassifier(provider);
        script.AnswerWith(PermissionChoice.Deny);
        var reports = new List<bool>();

        await gate.RequestAsync(
            FileWrite(Path.Combine(root, "a.cs")) with { OnReviewing = reports.Add },
            CancellationToken.None);

        Assert.Empty(reports);
    }

    [Fact]
    public async Task AClassifierRefusal_ReportsReviewingThenClearsIt_BeforeThePromptAppears()
    {
        // THE FALL-THROUGH CASE the brief calls out: the classifier declines to clear the action
        // (ASK) and a user prompt follows. Reviewing must have already ended by the time the prompt
        // shows — the row is then waiting on a HUMAN, which ReportPermissionWait already reports;
        // "reviewing…" lingering alongside it would describe an interval that is over.
        var root = MakeTempDir();
        var rules = EmptyRules();
        var gate = AutoGate(root, rules, "ASK: not sure", out var script);
        script.AnswerWith(PermissionChoice.Once);
        var reports = new List<bool>();

        await gate.RequestAsync(
            FileWrite(Path.Combine(root, "a.cs")) with { OnReviewing = reports.Add },
            CancellationToken.None);

        Assert.Equal(new[] { true, false }, reports);
        Assert.Equal(1, script.ShownCount);
    }

    [Fact]
    public async Task AnAskCarriesTheReasonToThePrompt()
    {
        // RefusedByClassifier KEEPS WORKING, and now carries the model's own words. The heading it
        // drives says "the reviewer refused" rather than blaming a folder the user trusts, and the
        // reason is the part they can actually act on when deciding whether to override it.
        var root = MakeTempDir();
        var rules = EmptyRules();
        var gate = AutoGate(root, rules, "ASK: I cannot tell what this file does", out var script);
        script.AnswerWith(PermissionChoice.Once);

        Assert.True((await gate.RequestAsync(FileWrite(Path.Combine(root, "a.cs")), CancellationToken.None)).Allowed);
        Assert.Equal(1, script.ShownCount);
        Assert.True(script.LastRequest!.RefusedByClassifier);
        Assert.Equal("I cannot tell what this file does", script.LastRequest.ClassifierReason);
    }

    [Fact]
    public async Task AClassifierFailureAsksAndSaysSo()
    {
        // EVERY FAILURE MEANS ASK, and says so once. A transport error is indistinguishable from a
        // strict reviewer unless the gate speaks up, and a user who concludes "auto is just strict"
        // has learned the wrong thing about a broken endpoint.
        var root = MakeTempDir();
        var rules = EmptyRules();
        rules.SetTrust(root, TrustState.Trusted);
        var notices = new List<Message>();
        var script = new PromptScript();
        var policy = new PermissionPolicy(root, rules, EditMode.Auto);
        var gate = PermissionDecider.ForTesting(policy, rules, notices.Add, script.Show);
        gate.Classifier = new ActionClassifier(new ThrowingProvider(new InvalidOperationException("endpoint down")));
        script.AnswerWith(PermissionChoice.Once);

        Assert.True((await gate.RequestAsync(FileWrite(Path.Combine(root, "a.cs")), CancellationToken.None)).Allowed);
        Assert.Equal(1, script.ShownCount);
        Assert.Contains(notices, n => n.Text.Contains("auto review unavailable"));

        // A WARNING, NOT AN ASIDE. The gate fell back to asking, which is it working — but a
        // classifier that is silently unreachable is how auto mode degrades into always-ask without
        // anyone noticing, so the tone has to carry that.
        Assert.All(notices, n => Assert.Equal(Severity.Warning, n.Severity));
    }

    // ---- fakes ---------------------------------------------------------------------------------------

    private static PermissionDecider AutoGate(string root, PermissionRulesStore rules, string reply,
        out PromptScript script)
    {
        rules.SetTrust(root, TrustState.Trusted);
        script = new PromptScript();
        var gate = PermissionDecider.ForTesting(
            new PermissionPolicy(root, rules, EditMode.Auto), rules, notice: null, script.Show);
        gate.Classifier = new ActionClassifier(new ScriptedProvider(reply));
        return gate;
    }

    /// <summary>Answers immediately with whatever the test set, and remembers what it was shown —
    /// the request object matters here because the classifier's reason rides on it.</summary>
    private sealed class PromptScript
    {
        private PermissionChoice _answer = PermissionChoice.Deny;

        public int ShownCount { get; private set; }
        public PermissionRequest? LastRequest { get; private set; }

        public void AnswerWith(PermissionChoice choice) => _answer = choice;

        public Task<PermissionChoice> Show(PermissionRequest request, bool offerTrust, CancellationToken ct)
        {
            ShownCount++;
            LastRequest = request;
            return Task.FromResult(_answer);
        }
    }

    private sealed class ScriptedProvider(string? reply) : ILlmProvider
    {
        public int Calls { get; private set; }

        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public string ModelId => "test-model";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LlmResponse { Text = reply });
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ThrowingProvider(Exception ex) : ILlmProvider
    {
        public string ProviderId => "throwing";
        public string DisplayName => "Throwing";
        public string ModelId => "test-model";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct) => Task.FromException<LlmResponse>(ex);

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

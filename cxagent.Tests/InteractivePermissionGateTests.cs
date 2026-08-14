using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Task 4: the interactive gate. Drives <see cref="InteractivePermissionGate"/> through its
/// internal fake prompt-hook seam (an internal <c>Func&lt;PermissionRequest, bool,
/// Task&lt;PermissionChoice&gt;&gt;</c>, the same trick <see cref="IChatSink"/> plays for
/// AgentHost) so the marshalling/serialisation/persistence logic is testable without a live
/// window — the UI path (PermissionPromptControl, MainWindow.ShowPermissionPrompt/RestoreComposer)
/// is exercised for real only by AppBootstrap at runtime.
/// </summary>
public class InteractivePermissionGateTests
{
    private static PermissionRequest Shell(string command) =>
        new(PermissionKind.Shell, command, command);

    private static PermissionRequest FileWrite(string path) =>
        new(PermissionKind.FileWrite, path, path);

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-ipg-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Records every prompt shown and lets the test resolve it on demand — the "script"
    /// the brief's tests drive. ShowCount/Answer are the seam between the test thread and
    /// whichever thread RequestAsync happens to run on.
    ///
    /// <para>Mirrors the real UI prompt hook's shape exactly (fix round 1): a cancellation must
    /// resolve the SAME completion source the "control" awaits, not a side channel, and the
    /// "restore" step must be observable — RestoredCount — so a test can tell the difference
    /// between "RequestAsync returned" and "the control was actually torn down." The gate that
    /// only did the former (an earlier version) soft-locked the app; this seam is built so that
    /// class of bug is visible to a test, not just to a live run.</para></summary>
    private sealed class PromptScript
    {
        private readonly object _lock = new();
        private TaskCompletionSource<PermissionChoice>? _current;

        public int ShownCount { get; private set; }
        public int RestoredCount { get; private set; }
        public bool? LastOfferTrust { get; private set; }

        public async Task<PermissionChoice> Show(PermissionRequest request, bool offerTrust, CancellationToken ct)
        {
            TaskCompletionSource<PermissionChoice> tcs;
            lock (_lock)
            {
                ShownCount++;
                LastOfferTrust = offerTrust;
                tcs = _current = new TaskCompletionSource<PermissionChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            // Same shape as InteractivePermissionGate's real prompt hook (AwaitAndRestore):
            // cancellation resolves the CONTROL's own completion, and "restore" only counts once
            // that completion has actually resolved — not merely once RequestAsync has decided
            // what to return.
            using var reg = ct.Register(() => tcs.TrySetResult(PermissionChoice.Deny));
            try
            {
                return await tcs.Task;
            }
            finally
            {
                lock (_lock) { RestoredCount++; }
            }
        }

        public void Answer(PermissionChoice choice)
        {
            TaskCompletionSource<PermissionChoice>? tcs;
            lock (_lock) { tcs = _current; }
            tcs!.TrySetResult(choice);
        }
    }

    private static InteractivePermissionGate GateWithScriptedPrompt(
        out PromptScript script, PermissionRulesStore? store = null, string? workingDir = null)
    {
        script = new PromptScript();
        var dir = workingDir ?? MakeTempDir();
        var rules = store ?? new PermissionRulesStore(new AppPaths(MakeTempDir()));
        var policy = new PermissionPolicy(dir, rules);
        return InteractivePermissionGate.ForTesting(policy, rules, dir, transcript: null, script.Show);
    }

    [Fact]
    public async Task TwoConcurrentRequests_AreAnsweredOneAtATime()
    {
        var gate = GateWithScriptedPrompt(out var script);
        var first = gate.RequestAsync(Shell("cmd-1"), CancellationToken.None);
        var second = gate.RequestAsync(Shell("cmd-2"), CancellationToken.None);

        // Poll briefly for the first prompt to land — RequestAsync's continuation onto the
        // (fake, synchronous-enough) prompt hook is asynchronous by construction.
        for (var i = 0; i < 100 && script.ShownCount == 0; i++) await Task.Delay(5);

        Assert.Equal(1, script.ShownCount);        // second is queued, not overlaid
        script.Answer(PermissionChoice.Once);
        Assert.True(await first);

        for (var i = 0; i < 100 && script.ShownCount == 1; i++) await Task.Delay(5);
        script.Answer(PermissionChoice.Deny);
        Assert.False(await second);
        Assert.Equal(2, script.ShownCount);
    }

    [Fact]
    public async Task CancellationWhileWaiting_IsADeny_NotAHang()
    {
        using var cts = new CancellationTokenSource();
        var gate = GateWithScriptedPrompt(out var script);
        var pending = gate.RequestAsync(Shell("slow"), cts.Token);
        cts.Cancel();
        Assert.False(await pending);               // resolves; the goal is not wedged forever
    }

    [Fact]
    public async Task ACancelledRequest_RestoresTheComposer_NotJustTheReturnValue()
    {
        // The soft-lock (fix round 1): RequestAsync resolving Deny is NOT enough. If the prompt
        // control is left awaiting a click that will never come, its own teardown ("restore")
        // never runs, the composer stays swapped out, MainWindow._activePrompt stays set, and
        // every later ShowPermissionPrompt call silently no-ops — no goal can ever be submitted
        // again. This pins the ACTUAL teardown, not just the return value.
        using var cts = new CancellationTokenSource();
        var gate = GateWithScriptedPrompt(out var script);

        var pending = gate.RequestAsync(Shell("sleep 999"), cts.Token);
        for (var i = 0; i < 100 && script.ShownCount == 0; i++) await Task.Delay(5);
        Assert.Equal(1, script.ShownCount);

        cts.Cancel();

        Assert.False(await pending);               // resolves Deny...
        Assert.Equal(1, script.RestoredCount);      // ...AND the prompt was actually torn down
    }

    [Fact]
    public async Task ALateClick_AfterCancellation_IsASilentNoOp_NotADoubleRestoreOrThrow()
    {
        // A user click arriving AFTER cancellation has already resolved the control must not
        // double-restore or throw — PermissionPromptControl.TryCancel/the button's TrySetResult
        // both use TrySetResult, so whichever resolves first wins and the loser is silently
        // ignored, same as a double-click (PermissionPromptControl.Completion's own doc comment).
        using var cts = new CancellationTokenSource();
        var gate = GateWithScriptedPrompt(out var script);

        var pending = gate.RequestAsync(Shell("sleep 999"), cts.Token);
        for (var i = 0; i < 100 && script.ShownCount == 0; i++) await Task.Delay(5);

        cts.Cancel();
        Assert.False(await pending);
        Assert.Equal(1, script.RestoredCount);

        // The "late click": Answer resolves the SAME underlying TCS the cancellation already
        // resolved. TrySetResult must no-op rather than throw, and RestoredCount must not move
        // again (Show's `finally` already ran exactly once).
        var ex = Record.Exception(() => script.Answer(PermissionChoice.Once));
        Assert.Null(ex);
        Assert.Equal(1, script.RestoredCount);
    }

    [Fact]
    public async Task Always_PersistsTheRule_SoTheSecondAskIsSilent()
    {
        // THE feature: "Always" must mean never asked again — across gates, i.e. across restarts.
        // Both gates must share the SAME workingDir (a rule is scoped to the granting cwd,
        // PermissionRulesStore.Matches) — the "restart" is the store/gate being rebuilt, not the
        // project folder changing.
        var cfgDir = MakeTempDir();
        var cfg = new AppPaths(cfgDir);
        var store = new PermissionRulesStore(cfg);
        var workingDir = MakeTempDir();
        var gate = GateWithScriptedPrompt(out var script, store, workingDir);

        var first = gate.RequestAsync(Shell("git status"), CancellationToken.None);
        for (var i = 0; i < 100 && script.ShownCount == 0; i++) await Task.Delay(5);
        script.Answer(PermissionChoice.Always);
        Assert.True(await first);

        var freshGate = GateWithScriptedPrompt(out var script2, new PermissionRulesStore(cfg), workingDir);
        Assert.True(await freshGate.RequestAsync(Shell("git status"), CancellationToken.None));
        Assert.Equal(0, script2.ShownCount);       // silent — the rule survived the "restart"
    }

    [Fact]
    public async Task AnInBoundaryFileWrite_NeverShowsAPrompt_WhenTheFolderIsTrusted()
    {
        var root = MakeTempDir();
        var rules = new PermissionRulesStore(new AppPaths(MakeTempDir()));
        rules.SetTrust(root, TrustState.Trusted);
        var gate = GateWithScriptedPrompt(out var script, rules, root);

        Assert.True(await gate.RequestAsync(FileWrite(Path.Combine(root, "a.txt")), CancellationToken.None));
        Assert.Equal(0, script.ShownCount);
    }

    [Fact]
    public async Task AnInBoundaryFileWrite_InAnUntrustedScope_Prompts_AndTrustFolderAllowsItAndPersists()
    {
        var root = MakeTempDir();
        var cfgDir = MakeTempDir();
        var store = new PermissionRulesStore(new AppPaths(cfgDir));
        // Unknown (never asked) is untrusted for the silent class — no arrange needed beyond that.
        var gate = GateWithScriptedPrompt(out var script, store, root);

        var pending = gate.RequestAsync(FileWrite(Path.Combine(root, "a.txt")), CancellationToken.None);
        for (var i = 0; i < 100 && script.ShownCount == 0; i++) await Task.Delay(5);
        Assert.Equal(1, script.ShownCount);
        Assert.True(script.LastOfferTrust);        // in-boundary + untrusted → offer the button

        script.Answer(PermissionChoice.TrustFolder);
        Assert.True(await pending);
        Assert.Equal(TrustState.Trusted, store.GetTrust(root));
    }

    [Fact]
    public async Task Always_OnAFileRequest_PersistsADirectoryRule_SoASiblingIsSilent()
    {
        // C1, end to end: every prior Always test here used Shell(...), so the file-grant
        // producer (PermissionPolicy.RequestsFor("file", ...)) was never driven through the
        // gate at all — this is the coverage gap that let a per-FILE rule ship instead of the
        // spec's per-DIRECTORY one. Drive the gate with a real FileRequests-produced request,
        // grant Always, then assert the STORED rule is the directory, not the file, and that an
        // untouched sibling in the same directory goes silent under a fresh gate/store pair.
        var cfgDir = MakeTempDir();
        var cfg = new AppPaths(cfgDir);
        var store = new PermissionRulesStore(cfg);
        var workingDir = MakeTempDir();
        var gate = GateWithScriptedPrompt(out var script, store, workingDir);

        var a = Path.Combine(workingDir, "a.txt");
        var req = PermissionPolicy.RequestsFor("file",
                new JobParameters(new Dictionary<string, object?> { ["action"] = "write", ["path"] = a }))
            .Single();

        var pending = gate.RequestAsync(req, CancellationToken.None);
        for (var i = 0; i < 100 && script.ShownCount == 0; i++) await Task.Delay(5);
        script.Answer(PermissionChoice.Always);
        Assert.True(await pending);

        var expectedDir = Path.TrimEndingDirectorySeparator(workingDir) + Path.DirectorySeparatorChar;
        Assert.True(store.Matches(workingDir, PermissionKind.FileWrite, expectedDir));

        // The affordance itself: a fresh gate over the SAME store silently allows an untouched
        // sibling file — never mentioned in the original grant.
        var freshGate = GateWithScriptedPrompt(out var script2, new PermissionRulesStore(cfg), workingDir);
        var b = Path.Combine(workingDir, "b.txt");
        var siblingReq = PermissionPolicy.RequestsFor("file",
                new JobParameters(new Dictionary<string, object?> { ["action"] = "write", ["path"] = b }))
            .Single();
        Assert.True(await freshGate.RequestAsync(siblingReq, CancellationToken.None));
        Assert.Equal(0, script2.ShownCount);
    }

    [Fact]
    public async Task SilentlyAllowedRequests_NeverPromptAtAll()
    {
        // policy.IsSilentlyAllowed short-circuits before the prompt hook is ever consulted — a
        // stored Always rule is exactly this path, driven through the gate rather than the
        // policy directly.
        var cfgDir = MakeTempDir();
        var store = new PermissionRulesStore(new AppPaths(cfgDir));
        store.Add(MakeTempDir(), PermissionKind.Shell, "harmless"); // different scope: must NOT match
        var dir = MakeTempDir();
        store.Add(dir, PermissionKind.Shell, "git status");
        var gate = GateWithScriptedPrompt(out var script, store, dir);

        Assert.True(await gate.RequestAsync(Shell("git status"), CancellationToken.None));
        Assert.Equal(0, script.ShownCount);
    }
}

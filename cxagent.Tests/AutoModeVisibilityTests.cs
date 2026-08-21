using CxAgent.Core.Sessions;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What auto mode did, made visible.
///
/// <para>THE CLASSIFIER WAS INVISIBLE. Its approval was recorded as "silent" — the same value a
/// boundary pass produces — so in one drive's 317 silent decisions there was no way to tell which
/// had cost a model call and which had passed on trust alone. And its REFUSAL was recorded as
/// nothing at all, though it is the one a user sees: it is why a prompt appeared.</para>
///
/// <para>WORSE, THE PROMPT BLAMED THE FOLDER. Its heading is inferred from whether the path is
/// in-boundary, which has one cause in the other modes — an untrusted folder — so it said
/// "in this (untrusted) folder" for every in-tree write. In auto mode the folder is usually TRUSTED,
/// because that is a precondition for the classifier running at all, so the line meant to explain
/// why someone is being asked told them the opposite of what happened.</para>
/// </summary>
public class AutoModeVisibilityTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "auto-" + Guid.NewGuid().ToString("N"));

    public AutoModeVisibilityTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private PermissionDecider AutoGate(string verdict, out List<(PermissionKind Kind, string Decision)> log)
    {
        var rules = new PermissionRulesStore(new AppPaths(_dir));
        rules.SetTrust(_dir, TrustState.Trusted);

        var policy = new PermissionPolicy(_dir, rules, EditMode.Auto);
        var decisions = new List<(PermissionKind, string)>();
        log = decisions;

        var gate = PermissionDecider.ForTesting(policy, rules, notice: null,
            (_, _, _) => Task.FromResult(PermissionChoice.Deny));

        // A provider that answers the classifier's question with the verdict this test wants.
        var judge = new CxAgent.Core.Llm.MockLlmProvider();
        judge.EnqueueResponse(new CxAgent.Core.Llm.LlmResponse { Text = verdict });
        gate.Classifier = new ActionClassifier(judge);
        gate.OnDecision = report => decisions.Add((report.Kind, report.Decision));
        return gate;
    }

    // AN APPROVAL IS ITS OWN DECISION, not "silent". Both let the action through, and recording them
    // the same is what made the classifier's cost invisible.
    [Fact]
    public async Task AnApprovalIsRecordedAsAutoAllowed()
    {
        var gate = AutoGate("ALLOW", out var log);

        Assert.True(await gate.RequestAsync(Request(), CancellationToken.None));
        Assert.Contains(log, d => d.Decision == "auto-allowed");
    }

    // AND SO IS A REFUSAL, which is the one a user sees — it is why a prompt appeared, and nothing
    // recorded it.
    [Fact]
    public async Task ARefusalIsRecordedAsAutoRefused()
    {
        var gate = AutoGate("ASK", out var log);

        await gate.RequestAsync(Request(), CancellationToken.None);

        Assert.Contains(log, d => d.Decision == "auto-refused");
    }

    /// <summary>A request judged by a trusted folder's own auto policy — what reaches the classifier.</summary>
    private PermissionRequest Request()
    {
        var rules = new PermissionRulesStore(new AppPaths(_dir));
        rules.SetTrust(_dir, TrustState.Trusted);

        return new PermissionRequest(PermissionKind.FileWrite, Path.Combine(_dir, "notes.txt"),
            AlwaysRule: null)
        {
            Policy = new PermissionPolicy(_dir, rules, EditMode.Auto),
        };
    }
}

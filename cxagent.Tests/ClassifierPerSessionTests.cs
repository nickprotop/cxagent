using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Which model judges a session's requests in auto mode.
///
/// <para>ONE GATE SERVES EVERY SESSION, and the classifier is a settable property ON IT. The
/// instance name comes from a session's own <see cref="ResolvedConfig"/>, so two sessions can be
/// configured to review with different models — but only the startup path binds one, and it binds it
/// into the shared gate. A second session inherits whatever the last bind left, and a
/// <c>/sessions new</c> session binds nothing at all.</para>
/// </summary>
public class ClassifierPerSessionTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-classifier-" + Guid.NewGuid().ToString("N"));

    public ClassifierPerSessionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ProviderRegistry Registry(params string[] names) =>
        ProviderRegistry.FromProviders(
            names.ToDictionary(n => n, n => (ILlmProvider)new MockLlmProvider(n)),
            defaultName: names.FirstOrDefault());

    private PermissionPolicy Policy(string sessionId) =>
        new(_dir, new PermissionRulesStore(new AppPaths(_dir))) { SessionId = sessionId };

    /// <summary>
    /// TWO SESSIONS HOLD TWO CLASSIFIERS, and neither can disturb the other.
    ///
    /// <para>The defect this replaces: `BindClassifier` sets one slot on the gate, and the gate
    /// serves every session — so the second session's bind replaced the first's, and every request
    /// in either was then judged by whichever model bound last.</para>
    /// </summary>
    [Fact]
    public void EachSessionsPolicyCarriesItsOwnClassifier()
    {
        var providers = Registry("alpha-judge", "beta-judge");

        var alpha = Policy("session-alpha");
        alpha.Classifier = PermissionDecider.ClassifierFor("alpha-judge", providers);

        var beta = Policy("session-beta");
        beta.Classifier = PermissionDecider.ClassifierFor("beta-judge", providers);

        Assert.NotNull(alpha.Classifier);
        Assert.NotNull(beta.Classifier);
        Assert.NotSame(alpha.Classifier, beta.Classifier);
    }

    /// <summary>
    /// AND A SESSION WITH NO CLASSIFIER LEAVES THE OTHERS REVIEWING.
    ///
    /// <para>This was the dangerous direction: binding null cleared the one slot, so opening a
    /// second session configured without a classifier turned auto-review OFF for the first — an
    /// action that should have been reviewed silently allowed instead, with nothing said.</para>
    /// </summary>
    [Fact]
    public void ASessionWithoutAClassifierDoesNotDisarmAnother()
    {
        var providers = Registry("alpha-judge");

        var reviewing = Policy("session-alpha");
        reviewing.Classifier = PermissionDecider.ClassifierFor("alpha-judge", providers);

        var unreviewed = Policy("session-beta");
        unreviewed.Classifier = PermissionDecider.ClassifierFor(null, providers);

        Assert.Null(unreviewed.Classifier);
        Assert.NotNull(reviewing.Classifier);   // untouched by the second session
    }

    /// <summary>
    /// THE GATE'S OWN SLOT REMAINS AS A FALLBACK, for an embedder that owns its whole process and
    /// binds one classifier for it — and for a request that carries no policy, which has no other
    /// source.
    /// </summary>
    [Fact]
    public void TheGatesClassifierIsStillThereForARequestWithNoPolicy()
    {
        var rules = new PermissionRulesStore(new AppPaths(_dir));
        var gate = PermissionDecider.WithPrompt(rules, null,
            (_, _, _) => Task.FromResult(PermissionChoice.Deny));

        gate.BindClassifier("alpha-judge", Registry("alpha-judge"));

        Assert.NotNull(gate.Classifier);
    }
}

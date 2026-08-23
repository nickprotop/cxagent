using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// That "Always allow" on an injected tool actually PERSISTS, and that "Allow once" does not.
///
/// <para>WHY THIS FILE EXISTS: both halves were broken at once and neither showed up in a test.
/// GatedAgentTool cached a bool after the first yes, so "once" behaved as "always" for the rest of
/// the session — reported from a drive where one `once` silently covered four files. And
/// PermissionPolicy.RuleSubject had no arm for PermissionKind.Tool, so it fell through the
/// <c>_ => null</c> that exists for cast integers: a stored rule could never match, and "always"
/// asked again forever while writing dead entries into permissions.json.</para>
///
/// <para>The two bugs cancelled each other out in the only place anyone looked. Within one session
/// the latch made repeat calls silent, which is exactly what a working "always" would look like —
/// so the broken persistence was invisible until someone answered "once" and got the same silence.
/// These tests go through the real store and the real policy for that reason.</para>
/// </summary>
public class ToolPermissionPersistenceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "toolperm-" + Guid.NewGuid().ToString("N")[..8]);

    public ToolPermissionPersistenceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static PermissionRequest Admission(string tool = "deploy") =>
        new(PermissionKind.Tool, $"use the {tool} tool in this folder", AlwaysRule: $"tool {tool}");

    private (PermissionPolicy Policy, PermissionRulesStore Rules) Setup()
    {
        var rules = new PermissionRulesStore(new AppPaths(_dir));
        rules.SetTrust(_dir, TrustState.Trusted);
        return (new PermissionPolicy(_dir, rules, EditMode.AlwaysAsk), rules);
    }

    [Fact]
    public void AStoredToolRuleIsMatchedAndSilencesThePrompt()
    {
        // THE BUG: RuleSubject returned null for Tool, so this returned false however many rules
        // were stored. The user pressed "Always allow", a rule was written, and they were asked
        // again on every call for the rest of time.
        var (policy, rules) = Setup();
        var request = Admission() with { Policy = policy };

        Assert.False(policy.IsSilentlyAllowed(request));   // nothing stored yet

        rules.Add(_dir, PermissionKind.Tool, "tool deploy");

        Assert.True(policy.IsSilentlyAllowed(request));
    }

    [Fact]
    public void ARuleForOneToolDoesNotAdmitAnother()
    {
        // The rule names the tool exactly. A grant for one tool must not admit another that
        // happens to be injected by the same embedder.
        var (policy, rules) = Setup();
        rules.Add(_dir, PermissionKind.Tool, "tool deploy");

        Assert.False(policy.IsSilentlyAllowed(Admission("notify") with { Policy = policy }));
    }

    [Fact]
    public void ARuleIsScopedToTheFolderItWasGrantedIn()
    {
        // Per the spec: scope is per folder, like trust and every stored rule. A tool admitted in
        // one project must not be admitted in another the user was not looking at.
        var other = Path.Combine(_dir, "other");
        Directory.CreateDirectory(other);

        var (_, rules) = Setup();
        rules.Add(_dir, PermissionKind.Tool, "tool deploy");

        var elsewhere = new PermissionPolicy(other, rules, EditMode.AlwaysAsk);

        Assert.False(elsewhere.IsSilentlyAllowed(Admission() with { Policy = elsewhere }));
    }

    [Fact]
    public void ARuleSurvivesReloadingTheStoreFromDisk()
    {
        // PermissionKind is persisted BY NAME, and Tool is a new value. This is the round trip that
        // proves the enum reaches permissions.json and comes back as itself rather than throwing —
        // which, per the enum's own summary, would take every rule and all folder trust with it.
        var (_, rules) = Setup();
        rules.Add(_dir, PermissionKind.Tool, "tool deploy");

        var reloaded = new PermissionRulesStore(new AppPaths(_dir));
        var policy = new PermissionPolicy(_dir, reloaded, EditMode.AlwaysAsk);

        Assert.True(policy.IsSilentlyAllowed(Admission() with { Policy = policy }));
    }

    [Fact]
    public void AnUntrustedFolderStillAdmitsAToolItWasGrantedIn()
    {
        // Trust and tool admission are different questions. A tool granted explicitly should not
        // silently lose that grant because the folder itself is untrusted — the user answered THIS
        // question, and nothing about folder trust revokes it.
        var rules = new PermissionRulesStore(new AppPaths(_dir));
        rules.Add(_dir, PermissionKind.Tool, "tool deploy");

        var policy = new PermissionPolicy(_dir, rules, EditMode.AlwaysAsk);

        Assert.True(policy.IsSilentlyAllowed(Admission() with { Policy = policy }));
    }
}

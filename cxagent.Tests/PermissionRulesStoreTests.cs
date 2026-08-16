using CxAgent.Core.Agent;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

public class PermissionRulesStoreTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-perm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Rules_RoundTripThroughPermissionsJson_ScopedByFolder()
    {
        var cfg = new AppPaths(MakeTempDir());
        var store = new PermissionRulesStore(cfg);
        store.Add("/proj/a", PermissionKind.Shell, "git status");

        var reloaded = new PermissionRulesStore(cfg);
        Assert.True(reloaded.Matches("/proj/a", PermissionKind.Shell, "git status"));
        // A grant made in project A must not follow the user to project B.
        Assert.False(reloaded.Matches("/proj/b", PermissionKind.Shell, "git status"));
    }

    [Fact]
    public void AnUnparseablePermissionsFile_IsAnEmptyRuleSet_NotACrash()
    {
        var cfg = new AppPaths(MakeTempDir());
        Directory.CreateDirectory(cfg.ConfigDir);
        File.WriteAllText(Path.Combine(cfg.ConfigDir, "permissions.json"), "{ not json");
        var store = new PermissionRulesStore(cfg);   // must not throw
        Assert.False(store.Matches("/x", PermissionKind.Shell, "ls"));
    }

    // --- I3: a bad hand-edit must be observable, and the user's file must survive one save. ---

    [Fact]
    public void ATypodEnumValue_SilentlyDropsEverything_ButIsObservableViaLoadError()
    {
        // Exactly the mistake string enums invite: "Shell" -> "Shel". JsonException, caught by
        // Load same as any other unparseable file — but this file WAS present and WAS "valid JSON
        // shape", so a bad load here is exactly the load-bearing case: the only revocation surface
        // in v1 (hand-editing) failing in a way the user cannot see without LoadError.
        var cfg = new AppPaths(MakeTempDir());
        Directory.CreateDirectory(cfg.ConfigDir);
        var badJson = """
            { "Rules": [ { "Scope": "/proj/a", "Kind": "Shel", "Pattern": "git status" } ],
              "Trust": { "/proj/a": "Trusted" } }
            """;
        File.WriteAllText(Path.Combine(cfg.ConfigDir, "permissions.json"), badJson);

        var store = new PermissionRulesStore(cfg);   // must not throw

        // Every rule and all trust are gone in memory (the pre-existing "empty, not a crash"
        // behaviour) ...
        Assert.False(store.Matches("/proj/a", PermissionKind.Shell, "git status"));
        Assert.Equal(TrustState.Unknown, store.GetTrust("/proj/a"));
        // ... but the failure itself is now observable, which is the actual fix.
        Assert.NotNull(store.LoadError);
    }

    [Fact]
    public void FirstSaveAfterAFailedLoad_BacksUpTheOriginalFile_BeforeOverwriting()
    {
        var cfg = new AppPaths(MakeTempDir());
        Directory.CreateDirectory(cfg.ConfigDir);
        var permissionsPath = Path.Combine(cfg.ConfigDir, "permissions.json");
        var badJson = "{ \"Rules\": [ { \"Scope\": \"/proj/a\", \"Kind\": \"Shel\", \"Pattern\": \"x\" } ] }";
        File.WriteAllText(permissionsPath, badJson);

        var store = new PermissionRulesStore(cfg);
        Assert.NotNull(store.LoadError);   // sanity: this is the failed-load case

        store.Add("/proj/b", PermissionKind.Shell, "npm test");   // the first save since construction

        var badPath = Path.Combine(cfg.ConfigDir, "permissions.json.bad");
        Assert.True(File.Exists(badPath), "the original unreadable file must be preserved as .bad");
        Assert.Equal(badJson, File.ReadAllText(badPath));

        // And the new grant is still there — a failed load must not block using the store going
        // forward.
        var reloaded = new PermissionRulesStore(cfg);
        Assert.True(reloaded.Matches("/proj/b", PermissionKind.Shell, "npm test"));
    }

    [Fact]
    public void ANormalLoad_ValidFile_ReportsNoErrorAndCreatesNoBadFile()
    {
        // The negative case: a healthy file must not be flagged, and a healthy Add must never
        // spuriously create a .bad file next to it.
        var cfg = new AppPaths(MakeTempDir());
        var store = new PermissionRulesStore(cfg);
        Assert.Null(store.LoadError);

        store.Add("/proj/a", PermissionKind.Shell, "git status");
        Assert.Null(store.LoadError);
        Assert.False(File.Exists(Path.Combine(cfg.ConfigDir, "permissions.json.bad")));
    }

    [Fact]
    public void ANormalLoad_AbsentFile_ReportsNoErrorAndCreatesNoBadFile()
    {
        // The negative case for "missing", distinct from "present but broken": a fresh install
        // with no permissions.json yet must not be treated as a load failure.
        var cfg = new AppPaths(MakeTempDir());
        Assert.False(File.Exists(Path.Combine(cfg.ConfigDir, "permissions.json")));

        var store = new PermissionRulesStore(cfg);
        Assert.Null(store.LoadError);

        store.Add("/proj/a", PermissionKind.Shell, "git status");
        Assert.False(File.Exists(Path.Combine(cfg.ConfigDir, "permissions.json.bad")));
    }

    [Fact]
    public void TrustState_RoundTripsThroughPermissionsJson_IncludingAPersistedNo()
    {
        // "Don't trust" is persisted — re-asking every launch nags the user into clicking Trust,
        // the manufactured-consent failure. A reloaded store must know the answer was NO, which is
        // different from never-asked.
        var cfg = new AppPaths(MakeTempDir());
        var store = new PermissionRulesStore(cfg);
        store.SetTrust("/proj/a", TrustState.Trusted);
        store.SetTrust("/proj/b", TrustState.Untrusted);

        var reloaded = new PermissionRulesStore(cfg);
        Assert.Equal(TrustState.Trusted, reloaded.GetTrust("/proj/a"));
        Assert.Equal(TrustState.Untrusted, reloaded.GetTrust("/proj/b"));
        Assert.Equal(TrustState.Unknown, reloaded.GetTrust("/proj/never-asked"));
    }

    [Fact]
    public void PermissionsJson_IsHandEditable_KindsAndTrustAreNamesNotNumbers()
    {
        // Hand-editing is the ONLY way to revoke a rule or revoke trust in v1. A file that
        // demands `"kind": 2` with no legend is one the user cannot safely edit — and a
        // mis-guessed integer silently grants the WRONG permission class rather than failing.
        var cfg = new AppPaths(MakeTempDir());
        var store = new PermissionRulesStore(cfg);
        store.Add("/proj/a", PermissionKind.Shell, "git status");
        store.SetTrust("/proj/a", TrustState.Untrusted);

        var json = File.ReadAllText(Path.Combine(cfg.ConfigDir, "permissions.json"));

        Assert.Contains("Shell", json);
        Assert.Contains("Untrusted", json);
        Assert.DoesNotMatch(@"""kind""\s*:\s*\d", json);   // no bare integer kinds
    }

    [Fact]
    public void ARuleFileWrittenWithStringEnums_ReloadsCorrectly()
    {
        // The deserializer must carry the SAME converter, or writing succeeds and reading
        // silently yields an empty rule set — every stored grant lost, with no error.
        var cfg = new AppPaths(MakeTempDir());
        var store = new PermissionRulesStore(cfg);
        store.Add("/proj/a", PermissionKind.Http, "https://example.com");
        store.SetTrust("/proj/a", TrustState.Trusted);

        var reloaded = new PermissionRulesStore(cfg);
        Assert.True(reloaded.Matches("/proj/a", PermissionKind.Http, "https://example.com"));
        Assert.Equal(TrustState.Trusted, reloaded.GetTrust("/proj/a"));
    }

    // --- I2: two instances over one file must not lose each other's grants. ---

    [Fact]
    public void TwoInstancesOverOneFile_BothGrantsSurvive()
    {
        // The shape of two cxagent windows open in two projects, sharing one permissions.json.
        // B is constructed BEFORE A saves, so B's in-memory state is stale by construction —
        // exactly the finding's scenario. Without a reload-merge-write, B's save would overwrite
        // A's grant with B's stale (rule-less) snapshot plus B's own new rule.
        var cfg = new AppPaths(MakeTempDir());
        var a = new PermissionRulesStore(cfg);
        var b = new PermissionRulesStore(cfg);

        a.Add("/proj/a", PermissionKind.Shell, "git status");
        b.Add("/proj/b", PermissionKind.Shell, "npm test");

        var reloaded = new PermissionRulesStore(cfg);
        Assert.True(reloaded.Matches("/proj/a", PermissionKind.Shell, "git status"));
        Assert.True(reloaded.Matches("/proj/b", PermissionKind.Shell, "npm test"));
    }

    [Fact]
    public void TwoInstancesOverOneFile_TrustSetByOneInstance_SurvivesAnothersLaterSave()
    {
        // A sets trust on /proj/a. B (stale, never touched /proj/a) saves afterwards for an
        // unrelated scope. A's trust entry must not be silently revoked by B's save.
        var cfg = new AppPaths(MakeTempDir());
        var a = new PermissionRulesStore(cfg);
        var b = new PermissionRulesStore(cfg);

        a.SetTrust("/proj/a", TrustState.Trusted);
        b.Add("/proj/b", PermissionKind.Shell, "npm test");

        var reloaded = new PermissionRulesStore(cfg);
        Assert.Equal(TrustState.Trusted, reloaded.GetTrust("/proj/a"));
    }

    [Fact]
    public void ATrustScopeThisInstanceActuallySet_WinsOverWhatIsOnDisk()
    {
        // A trusts /proj/a. B independently, and later, classifies /proj/a as Untrusted itself
        // (a genuine local decision via SetTrust) and saves. B's value must win for /proj/a —
        // this is the case the fix must NOT break: a scope an instance actually classified is
        // never silently overridden by a stale on-disk value from a merge.
        var cfg = new AppPaths(MakeTempDir());
        var a = new PermissionRulesStore(cfg);
        var b = new PermissionRulesStore(cfg);

        a.SetTrust("/proj/a", TrustState.Trusted);
        b.SetTrust("/proj/a", TrustState.Untrusted);

        var reloaded = new PermissionRulesStore(cfg);
        Assert.Equal(TrustState.Untrusted, reloaded.GetTrust("/proj/a"));
    }

    [Fact]
    public void UnparseableFileAtMergeTime_DoesNotThrow_AndDoesNotLoseThisInstancesState()
    {
        // If the on-disk file becomes unparseable between this instance's construction and its
        // next save (e.g. another process wrote a half-broken hand-edit), the merge must degrade
        // to "nothing on disk to merge" — never throw, never lose this instance's own new grant.
        var cfg = new AppPaths(MakeTempDir());
        var store = new PermissionRulesStore(cfg);
        store.Add("/proj/a", PermissionKind.Shell, "git status");

        File.WriteAllText(Path.Combine(cfg.ConfigDir, "permissions.json"), "{ not json");

        store.Add("/proj/a", PermissionKind.Shell, "npm test"); // must not throw

        var reloaded = new PermissionRulesStore(cfg);
        Assert.True(reloaded.Matches("/proj/a", PermissionKind.Shell, "git status"));
        Assert.True(reloaded.Matches("/proj/a", PermissionKind.Shell, "npm test"));
    }

    [Fact]
    public void RulesFor_ReturnsOnlyTheGivenScope_WithAnHonestOtherScopeCount()
    {
        var store = new PermissionRulesStore(new AppPaths(MakeTempDir()));
        store.Add("/proj/a", PermissionKind.Shell, "git status");
        store.Add("/proj/a", PermissionKind.Http, "https://api.github.com");
        store.Add("/proj/b", PermissionKind.Shell, "ls");

        var (rules, others) = store.RulesFor("/proj/a");
        Assert.Equal(2, rules.Count);
        Assert.DoesNotContain(rules, r => r.Pattern == "ls");
        Assert.Equal(1, others);
    }

    // REGRESSION: RulesFor compared the caller's RAW path against stored scopes while Add persists
    // under FolderIdentity.ScopeFor(...). For any folder that gets an identity suffix, the lookup
    // matched nothing — the panel and the Settings page reported zero rules however many were
    // granted. The test above never caught it because "/proj/a" has no identity, so ScopeFor is a
    // no-op on both sides and the raw and normalised keys are identical. This one uses a real
    // directory, which does get a suffix.
    [Fact]
    public void RulesFor_FindsRulesWhenTheScopeHasAnIdentitySuffix()
    {
        var dir = MakeTempDir();
        var store = new PermissionRulesStore(new AppPaths(MakeTempDir()));
        store.Add(dir, PermissionKind.Shell, "git status");

        // Precondition: this really is a scope that normalises to something else, otherwise the
        // test would pass for the same uninteresting reason the one above did.
        Assert.NotEqual(dir, FolderIdentity.ScopeFor(dir));

        var (rules, _) = store.RulesFor(dir);
        Assert.Single(rules);
        Assert.Equal("git status", rules[0].Pattern);
    }

    [Fact]
    public void EditMode_IsNullUntilSet_SoAbsentIsNotASilentDefault()
    {
        var store = new PermissionRulesStore(new AppPaths(MakeTempDir()));
        Assert.Null(store.GetEditMode("/proj/a"));
    }

    [Fact]
    public void EditMode_RoundTripsAcrossInstances_IncludingThePermissiveOnes()
    {
        var config = MakeTempDir();
        new PermissionRulesStore(new AppPaths(config)).SetEditMode("/proj/a", EditMode.Auto);

        // A NEW INSTANCE, i.e. the next launch — the whole point of the feature.
        Assert.Equal(EditMode.Auto, new PermissionRulesStore(new AppPaths(config)).GetEditMode("/proj/a"));
    }

    [Fact]
    public void EditMode_IsPerFolder_SoOneProjectsChoiceNeverGovernsAnother()
    {
        var store = new PermissionRulesStore(new AppPaths(MakeTempDir()));
        store.SetEditMode("/proj/a", EditMode.AlwaysAsk);

        Assert.Equal(EditMode.AlwaysAsk, store.GetEditMode("/proj/a"));
        Assert.Null(store.GetEditMode("/proj/b"));
    }

    // Mirrors the trust merge rule: an instance that never chose a mode for a folder must not
    // clobber another window's newer choice when it saves for some unrelated reason.
    [Fact]
    public void EditMode_FromDiskWins_ForAScopeThisInstanceNeverSet()
    {
        var config = MakeTempDir();
        var first = new PermissionRulesStore(new AppPaths(config));
        var second = new PermissionRulesStore(new AppPaths(config));

        second.SetEditMode("/proj/a", EditMode.AlwaysAsk);   // the newer, on-disk choice
        first.Add("/proj/other", PermissionKind.Shell, "ls"); // triggers first's Save + merge

        Assert.Equal(EditMode.AlwaysAsk,
            new PermissionRulesStore(new AppPaths(config)).GetEditMode("/proj/a"));
    }

    [Fact]
    public void Add_RaisesRulesChanged_SoAViewCanRecount()
    {
        var store = new PermissionRulesStore(new AppPaths(MakeTempDir()));
        var raised = 0;
        store.RulesChanged += () => raised++;

        store.Add("/proj/a", PermissionKind.Shell, "git status");

        Assert.Equal(1, raised);
    }
}

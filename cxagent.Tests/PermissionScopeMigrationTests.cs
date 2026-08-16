using CxAgent.Core.Agent;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

public class PermissionScopeMigrationTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cxmig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A scope string for <paramref name="dir"/> whose suffix sits <paramref name="after"/>
    /// past the folder's real birth time — the shape the ctime bug actually produced, since a change
    /// time only ever moves forward from birth.</summary>
    private static string StaleScopeAfterBirth(string dir, TimeSpan after)
    {
        var current = FolderIdentity.ScopeFor(dir);
        var birth = DateTime.ParseExact(current[(dir.Length + 1)..], "yyyyMMddTHHmmss",
            System.Globalization.CultureInfo.InvariantCulture);
        return $"{dir}#{birth + after:yyyyMMddTHHmmss}";
    }

    // The real shape of the bug: a LIVE folder carrying scopes from the ctime era, none of which
    // equals what FolderIdentity returns for it today. Trust and rules under those scopes are
    // stranded, and the user is asked again for things they granted.
    [Fact]
    public void FoldsStaleScopesOfALiveFolderOntoItsCurrentIdentity()
    {
        var config = MakeTempDir();
        var project = MakeTempDir();
        var store = new PermissionRulesStore(new AppPaths(config));

        // Written the way the ctime era wrote them: same path, a suffix LATER than the real birth
        // time, because the change time only ever moves forward as the folder is used. A suffix
        // EARLIER than birth would describe something else that stood at this path, which is the
        // recreation case covered separately below.
        var stale = StaleScopeAfterBirth(project, TimeSpan.FromDays(3));
        store.AddRaw(stale, PermissionKind.Shell, "dotnet build*");
        store.SetTrustRaw(stale, TrustState.Trusted);

        var result = PermissionScopeMigration.Run(store);

        Assert.True(result.ChangedAnything);
        var current = FolderIdentity.ScopeFor(project);
        Assert.NotEqual(stale, current);

        // The grant answers again, under the identity the app will actually look up.
        Assert.True(store.Matches(project, PermissionKind.Shell, "dotnet build -c Release"));
        Assert.Equal(TrustState.Trusted, store.GetTrust(project));

        // And the stranded scope is gone rather than lingering as a second generation.
        Assert.DoesNotContain(stale, store.AllScopes());
    }

    // THE CASE THAT MUST NOT COLLAPSE. A path whose folder is gone cannot have its identity
    // recomputed, and folding it onto anything would re-grant permissions to a directory that no
    // longer exists — reviving them for whatever gets created at that path next. This is the exact
    // failure FolderIdentity was built to prevent, so the migration has to leave it alone.
    [Fact]
    public void LeavesScopesOfDeletedFoldersUntouched()
    {
        var config = MakeTempDir();
        var gone = Path.Combine(Path.GetTempPath(), $"cxmig-gone-{Guid.NewGuid():N}");
        var store = new PermissionRulesStore(new AppPaths(config));

        var stale = $"{gone}#20260813T100304";
        store.AddRaw(stale, PermissionKind.Shell, "rm*");
        store.SetTrustRaw(stale, TrustState.Trusted);

        var result = PermissionScopeMigration.Run(store);

        Assert.False(result.ChangedAnything);
        Assert.Contains(stale, store.AllScopes());
        Assert.Equal(TrustState.Unknown, store.GetTrust(gone));
    }

    [Fact]
    public void IsIdempotent_SoASecondRunFindsNothing()
    {
        var config = MakeTempDir();
        var project = MakeTempDir();
        var store = new PermissionRulesStore(new AppPaths(config));
        store.AddRaw(StaleScopeAfterBirth(project, TimeSpan.FromDays(3)), PermissionKind.Shell, "ls");

        Assert.True(PermissionScopeMigration.Run(store).ChangedAnything);
        Assert.False(PermissionScopeMigration.Run(store).ChangedAnything);
    }

    // Untrusted must not be laundered into Trusted just because some other generation of the same
    // path was trusted... but nothing here says trusted, so nothing may claim it.
    [Fact]
    public void DoesNotInventTrustWhenNoStaleScopeHadIt()
    {
        var config = MakeTempDir();
        var project = MakeTempDir();
        var store = new PermissionRulesStore(new AppPaths(config));
        store.AddRaw(StaleScopeAfterBirth(project, TimeSpan.FromDays(3)), PermissionKind.Shell, "ls");

        PermissionScopeMigration.Run(store);

        Assert.Equal(TrustState.Unknown, store.GetTrust(project));
    }

    [Fact]
    public void CollapsesTheBarePrePermissionIdentityScopeToo()
    {
        var config = MakeTempDir();
        var project = MakeTempDir();
        var store = new PermissionRulesStore(new AppPaths(config));

        // Pre-identity: the scope WAS the path, with no suffix at all.
        store.AddRaw(project, PermissionKind.Shell, "git status");
        store.SetTrustRaw(project, TrustState.Trusted);

        PermissionScopeMigration.Run(store);

        Assert.True(store.Matches(project, PermissionKind.Shell, "git status"));
        Assert.Equal(TrustState.Trusted, store.GetTrust(project));
    }
}

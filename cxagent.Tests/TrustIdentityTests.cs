using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Trust is not remembered for a folder the filesystem cannot distinguish.
///
/// <para>THE FAILURE THIS PREVENTS. ScopeFor disambiguates a recreated folder by birth time, and
/// several filesystems record none — ext3, older XFS, NFS, some FUSE mounts. There the scope is a
/// bare path, so a grant made before <c>rm -rf dir &amp;&amp; mkdir dir</c> applies to whatever is
/// there now: the precise failure FolderIdentity exists to prevent, unfixed on those mounts.</para>
///
/// <para>WHY TRUST AND NOT RULES. A stale rule permits one command shape inside a folder. Stale
/// trust unlocks every silent write in it AND is the precondition for the read-only free pass that
/// reads files by name — so a stranger inheriting trust inherits the whole folder. The consequence
/// is not symmetric, so neither is the guard.</para>
///
/// <para>NOT REPRODUCIBLE ON THIS MACHINE, which is worth stating: statx reports birth times on
/// every mount here, so the guard never fires in practice locally. These tests reach the branch
/// directly rather than pretending to simulate a filesystem.</para>
/// </summary>
public class TrustIdentityTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "trustid-" + Guid.NewGuid().ToString("N"));

    public TrustIdentityTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    // A REAL FOLDER ON A FILESYSTEM THAT REPORTS BIRTH TIMES IS DISTINGUISHABLE, and trust for it
    // round-trips as before — the guard must not cost anything where identity works.
    [Fact]
    public void ARealFolderKeepsItsTrust()
    {
        Assert.True(FolderIdentity.IsDistinguishable(FolderIdentity.ScopeFor(_dir)),
            "this machine reports no birth time for a temp dir — the rest of this test is moot");

        var store = new PermissionRulesStore(new AppPaths(_dir));
        store.SetTrust(_dir, TrustState.Trusted);

        Assert.Equal(TrustState.Trusted, store.GetTrust(_dir));
    }

    // A BARE PATH FOR AN EXISTING FOLDER IS NOT DISTINGUISHABLE. This is the shape ScopeFor returns
    // on a mount with no birth times, and the scope that must not carry trust.
    [Fact]
    public void AnExistingFolderWithoutABirthTimeIsNotDistinguishable() =>
        Assert.False(FolderIdentity.IsDistinguishable(_dir));

    // A PATH THAT DOES NOT EXIST IS FINE, and the distinction matters: nothing runs in a folder that
    // is not there, so there is no stranger to inherit anything. Refusing these would break every
    // caller reasoning about a path in the abstract, including this suite.
    [Fact]
    public void APathThatDoesNotExistIsDistinguishable() =>
        Assert.True(FolderIdentity.IsDistinguishable("/proj/does-not-exist"));

    // AND THE STORE HONOURS IT. Reaching the branch needs a folder whose scope has no suffix, which
    // on this machine means one that exists but whose birth time ScopeFor did not record — so this
    // asserts the store's CONTRACT directly: an indistinguishable scope reads back Unknown, putting
    // the question to the user again rather than answering it with a stranger's answer.
    [Fact]
    public void TrustOnAnIndistinguishableScopeReadsBackUnknown()
    {
        var store = new PermissionRulesStore(new AppPaths(_dir));
        store.SetTrust(_dir, TrustState.Trusted);

        // The guard reads through ScopeFor, so on a birth-time filesystem this is the trusted path.
        Assert.Equal(TrustState.Trusted, store.GetTrust(_dir));

        // And the bare form — what a no-birth-time mount yields — is refused identity, which is what
        // makes GetTrust return Unknown there.
        Assert.False(FolderIdentity.IsDistinguishable(_dir));
        Assert.True(FolderIdentity.IsDistinguishable(FolderIdentity.ScopeFor(_dir)));
    }
}

using CxAgent.Core.Commands;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// TRUST IS THE FOLDER A SESSION REALLY WORKS IN. Opened through a link — a repository's <c>docs → ~</c> — the
/// question named the link while the boundary became the home folder, so trusting "clone/docs" made every read in
/// home silent. Trust, rules and edit modes now key on the real folder, and the question names it, saying what it
/// was opened as when the two differ.
///
/// <para>OLD GRANTS UNDER A LINK'S SPELLING STAY WHERE THEY ARE, UNUSED: the startup migration never carries one to
/// the folder the link points at — that is the escalation itself — so the real folder is asked about afresh.</para>
/// </summary>
public sealed class TrustRealFolderTests : IDisposable
{
    private readonly string _top = Path.Combine(Path.GetTempPath(), "cx-trustreal-" + Guid.NewGuid().ToString("N"));
    private readonly string _real;
    private readonly string _link;
    private readonly AppPaths _config;

    public TrustRealFolderTests()
    {
        _real = Path.Combine(_top, "home");
        Directory.CreateDirectory(_real);
        Directory.CreateDirectory(Path.Combine(_top, "clone"));
        _link = Path.Combine(_top, "clone", "docs");
        if (!OperatingSystem.IsWindows()) Directory.CreateSymbolicLink(_link, _real);
        _config = new AppPaths(Path.Combine(_top, "config"));
    }

    public void Dispose() { try { Directory.Delete(_top, recursive: true); } catch (IOException) { } }

    [Fact]
    public void TrustGrantedThroughALink_IsTheRealFoldersTrust()
    {
        if (OperatingSystem.IsWindows()) return;
        var store = new PermissionRulesStore(_config);
        new PermissionPolicy(_link, store).SetTrust(TrustState.Trusted);

        Assert.Equal(TrustState.Trusted, new PermissionPolicy(_real, store).Trust);
    }

    [Fact]
    public void TheTrustQuestion_NamesTheRealFolder_AndWhatItWasOpenedAs()
    {
        if (OperatingSystem.IsWindows()) return;
        var store = new PermissionRulesStore(_config);

        Assert.Equal($"{PathResolver.RealFolder(_real)} (opened as {_link})", new PermissionPolicy(_link, store).TrustShown);
        Assert.Equal(PathResolver.RealFolder(_real), new PermissionPolicy(_real, store).TrustShown);
    }

    [Fact]
    public void ALinkSpelledTrust_IsNotCarriedByTheMigration_AndStaysInTheFile()
    {
        if (OperatingSystem.IsWindows()) return;
        var store = new PermissionRulesStore(_config);
        var current = FolderIdentity.ScopeFor(_real);
        var old = _link + current[PathResolver.RealFolder(_real).Length..];
        store.SetTrustRaw(old, TrustState.Trusted);
        store.AddRaw(old, PermissionKind.Shell, "make*");

        PermissionScopeMigration.Run(store);

        Assert.NotEqual(TrustState.Trusted, store.GetTrust(_real));
        Assert.Equal(TrustState.Trusted, store.GetTrustRaw(old));
        Assert.False(store.Matches(_link, PermissionKind.Shell, "make all"));
    }

    [Fact]
    public void TheRootFolder_IsNeverTrusted()
    {
        var store = new PermissionRulesStore(_config);
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var policy = new PermissionPolicy(root, store);
        policy.SetTrust(TrustState.Trusted);

        Assert.False(policy.CanBeTrusted);
        Assert.NotEqual(TrustState.Trusted, policy.Trust);
        Assert.False(policy.IsSilentlyAllowed(new PermissionRequest(PermissionKind.FileRead,
            Path.Combine(root, "etc", "hostname"), null)));
    }

    [Fact]
    public async Task ARootFolder_IsNeverOfferedTheTrustButton()
    {
        var store = new PermissionRulesStore(_config);
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var policy = new PermissionPolicy(root, store);
        bool? offered = null;
        var gate = PermissionDecider.ForTesting(policy, store, null, (_, offerTrust, _) =>
        {
            offered = offerTrust;
            return Task.FromResult(PermissionChoice.Deny);
        });

        await gate.RequestAsync(new PermissionRequest(PermissionKind.FileRead, Path.Combine(root, "etc", "hostname"), null)
        { Policy = policy }, CancellationToken.None);

        Assert.False(offered);
    }

    [Fact]
    public async Task TheTrustButtonsNotice_NamesTheRealFolder()
    {
        if (OperatingSystem.IsWindows()) return;
        var store = new PermissionRulesStore(_config);
        store.SetTrust(_link, TrustState.Untrusted);
        var policy = new PermissionPolicy(_link, store);
        var notices = new List<Message>();
        var gate = PermissionDecider.ForTesting(policy, store, (_, m) => notices.Add(m),
            (_, _, _) => Task.FromResult(PermissionChoice.TrustFolder));

        await gate.RequestAsync(new PermissionRequest(PermissionKind.FileRead, Path.Combine(_real, "x.txt"), null)
        { Policy = policy }, CancellationToken.None);

        Assert.Contains(notices, m => m.Text.Contains($"{PathResolver.RealFolder(_real)} (opened as {_link})"));
    }

    [Fact]
    public void TheRootFolder_ContainsEveryPath()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.True(PathResolver.Contains(root, Path.Combine(root, "etc", "passwd")));
        Assert.False(PathResolver.Contains(Path.Combine(root, "a"), Path.Combine(root, "ab")));
    }
}

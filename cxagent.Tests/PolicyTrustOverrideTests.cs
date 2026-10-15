using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>--trust: trusted for this process, with nothing written to permissions.json.</summary>
public class PolicyTrustOverrideTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "trustov-" + Guid.NewGuid().ToString("N"));
    private readonly string _config;

    public PolicyTrustOverrideTests()
    {
        Directory.CreateDirectory(_root);
        _config = Path.Combine(_root, ".cfg");
        Directory.CreateDirectory(_config);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (Exception) { } }

    private PermissionRulesStore Rules() => new(new AppPaths(_config));

    private static PermissionRequest Read(string path) => new(PermissionKind.FileRead, path, null);
    private static PermissionRequest Write(string path) => new(PermissionKind.FileWrite, path, null);

    [Fact]
    public void WithoutTheOverride_AnUnknownFolder_IsNotTrusted_AndAReadIsNotSilent()
    {
        var policy = new PermissionPolicy(_root, Rules());

        Assert.False(policy.FolderTrusted);
        Assert.False(policy.IsSilentlyAllowed(Read(Path.Combine(_root, "a.txt"))));
    }

    [Fact]
    public void WithTheOverride_TheFolderIsTrusted_AndNothingIsStored()
    {
        var rules = Rules();
        var policy = new PermissionPolicy(_root, rules) { TrustOverride = true };

        Assert.True(policy.FolderTrusted);
        Assert.True(policy.IsSilentlyAllowed(Read(Path.Combine(_root, "a.txt"))));
        Assert.Equal(TrustState.Unknown, rules.GetTrust(_root));
        Assert.False(File.Exists(Path.Combine(_config, "permissions.json")));
    }

    [Theory]
    [InlineData(".git/hooks/pre-commit", true)]
    [InlineData(".vscode/settings.json", true)]
    [InlineData(".claude/settings.json", true)]
    [InlineData(".idea/workspace.xml", true)]
    [InlineData("src/app.cs", false)]
    public void ProtectedWrites_AreTheExecutableConfigFolders(string relative, bool expected)
    {
        var policy = new PermissionPolicy(_root, Rules());

        Assert.Equal(expected, policy.IsProtectedWrite(Write(Path.Combine(_root, relative))));
    }

    [Fact]
    public void AReadOfAProtectedFolder_IsNotAProtectedWrite()
    {
        var policy = new PermissionPolicy(_root, Rules());

        Assert.False(policy.IsProtectedWrite(Read(Path.Combine(_root, ".git", "config"))));
    }
}

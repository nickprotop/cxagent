using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A read-only command may only read inside the folder.
///
/// <para>THE HOLE THIS CLOSES. IsReadOnly answers "does this program write" and says nothing about
/// WHAT it reads, so <c>cat /etc/shadow</c> was silently allowed in any trusted folder — while
/// <c>file read /etc/shadow</c> prompted, because that path resolves outside the boundary. Two
/// spellings of one read, opposite answers, and the permissive one was the less inspectable.</para>
///
/// <para>THE SAME SHAPE AS THE `cd*` GRANT: a check that examines part of a request and lets the
/// rest through. There it was the text after `&amp;&amp;`; here it was the arguments after the verb.
/// Both passed review because each piece was locally sound.</para>
/// </summary>
public class ReadOnlyBoundaryTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "robound-" + Guid.NewGuid().ToString("N"));

    private readonly PermissionPolicy _policy;

    public ReadOnlyBoundaryTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "in tree");

        var rules = new PermissionRulesStore(new AppPaths(_dir));
        rules.SetTrust(_dir, TrustState.Trusted);
        _policy = new PermissionPolicy(_dir, rules);
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private bool Silent(string command) =>
        _policy.IsSilentlyAllowed(new PermissionRequest(
            PermissionKind.Shell, command, "x*", Subject: command));

    // THE DISCLOSURE PATH. Every one of these reads a real file outside the folder, and every one
    // was silent before: no prompt, no rule, nothing but a "silent" row in the usage archive.
    [Theory]
    [InlineData("cat /etc/passwd")]
    [InlineData("head /etc/passwd")]
    [InlineData("grep root /etc/passwd")]
    [InlineData("wc -l /etc/passwd")]
    public void ReadingOutsideTheFolderIsRefused(string command) =>
        Assert.False(Silent(command));

    // AND THE SAME READ THROUGH THE FILE TOOL WAS ALREADY REFUSED, which is the asymmetry: the two
    // spellings now agree.
    [Fact]
    public void TheFileToolAndTheShellAgree()
    {
        var viaTool = _policy.IsSilentlyAllowed(
            new PermissionRequest(PermissionKind.FileRead, "/etc/passwd", null));

        Assert.False(viaTool);
        Assert.False(Silent("cat /etc/passwd"));
    }

    // IN-TREE READS STILL PASS, so the guard cannot swallow the feature it protects — this is the
    // whole point of the read-only free pass and it must keep working.
    [Fact]
    public void ReadingInsideTheFolderIsStillSilent()
    {
        Assert.True(Silent($"cat {Path.Combine(_dir, "notes.txt")}"));
        Assert.True(Silent("ls"));
        Assert.True(Silent("pwd"));
    }

    // A PATTERN IS NOT A PATH. Returning every token would refuse `grep TODO` for want of a file
    // called TODO — the extraction takes only what exists on disk, because a path that does not
    // exist reads nothing.
    [Fact]
    public void NonPathArgumentsAreIgnored()
    {
        Assert.True(Silent("grep TODO"));
        Assert.True(Silent("ls -la"));
    }

    // A STORED RULE CANNOT BUY ITS WAY PAST THE BOUNDARY EITHER. `cat*` is an honest grant for
    // reading this project, and it was permitting `cat /etc/passwd` — the rule matched the command
    // TEXT and never looked at the paths in it, walking straight around the fix above. Verified
    // live before the fix: with `cat*` granted, `cat /etc/passwd` ran silently.
    [Fact]
    public void AStoredRuleCannotReadOutsideTheFolder()
    {
        var rules = new PermissionRulesStore(new AppPaths(_dir));
        rules.SetTrust(_dir, TrustState.Trusted);
        rules.Add(_dir, PermissionKind.Shell, "cat*");

        var policy = new PermissionPolicy(_dir, rules);

        Assert.False(policy.IsSilentlyAllowed(new PermissionRequest(
            PermissionKind.Shell, "cat /etc/passwd", "cat*", Subject: "cat /etc/passwd")));

        // AND STILL PERMITS WHAT IT HONESTLY COVERS.
        var inTree = Path.Combine(_dir, "notes.txt");
        Assert.True(policy.IsSilentlyAllowed(new PermissionRequest(
            PermissionKind.Shell, $"cat {inTree}", "cat*", Subject: $"cat {inTree}")));
    }

    // THE cd FORM TOO, since that is the idiom the model actually writes.
    [Fact]
    public void TheCdIdiomIsCheckedOnBothHalves()
    {
        Assert.True(Silent($"cd {_dir} && cat notes.txt"));
        Assert.False(Silent($"cd {_dir} && cat /etc/passwd"));
    }
}

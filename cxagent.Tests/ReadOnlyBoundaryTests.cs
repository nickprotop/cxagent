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

    // A cd INTO THE HOME FOLDER LEAVES. The chain's cd target is taken off before the rest is examined, so a
    // tilde there meets no tilde refusal of the arguments' kind; joined onto the folder as a plain name it
    // would read as `<folder>/~` — inside.
    [Theory]
    [InlineData("cd ~ && ls")]
    [InlineData("cd ~/notes && ls")]
    [InlineData("cd ~user && ls")]
    public void ACdIntoTheHomeFolder_IsRefused(string command) =>
        Assert.False(Silent(command));

    // A cd WHOSE TARGET THE SHELL DECIDES leaves for a folder the text does not name: `-` is $OLDPWD, and a
    // variable is whatever it holds.
    [Theory]
    [InlineData("cd - && ls")]
    [InlineData("cd $HOME && ls")]
    [InlineData("cd ${OLDPWD} && ls")]
    public void ACdTheShellResolves_IsRefused(string command) =>
        Assert.False(Silent(command));

    // A CUSTOM ENVIRONMENT IS A DIFFERENT PROGRAM. PATH picks which `cat` runs, LD_PRELOAD what it loads, and
    // OLDPWD or CDPATH where a cd goes — none of it in the text the read-only pass vouches for.
    [Theory]
    [InlineData("PATH")]
    [InlineData("LD_PRELOAD")]
    [InlineData("CDPATH")]
    public void AReadWithACustomEnvironment_IsRefused(string variable)
    {
        var request = PermissionPolicy.RequestsFor("shell", new CxAgent.Core.Models.JobParameters(
            new Dictionary<string, object?>
            {
                ["command"] = "cat notes.txt",
                ["env"] = new Dictionary<string, string> { [variable] = "/tmp" },
            }), _dir).Single();

        Assert.False(_policy.IsSilentlyAllowed(request with { Policy = _policy }));
    }

    // A PATH AFTER A cd NAMES SOMETHING IN THE FOLDER THE cd ENTERED. Judged from the folder before it,
    // `cd sub && cat leak.txt` would read as `<root>/leak.txt` — inside — while the shell reads
    // `sub/leak.txt`, a link out of the folder.
    [Fact]
    public void APathAfterACd_IsJudgedFromTheFolderItEntered()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(Path.GetTempPath(), "robound-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "away");
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.CreateSymbolicLink(Path.Combine(_dir, "sub", "leak.txt"), Path.Combine(outside, "secret.txt"));
        File.WriteAllText(Path.Combine(_dir, "sub", "kept.txt"), "in tree");
        try
        {
            Assert.False(Silent("cd sub && cat leak.txt"));
            Assert.True(Silent("cd sub && cat kept.txt"));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

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

    /// <summary>
    /// A PIPE OF READS RUNS UNASKED, where every stage is a read on its own: `grep … | head` is the idiom for
    /// looking at part of a result, and asking for it taught the user to click through prompts.
    /// </summary>
    [Fact]
    public void APipeOfInTreeReads_IsSilent()
    {
        var notes = Path.Combine(_dir, "notes.txt");
        Assert.True(Silent($"grep -n tree {notes} | head -5"));
        Assert.True(Silent("ls | wc -l"));
        Assert.True(Silent($"cat {notes} | grep tree | wc -l"));

        // A STAGE OFF THE LIST STILL ASKS: `sort -o` and `uniq in out` write, which is why neither is on it.
        Assert.False(Silent($"cat {notes} | sort | uniq -c"));
        Assert.True(Silent($"cd {_dir} && ls | wc -l"));
    }

    /// <summary>
    /// EVERY STAGE IS CONFINED, NOT ONLY THE FIRST: a read outside the folder hidden after the pipe is the same
    /// disclosure as one before it.
    /// </summary>
    [Theory]
    [InlineData("cat /etc/passwd | head")]
    [InlineData("ls | grep root /etc/passwd")]
    [InlineData("cd /etc && cat passwd | wc -l")]
    public void APipeReadingOutsideTheFolder_Asks(string command) => Assert.False(Silent(command));

    /// <summary>
    /// A PIPE INTO SOMETHING THAT WRITES, or anything that is not a plain pipe, still asks: `tee` writes, `||` and
    /// `|&` chain or redirect, and a `|` inside quotes cannot be told from a real one without a shell parser.
    /// </summary>
    [Theory]
    [InlineData("cat notes.txt | tee out.txt")]
    [InlineData("ls || cat notes.txt")]
    [InlineData("ls |& cat")]
    [InlineData("grep 'a|b' notes.txt | wc -l")]
    [InlineData("ls | sh")]
    [InlineData("ls |")]
    [InlineData("| wc -l")]
    public void APipeThatIsNotJustReads_Asks(string command) => Assert.False(Silent(command));

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

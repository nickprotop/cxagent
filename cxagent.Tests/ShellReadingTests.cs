using CxAgent.Core.Permissions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// How the shell checks READ a command before judging it (user, 2026-11-11: a colleague's read-only exploration of its
/// own project asked on nearly every command, and auto mode never saw one of them). The checks split a command at the
/// shell's own operators — outside quotes only — lift a redirect's target out as a written path, and follow a leading
/// <c>cd</c> through the whole chain. Each misreading here was refusing a confined command, or could have passed an
/// unconfined one.
/// </summary>
[Collection("working-directory")]
public sealed class ShellReadingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("shell-reading-").FullName;
    private readonly PermissionPolicy _policy;

    public ShellReadingTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "sub"));
        File.WriteAllText(Path.Combine(_root, "src", "sub", "a.cs"), "public class A {}");
        File.WriteAllText(Path.Combine(_root, "src", "b.cs"), "public class B {}");
        var rules = new PermissionRulesStore(new CxAgent.Core.Storage.AppPaths(Directory.CreateTempSubdirectory("shell-reading-cfg-").FullName));
        rules.SetTrust(_root, TrustState.Trusted);
        _policy = new PermissionPolicy(_root, rules, EditMode.Auto);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ReviewEffect Effect(string command) =>
        _policy.EffectFor(new PermissionRequest(PermissionKind.Shell, command, CommandArity.RuleFor(command), Subject: command));

    [Theory]
    // A SEPARATOR INSIDE QUOTES IS PART OF AN ARGUMENT: the grep alternation is not a pipe into a program "///.*A)'".
    [InlineData("grep -nE '^\\s*(public|///.*A)' src/sub/a.cs | head -5")]
    [InlineData("grep -n \"class; struct\" src/b.cs")]
    // A QUOTED REGEX SHAPED LIKE A PATH IS A PATTERN, not the root of the disk.
    [InlineData("sed -n '1,5p' src/b.cs | grep -vE '^\\s*///' | head -3")]
    // A REDIRECT TO /dev/null DISCARDS; its target is not a program run by path.
    [InlineData("ls src 2>/dev/null | head")]
    [InlineData("ls src > /dev/null")]
    [InlineData("cat src/b.cs 2>&1 | head")]
    // A LEADING cd CARRIES THROUGH THE CHAIN: ../b.cs from src/sub is src/b.cs, inside.
    [InlineData("cd src/sub && echo '== A ==' && cat a.cs ../b.cs | head")]
    public void A_confined_command_reaches_the_reviewer(string command) =>
        Assert.Equal(ReviewEffect.MayApprove, Effect(command));

    [Theory]
    // THE cd IS FOLLOWED, SO WHAT LEAVES FROM IT IS SEEN: ../../.. from src/sub is above the root.
    [InlineData("cd src/sub && echo hi && cat ../../../outside.txt")]
    // A cd THAT IS NOT FOLLOWED leaves paths judged from where the command starts, which is never more lenient than
    // the folder it reaches: from the root, ../x is outside, though from src/sub after a second cd it would not be.
    [InlineData("cd src/sub && cd ../.. && cat ../x")]
    [InlineData("cd src/sub; cat ../../../x")]
    // A QUOTED PIPE DOES NOT HIDE A REAL ONE.
    [InlineData("grep 'a|b' src/b.cs | sh")]
    // A REDIRECT WRITES ITS TARGET, and the target is held to the boundary like any path.
    [InlineData("echo x > /tmp/cxagent-shell-reading-outside.txt")]
    [InlineData("echo x >/tmp/cxagent-shell-reading-outside.txt")]
    [InlineData("echo x 2>> ../outside.log")]
    [InlineData("cat < /etc/hostname")]
    // AN UNTERMINATED QUOTE IS NOT GUESSED AT.
    [InlineData("grep 'abc src/b.cs | head")]
    public void An_unconfined_or_unreadable_command_does_not(string command) =>
        Assert.Equal(ReviewEffect.None, Effect(command));

    /// <summary>
    /// A PLAIN TILDE IS THE HOME FOLDER, resolved and then held to the boundary like any path (user, 2026-11-11): a
    /// session in ~/source/ConsoleEx running `cd ~/source/ConsoleEx/x && …` was asked every time. `~user` and
    /// `$HOME` are the shell's to resolve and keep asking.
    /// </summary>
    [Theory]
    [InlineData("cd ~/{root}/src && grep -n class b.cs | head", ReviewEffect.MayApprove)]
    [InlineData("cat ~/{root}/src/b.cs", ReviewEffect.MayApprove)]
    [InlineData("ls ~", ReviewEffect.None)]
    [InlineData("ls ~/", ReviewEffect.None)]
    [InlineData("cat ~someone/{root}/src/b.cs", ReviewEffect.None)]
    [InlineData("echo x > ~/{root}/out.txt", ReviewEffect.MayApprove)]
    [InlineData("echo x > ~/elsewhere.txt", ReviewEffect.None)]
    public void A_plain_tilde_is_the_home_folder(string command, ReviewEffect expected)
    {
        var before = CommandSubjects.HomeForTest;
        CommandSubjects.HomeForTest = Path.GetDirectoryName(_root);
        try
        {
            Assert.Equal(expected, Effect(command.Replace("{root}", Path.GetFileName(_root), StringComparison.Ordinal)));
        }
        finally { CommandSubjects.HomeForTest = before; }
    }

    /// <summary>
    /// A GLOB WITH ITS FOLDER SPELLED OUT IS LISTED NOW, and every match is held to the boundary (user, 2026-11-11). A
    /// wildcard in the folder part, a bracket class, or a match that is a link out of the project keeps it asking.
    /// </summary>
    [Theory]
    [InlineData("grep -n class src/*.cs | head", ReviewEffect.MayApprove)]
    [InlineData("cat src/sub/?.cs", ReviewEffect.MayApprove)]
    [InlineData("ls src/nothing-here/*.cs", ReviewEffect.MayApprove)]
    [InlineData("cat src/*/a.cs", ReviewEffect.None)]
    [InlineData("cat src/[ab].cs", ReviewEffect.None)]
    [InlineData("cat ../*.cs", ReviewEffect.None)]
    public void A_glob_is_listed_and_each_match_confined(string command, ReviewEffect expected) =>
        Assert.Equal(expected, Effect(command));

    [Fact]
    public void A_glob_matching_a_link_out_of_the_project_asks()
    {
        File.CreateSymbolicLink(Path.Combine(_root, "src", "leak.cs"), "/etc/hostname");

        Assert.Equal(ReviewEffect.None, Effect("cat src/*.cs"));
    }

    // ---- reading where a file read would not ask ------------------------------------------------------------------

    private string Elsewhere()
    {
        var other = Directory.CreateTempSubdirectory("shell-reading-other-").FullName;
        File.WriteAllText(Path.Combine(other, "f.cs"), "class F {}");
        return other;
    }

    private bool Silent(PermissionPolicy policy, string command) =>
        policy.IsSilentlyAllowed(new PermissionRequest(PermissionKind.Shell, command, CommandArity.RuleFor(command), Subject: command));

    /// <summary>
    /// A FOLDER GRANTED FOR READING IS READABLE FROM THE SHELL TOO (user, 2026-11-11): a session holding Always for file
    /// reads under ~/source still asked for every `grep` and `find` there. Only a read-only command; one that writes
    /// is held to the project as before.
    /// </summary>
    [Fact]
    public void A_read_only_command_reads_a_folder_granted_for_reading()
    {
        var other = Elsewhere();
        var rules = new PermissionRulesStore(new CxAgent.Core.Storage.AppPaths(Directory.CreateTempSubdirectory("shell-reading-cfg-").FullName));
        rules.SetTrust(_root, TrustState.Trusted);
        var policy = new PermissionPolicy(_root, rules, EditMode.AlwaysAsk);
        Assert.False(Silent(policy, $"grep -n class {other}/f.cs"));

        rules.Add(_root, PermissionKind.FileRead, other + "/");

        Assert.True(Silent(policy, $"grep -n class {other}/f.cs"));
        Assert.True(Silent(policy, $"cd {other} && ls"));
        Assert.False(Silent(policy, $"rm {other}/f.cs"));
        Assert.False(Silent(policy, $"cp {other}/f.cs {other}/g.cs"));
    }

    /// <summary>
    /// AND A COLLEAGUE'S WORKPLACE: a colleague brought into a session elsewhere reads the codebases it knows without
    /// asking through the file tools, and asked for every `grep` there through the shell.
    /// </summary>
    [Fact]
    public void A_read_only_command_reads_a_colleague_s_workplace()
    {
        var other = Elsewhere();
        var rules = new PermissionRulesStore(new CxAgent.Core.Storage.AppPaths(Directory.CreateTempSubdirectory("shell-reading-cfg-").FullName));
        rules.SetTrust(_root, TrustState.Trusted);
        rules.SetTrust(other, TrustState.Trusted);
        var policy = new PermissionPolicy(_root, rules, EditMode.AlwaysAsk).WithReadAlso(new ReadAlso([other], null));

        Assert.True(Silent(policy, $"cat {other}/f.cs"));
        Assert.True(Silent(policy, $"cd {other} && grep -rn class ."));
        Assert.False(Silent(policy, $"touch {other}/new.cs"));
    }

    /// <summary>
    /// NO ALWAYS THAT CANNOT HOLD (user, 2026-11-11): a stored rule never reaches past the project or across a chain, so
    /// offering one for such a command stored a rule and asked again next time.
    /// </summary>
    [Theory]
    [InlineData("dotnet test", true)]
    [InlineData("grep -n class src/b.cs", true)]
    [InlineData("find /etc -name hosts", false)]
    [InlineData("cat ~/elsewhere.txt", false)]
    [InlineData("dotnet build 2>&1 | tail -5", false)]
    public void Always_is_offered_only_where_a_rule_could_match(string command, bool offered) =>
        Assert.Equal(offered, _policy.RuleCouldMatch(new PermissionRequest(PermissionKind.Shell, command, CommandArity.RuleFor(command), Subject: command)));
}

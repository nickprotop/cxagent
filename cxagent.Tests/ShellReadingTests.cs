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
}

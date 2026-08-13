using CxAgent.Core.Permissions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What "Always allow" writes down.
///
/// <para>The rule used to be the whole command string, so 111 accumulated grants could essentially
/// never match again. The obvious fix — grant the first word — is the dangerous one, and the tests
/// that matter here are the ones proving it was not taken.</para>
/// </summary>
public class CommandArityTests
{
    /// <summary>
    /// THE HOLE THIS DESIGN EXISTS TO CLOSE. Granting `git` because the user approved `git status`
    /// would grant `git push --force` with it. A subcommand is part of the command's NAME.
    /// </summary>
    [Fact]
    public void GrantingGitStatus_DoesNotGrantGitPush()
    {
        var granted = CommandArity.RuleFor("git status");

        Assert.Equal("git status*", granted);

        // The store matches a trailing * as a prefix, so this is the real question: does the rule
        // written for one subcommand cover another?
        Assert.NotEqual(granted, CommandArity.RuleFor("git push --force"));
        Assert.Equal("git push*", CommandArity.RuleFor("git push --force"));
    }

    [Theory]
    [InlineData("git status", "git status*")]
    [InlineData("git commit -m 'x'", "git commit*")]
    [InlineData("docker compose up", "docker compose up*")]
    [InlineData("docker run nginx", "docker run*")]
    [InlineData("npm install", "npm install*")]
    [InlineData("npm run build", "npm run build*")]
    [InlineData("dotnet build", "dotnet build*")]
    [InlineData("dotnet test", "dotnet test*")]
    [InlineData("kubectl get pods", "kubectl get*")]
    [InlineData("aws s3 ls", "aws s3 ls*")]
    public void ASubcommandIsPartOfTheName(string command, string expected) =>
        Assert.Equal(expected, CommandArity.RuleFor(command));

    /// <summary>
    /// FLAGS ARE NEVER PART OF THE NAME. A positional count would be defeated by moving one, which
    /// is a difference the user cannot see and would not expect to matter.
    /// </summary>
    [Theory]
    [InlineData("npm --silent install", "npm install*")]
    [InlineData("git --no-pager log", "git log*")]
    [InlineData("dotnet --verbosity quiet build", "dotnet quiet*")]
    public void FlagsDoNotCount(string command, string expected) =>
        Assert.Equal(expected, CommandArity.RuleFor(command));

    /// <summary>
    /// A program with no subcommands generalises over its arguments, which is the whole point —
    /// `find Services -type f` and `find . -type f` become one rule instead of two.
    /// </summary>
    [Theory]
    [InlineData("ls -la src", "ls*")]
    [InlineData("find . -type f", "find*")]
    [InlineData("cat README.md", "cat*")]
    [InlineData("grep -rn TODO .", "grep*")]
    public void AProgramWithoutSubcommands_GeneralisesOverItsArguments(string command, string expected) =>
        Assert.Equal(expected, CommandArity.RuleFor(command));

    /// <summary>
    /// AN UNKNOWN PROGRAM GETS THE PROGRAM ONLY. That is the conservative answer for something the
    /// table has never classified: it generalises over arguments and grants nothing about a
    /// subcommand nobody has reasoned about.
    /// </summary>
    [Fact]
    public void AnUnknownProgram_GrantsOnlyItself() =>
        Assert.Equal("someweirdtool*", CommandArity.RuleFor("someweirdtool --do-a-thing now"));

    /// <summary>
    /// A PATH IS AN ARGUMENT, NOT A SUBCOMMAND. `git -C /some/path status` drops the flag and would
    /// otherwise store `/some/path` as though it named something. The resulting rule is useless
    /// rather than unsafe — nothing matches it, and the real `git status` still asks.
    /// </summary>
    [Fact]
    public void APathIsNeverTreatedAsASubcommand()
    {
        var rule = CommandArity.RuleFor("git -C /some/path status");

        Assert.Equal("git*", rule);
        Assert.DoesNotContain("/some/path", rule!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NothingToName_WritesNoRule(string? command) =>
        Assert.Null(CommandArity.RuleFor(command));

    /// <summary>
    /// LONGEST MATCH WINS, so a subcommand that takes its own subcommand gets all three words.
    /// `npm run` is not a command; `npm run build` is.
    /// </summary>
    [Fact]
    public void TheLongestMatchingPrefixWins()
    {
        Assert.Equal("npm run build*", CommandArity.RuleFor("npm run build"));
        Assert.Equal("npm install*", CommandArity.RuleFor("npm install express"));
    }

    /// <summary>
    /// THE WHOLE ROUND TRIP, through the real producer, the real store and the real policy — the
    /// only way to know the three agree about what a rule IS. They did not at first: the policy
    /// matched a shell request against its own AlwaysRule, so once a rule became a PATTERN that
    /// would have compared `git status*` to `git status*` and matched nothing real.
    /// </summary>
    [Fact]
    public void AGrantOnOneCommand_SilencesTheSameCommandWithDifferentArguments()
    {
        var dir = Directory.CreateTempSubdirectory("cxa-arity-").FullName;
        var config = Directory.CreateTempSubdirectory("cxa-arity-cfg-").FullName;
        try
        {
            // SEPARATE DIRECTORIES. The store writes permissions.json into its config dir, and the
            // scope's identity comes from stat'ing the folder being scoped — pointing both at one
            // directory makes a folder's identity depend on the store's own writes.
            var rules = new PermissionRulesStore(new CxAgent.Core.Storage.AppPaths(config));
            var policy = new PermissionPolicy(dir, rules);

            var first = PermissionPolicy.RequestsFor("shell",
                Params(("command", "find Services -type f"))).Single();

            Assert.False(policy.IsSilentlyAllowed(first));      // nothing granted yet
            rules.Add(dir, first.Kind, first.AlwaysRule!);      // the user presses Always

            // A DIFFERENT INVOCATION OF THE SAME COMMAND — what 111 stored literals could never do.
            var second = PermissionPolicy.RequestsFor("shell",
                Params(("command", "find . -name x.cs"))).Single();

            Assert.True(policy.IsSilentlyAllowed(second));

            // AND A DIFFERENT PROGRAM STILL ASKS.
            var other = PermissionPolicy.RequestsFor("shell",
                Params(("command", "rm -rf build"))).Single();

            Assert.False(policy.IsSilentlyAllowed(other));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(config, recursive: true);
        }
    }

    /// <summary>The same property as GrantingGitStatus_DoesNotGrantGitPush, proven through the
    /// store rather than the helper.</summary>
    [Fact]
    public void AGrantOnOneSubcommand_DoesNotSilenceAnother()
    {
        var dir = Directory.CreateTempSubdirectory("cxa-arity2-").FullName;
        var config = Directory.CreateTempSubdirectory("cxa-arity2-cfg-").FullName;
        try
        {
            var rules = new PermissionRulesStore(new CxAgent.Core.Storage.AppPaths(config));
            var policy = new PermissionPolicy(dir, rules);

            var status = PermissionPolicy.RequestsFor("shell", Params(("command", "git status"))).Single();
            rules.Add(dir, status.Kind, status.AlwaysRule!);

            Assert.True(policy.IsSilentlyAllowed(status));

            var push = PermissionPolicy.RequestsFor("shell",
                Params(("command", "git push --force origin main"))).Single();

            Assert.False(policy.IsSilentlyAllowed(push));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(config, recursive: true);
        }
    }

    private static CxAgent.Core.Models.JobParameters Params(params (string, object?)[] entries)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (k, v) in entries) dict[k] = v;
        return new CxAgent.Core.Models.JobParameters(dict);
    }
}

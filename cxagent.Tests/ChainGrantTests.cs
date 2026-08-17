using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A chained command cannot become a standing rule.
///
/// <para>THE HOLE THIS CLOSES, measured on a live drive. CommandArity reads the first word or two
/// and knows nothing about <c>&amp;&amp;</c>, so <c>cd /repo &amp;&amp; dotnet test</c> produced the
/// rule <c>cd*</c> — and PermissionRulesStore matches a pattern by RAW PREFIX, so that rule then
/// permitted <c>cd /tmp &amp;&amp; rm -rf ~</c>. Since almost any command can be written
/// <c>cd . &amp;&amp; anything</c>, one grant became arbitrary shell.</para>
///
/// <para>AND THE PROMPT HID IT: "Always allow covers: cd*" reads as "cd commands" and means
/// "anything whose text starts with cd". The drive that found this asked seven times, six of them
/// for shell shapes — pressing Always on one of those chains is exactly what a user under that much
/// friction does, and is what happened.</para>
/// </summary>
public class ChainGrantTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "chain-" + Guid.NewGuid().ToString("N"));

    public ChainGrantTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    // NO RULE IS OFFERED FOR A CHAIN. The action can still be allowed once; what it cannot do is
    // become a standing grant, because there is no honest rule to write for several commands.
    [Theory]
    [InlineData("cat notes.txt | head -20")]
    [InlineData("dotnet build; dotnet test")]
    [InlineData("dotnet test 2>&1 > /tmp/out.txt")]
    public void AChainedCommandGetsNoRule(string command) =>
        Assert.Null(CommandArity.RuleFor(command));

    // THE `cd X && one-command` IDIOM IS THE EXCEPTION, and it earns one: it is what the model
    // writes constantly, refusing it outright is what made a user reach for `cd*`, and the rule it
    // produces names the command actually run rather than the `cd` in front of it. ReadOnlyCommands
    // already strips and boundary-checks this exact form for the allow decision, so no new parsing
    // is involved.
    [Theory]
    [InlineData("cd /repo && dotnet test", "dotnet test*")]
    [InlineData("cd /repo && ls", "ls*")]
    public void TheCdIdiomGetsARuleForTheCommandItRuns(string command, string expected) =>
        Assert.Equal(expected, CommandArity.RuleFor(command));

    // BUT ONLY ONE COMMAND AFTER IT. `cd /x && a && b` is still a chain and still gets nothing.
    [Fact]
    public void ACdFollowedByAChainStillGetsNoRule() =>
        Assert.Null(CommandArity.RuleFor("cd /repo && dotnet build && rm -rf /tmp/x"));

    // AN ORDINARY COMMAND STILL DOES, so the guard cannot swallow the feature it protects.
    [Theory]
    [InlineData("git status", "git status*")]
    [InlineData("dotnet test", "dotnet test*")]
    [InlineData("ls -la", "ls*")]
    public void APlainCommandStillGetsOne(string command, string expected) =>
        Assert.Equal(expected, CommandArity.RuleFor(command));

    // AND A STORE THAT ALREADY HOLDS `cd*` CANNOT USE IT ON A CHAIN. Refusing to create one protects
    // the future; this protects the past, because every machine that granted it before the fix still
    // has the rule on disk.
    [Fact]
    public void AnExistingBroadRuleCannotPermitAChain()
    {
        var rules = new PermissionRulesStore(new AppPaths(_dir));
        rules.SetTrust(_dir, TrustState.Trusted);
        rules.Add(_dir, PermissionKind.Shell, "cd*");

        var policy = new PermissionPolicy(_dir, rules);

        var chained = new PermissionRequest(PermissionKind.Shell,
            "cd /tmp && rm -rf /important", "cd*", Subject: "cd /tmp && rm -rf /important");

        Assert.False(policy.IsSilentlyAllowed(chained));
    }

    // THE RULE STILL WORKS ON WHAT IT HONESTLY COVERS, so the past-protection is not a blanket
    // revocation of everything already granted.
    [Fact]
    public void AnExistingRuleStillPermitsThePlainCommand()
    {
        var rules = new PermissionRulesStore(new AppPaths(_dir));
        rules.SetTrust(_dir, TrustState.Trusted);
        rules.Add(_dir, PermissionKind.Shell, "dotnet test*");

        var policy = new PermissionPolicy(_dir, rules);

        var plain = new PermissionRequest(PermissionKind.Shell,
            "dotnet test --no-build", "dotnet test*", Subject: "dotnet test --no-build");

        Assert.True(policy.IsSilentlyAllowed(plain));
    }
}

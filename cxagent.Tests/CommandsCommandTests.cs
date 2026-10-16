using CxAgent.Core.Commands;
using CxAgent.Core.Skills;
using Xunit;

namespace CxAgent.Tests;

public class CommandsCommandTests
{
    private static Invocable Entry(string name, string path, InvocableKind kind = InvocableKind.Command,
        IReadOnlyList<string>? ignored = null) =>
        new(name, kind, path, Path.GetDirectoryName(path)!, null, $"does {name}", null, true, false,
            ignored ?? [], $"body of {name}", null);

    private static string Render(InvocableCatalogResult catalog, string? argument = null,
        bool trusted = true, params string[] builtIns) =>
        CommandsCommand.Render(new CommandsView(catalog, n => builtIns.Contains(n), trusted), argument);

    [Fact]
    public void Lists_NameSourceAndDescription()
    {
        var text = Render(new([Entry("review", "/r/.cxagent/commands/review.md")], [], []));

        Assert.Contains("/review", text);
        Assert.Contains("does review", text);
        Assert.Contains(".cxagent/commands", text);
    }

    [Fact]
    public void Shadowed_NamesTheWinner()
    {
        var winner = Entry("review", "/r/.cxagent/commands/review.md");
        var loser = Entry("review", "/home/config/commands/review.md");

        var text = Render(new([winner], [new ShadowedEntry(loser, winner)], []));

        Assert.Contains("/home/config/commands/review.md", text);
        Assert.Contains("shadowed by /r/.cxagent/commands/review.md", text);
    }

    [Fact]
    public void AFileNamedLikeABuiltIn_IsShadowedByTheBuiltIn()
    {
        var text = Render(new([Entry("init", "/r/.cxagent/commands/init.md")], [], []), builtIns: "/init");

        Assert.Contains("shadowed by built-in /init", text);
    }

    [Fact]
    public void Problems_AndIgnoredFields_AreListed()
    {
        var entry = Entry("commit", "/r/c/commit.md", ignored: ["allowed-tools"]);

        var text = Render(new([entry], [], [new SkillProblem("/r/c/bad.md", "frontmatter is never closed with ---")]));

        Assert.Contains("never closed", text);
        Assert.Contains("allowed-tools", text);
    }

    [Fact]
    public void AnUntrustedFolder_SaysShellLinesWillAsk() =>
        Assert.Contains("will ask", Render(new([Entry("x", "/r/x.md")], [], []), trusted: false));

    [Fact]
    public void ANamedCommand_ShowsItsFile()
    {
        var text = Render(new([Entry("review", "/r/review.md")], [], []), "review");

        Assert.Contains("/r/review.md", text);
        Assert.Contains("body of review", text);
    }

    [Fact]
    public void AnUnknownName_SaysSo() =>
        Assert.Contains("No command named", Render(InvocableCatalogResult.Empty, "nope"));

    /// <summary>Showing a file a built-in shadows says so — typing that name runs the built-in.</summary>
    [Fact]
    public void ANamedCommandABuiltInHolds_SaysTheBuiltInRuns() =>
        Assert.Contains("shadowed by built-in /init",
            Render(new([Entry("init", "/r/.cxagent/commands/init.md")], [], []), "init", builtIns: "/init"));
}

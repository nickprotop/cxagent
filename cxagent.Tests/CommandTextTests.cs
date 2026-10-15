using CxAgent.Core.Skills;
using Xunit;

namespace CxAgent.Tests;

public class CommandTextTests
{
    [Fact]
    public void SplitArguments_GroupsQuotedWords() =>
        Assert.Equal(["fix", "the login page", "now"], CommandText.SplitArguments("fix \"the login page\" now"));

    [Fact]
    public void SplitArguments_OfNothing_IsEmpty() => Assert.Empty(CommandText.SplitArguments("   "));

    [Fact]
    public void PosixQuote_MakesOneInertWord() =>
        Assert.Equal("'1; rm -rf ~'\\'''", CommandText.PosixQuote("1; rm -rf ~'"));

    [Fact]
    public void Segments_FindShellLinesAndFileTokens()
    {
        var parts = CommandText.Segments("Diff: !`git diff --staged`\nSee @src/a.cs and mail me@x.com.");

        Assert.Collection(parts,
            p => Assert.Equal(new Segment.Literal("Diff: "), p),
            p => Assert.Equal(new Segment.Shell("git diff --staged"), p),
            p => Assert.Equal(new Segment.Literal("\nSee "), p),
            p => Assert.Equal(new Segment.At("@src/a.cs", "src/a.cs"), p),
            p => Assert.Equal(new Segment.Literal(" and mail me@x.com."), p));
    }

    /// <summary>Trailing sentence punctuation is not part of a path.</summary>
    [Fact]
    public void Segments_TrimTrailingPunctuation_FromAPath() =>
        Assert.Contains(new Segment.At("@README.md", "README.md"), CommandText.Segments("Read @README.md."));

    [Fact]
    public void Substitute_FillsPositionalAndAll()
    {
        Assert.Equal("PR 123 at high (123 high), missing: .",
            CommandText.Substitute("PR $1 at $2 ($ARGUMENTS), missing: $3.", TypedArguments.From("123 high"), quote: false));
    }

    [Fact]
    public void Substitute_Quoted_ForAShellLine()
    {
        Assert.Equal("gh pr view '1; rm -rf ~'",
            CommandText.Substitute("gh pr view $1", TypedArguments.From("\"1; rm -rf ~\""), quote: true));
    }

    [Theory]
    [InlineData("Review $ARGUMENTS", true)]
    [InlineData("Fix issue $1", true)]
    [InlineData("Costs $5 today", true)]
    [InlineData("No placeholders here", false)]
    public void HasPlaceholder(string text, bool expected) => Assert.Equal(expected, CommandText.HasPlaceholder(text));

    /// <summary>A placeholder the author already quoted keeps their quotes and stays one inert word.</summary>
    [Theory]
    [InlineData("echo '$1'", "x; echo INJECTED", "echo 'x; echo INJECTED'")]
    [InlineData("echo '$1'", "it's", "echo 'it'\\''s'")]
    [InlineData("echo \"$1\"", "a\"; echo X; \"", "echo \"a\\\"; echo X; \\\"\"")]
    [InlineData("echo \"$1\"", "$(id) `id` \\", "echo \"\\$(id) \\`id\\` \\\\\"")]
    [InlineData("gh pr view \"$ARGUMENTS\"", "123", "gh pr view \"123\"")]
    [InlineData("gh pr view $1", "1; rm -rf ~", "gh pr view '1; rm -rf ~'")]
    public void Substitute_ForAShellLine_RespectsTheAuthorsQuotes(string line, string argument, string expected) =>
        Assert.Equal(expected, CommandText.Substitute(line, new TypedArguments(argument, [argument]), quote: true));

    /// <summary>The real shell agrees: whatever the quoting, the argument arrives as exactly what was typed.</summary>
    [Theory]
    [InlineData("printf %s $1")]
    [InlineData("printf %s '$1'")]
    [InlineData("printf %s \"$1\"")]
    public async Task ATypedArgument_ReachesTheShell_AsOneLiteralWord(string line)
    {
        if (OperatingSystem.IsWindows()) return;
        const string hostile = "a'b\"c; echo INJECTED $(echo SUB) `echo TICK` \\";

        var command = CommandText.Substitute(line, new TypedArguments(hostile, [hostile]), quote: true);
        var result = await ShellLine.RunAsync(command, Path.GetTempPath(), TimeSpan.FromSeconds(10), default);

        Assert.Equal(hostile, result.Output);
    }
}

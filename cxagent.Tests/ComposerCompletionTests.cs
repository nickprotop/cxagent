using CxAgent.Core.Commands;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What a composer offers as you type — commands, their arguments and values, and <c>@</c> paths — computed once for
/// both front ends, each row carrying the text and caret choosing it gives.
/// </summary>
public sealed class ComposerCompletionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("composer-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static ComposerSources Sources(Func<string, IReadOnlyList<CommandArgument>>? values = null, string? root = null) =>
        new(Registry: null, Values: values, Custom: [], Root: root);

    // ---- a colleague's tab: its own few commands, from the table the tab runs ------------------------------------------

    [Fact]
    public void A_slash_in_a_colleague_s_tab_offers_its_three_commands()
    {
        var rows = ComposerCompletion.ColleagueTab("/");
        Assert.Equal(["/clear", "/freeze", "/unfreeze"], rows.Select(r => r.Label));
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.Summary)));
    }

    [Theory]
    [InlineData("/f", "/freeze")]
    [InlineData("/UN", "/unfreeze")]
    [InlineData("/cl", "/clear")]
    public void A_colleague_s_tab_narrows_as_typed(string typed, string offered)
    {
        var row = Assert.Single(ComposerCompletion.ColleagueTab(typed));
        Assert.Equal(offered, row.Text);
        Assert.Equal(offered.Length, row.Caret);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("/memory")]
    [InlineData("/freeze now")]
    [InlineData("/fr\nmore")]
    public void A_colleague_s_tab_offers_nothing_else(string typed) =>
        Assert.Empty(ComposerCompletion.ColleagueTab(typed.Replace("\\n", "\n")));

    [Fact]
    public void A_slash_offers_matching_commands_with_their_summaries()
    {
        var rows = ComposerCompletion.For("/memo", 5, Sources());
        var row = Assert.Single(rows, r => r.Label == "/memory");
        Assert.Contains("notes", row.Summary, StringComparison.Ordinal);
        Assert.Equal("/memory", row.Text);
        Assert.Equal("/memory".Length, row.Caret);
    }

    [Fact]
    public void A_space_after_a_command_offers_its_verbs_and_a_placeholder_completes_to_the_verb()
    {
        var rows = ComposerCompletion.For("/memory ", 8, Sources());
        var open = Assert.Single(rows, r => r.Label == "open <name>");
        Assert.Equal("/memory open ", open.Text);
    }

    [Fact]
    public void A_verb_with_a_value_set_offers_the_values()
    {
        IReadOnlyList<CommandArgument> Notes(string set) =>
            set == ValueSources.MemoryNotes ? [new("deploy-target", "project · where it deploys")] : [];
        var rows = ComposerCompletion.For("/memory open ", 13, Sources(Notes));
        var note = Assert.Single(rows);
        Assert.Equal("deploy-target", note.Label);
        Assert.Equal("/memory open deploy-target", note.Text);
    }

    [Fact]
    public void Plain_text_offers_nothing() =>
        Assert.Empty(ComposerCompletion.For("fix the parser", 14, Sources()));

    [Fact]
    public void Commands_off_offers_no_commands() =>
        Assert.Empty(ComposerCompletion.For("/me", 3, Sources() with { CommandsOff = true }));

    [Fact]
    public void An_at_reference_offers_paths_and_a_directory_keeps_the_at()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Parser.cs"), "");
        const string text = "fix @sr please";
        var rows = ComposerCompletion.For(text, "fix @sr".Length, Sources(root: _root));

        var dir = Assert.Single(rows, r => r.Label.StartsWith("src", StringComparison.Ordinal));
        Assert.Equal("fix @src/ please", dir.Text);
        Assert.Equal("fix @src/".Length, dir.Caret);
    }

    [Fact]
    public void A_file_completes_to_a_plain_path_mid_sentence()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Parser.cs"), "");
        const string text = "fix @src/Pa please";
        var rows = ComposerCompletion.For(text, "fix @src/Pa".Length, Sources(root: _root));

        var file = Assert.Single(rows);
        Assert.Equal("fix src/Parser.cs please", file.Text);
        Assert.Equal("fix src/Parser.cs".Length, file.Caret);
    }

    [Fact]
    public void Without_a_root_an_at_is_just_text() =>
        Assert.Empty(ComposerCompletion.For("look @sr", 8, Sources()));
}

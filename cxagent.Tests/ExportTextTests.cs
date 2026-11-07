using CxAgent.Core.Export;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The words an export's choices are offered in — one set, read by the terminal's dialog and the browser's.</summary>
public class ExportTextTests
{
    [Fact]
    public void Every_part_a_full_export_holds_has_one_label()
    {
        var all = ExportText.Parts.Aggregate(ExportParts.None, (sum, p) => sum | p.Part);
        Assert.Equal(ExportOptions.Full, all);
        Assert.Equal(ExportText.Parts.Count, ExportText.Parts.Select(p => p.Label).Distinct().Count());
        Assert.Equal("Tool calls, one line each", ExportText.Parts[0].Label);
    }

    [Theory]
    [InlineData(ExportOptions.Readable, "What was said and done — tool calls as lines, edits as diffs.")]
    [InlineData(ExportOptions.Full, "All the model saw — tool output, thinking, the system prompt.")]
    [InlineData(ExportParts.ToolCalls, "Custom — your own mix, set under Advanced.")]
    public void A_mix_is_described_by_its_preset(ExportParts parts, string expected) =>
        Assert.Equal(expected, ExportText.Describe(new ExportOptions(ExportScope.Whole, 0, parts).PresetName));

    [Fact]
    public void The_size_line_counts_messages_and_sub_agents()
    {
        Assert.Equal("≈ 1 KB · 1 message · 2 sub-agents", ExportText.SizeLine(new ExportStats(1, 2, 1024), isAgent: false));
        Assert.Equal("≈ 1 KB · 3 messages", ExportText.SizeLine(new ExportStats(3, 0, 1024), isAgent: true));
    }

    [Fact]
    public void Only_tool_output_earns_the_sharing_warning()
    {
        Assert.Null(ExportText.PartsNote(ExportOptions.Readable));
        Assert.Contains("check before sharing", ExportText.PartsNote(ExportOptions.Full), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_big_export_is_large_for_the_clipboard()
    {
        Assert.False(ExportText.LargeForClipboard(new ExportStats(1, 0, 10)));
        Assert.True(ExportText.LargeForClipboard(new ExportStats(1, 0, ExportTarget.ClipboardWarnBytes + 1)));
    }
}

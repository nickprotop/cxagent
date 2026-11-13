using CxAgent.Core.Colleagues;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Web;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A colleague's way back into what it has done: words across its past, one round whole, and the current version of its
/// experience — its own library, read only, every answer within one tool result's room.
/// </summary>
public sealed class RecallToolTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("recall-").FullName;
    private readonly string _invoice = "/home/dev/source/invoice";
    private readonly string _cxgpu = "/home/dev/source/cxgpu";

    public RecallToolTests()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Created = DateTimeOffset.UtcNow });
        Library.Update("Charting", r => r with
        {
            Workplaces = [new Workplace("invoice-11111111", _invoice, 1, DateTimeOffset.UtcNow),
                new Workplace("cxgpu-22222222", _cxgpu, 1, DateTimeOffset.UtcNow.AddDays(-1))],
        });
        Round(_cxgpu, "lay the panes out", "Three panes, legend follows the series.", DateTimeOffset.UtcNow.AddDays(-2));
        Round(_invoice, "compute the total", "Bulk discount per line, then tax, then loyalty.", DateTimeOffset.UtcNow.AddDays(-1));
        Round(_cxgpu, "move the legend", "The legend follows the series list, below the panes.", DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        try { Directory.Delete(_config, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private long Round(string folder, string asked, string answered, DateTimeOffset at, string session = "SESSIONAAAA1") =>
        Library.Append("Charting", new PastRound(0, session, folder, at, at,
            [new ChatMessage { Role = "user", Content = asked }, new ChatMessage { Role = "assistant", Content = answered }]));

    private RecallTool Tool(ServedFolders? served = null) => new(new RecallScope(Library, "Charting", served));

    private static JobParameters Call(params (string Key, object Value)[] values) =>
        new(values.ToDictionary(v => v.Key, v => (object?)v.Value));

    private async Task<string> Ask(params (string Key, object Value)[] values)
    {
        var result = await Tool().ExecuteAsync(Call(values), null!, CancellationToken.None);
        return result.Output["content"]?.ToString() ?? "";
    }

    [Fact]
    public async Task Search_finds_rounds_by_words_newest_first()
    {
        var text = await Ask(("action", "search"), ("query", "Legend SERIES"));

        Assert.Contains("round 3", text);
        Assert.Contains("round 1", text);
        Assert.DoesNotContain("round 2", text);
        Assert.True(text.IndexOf("round 3", StringComparison.Ordinal) < text.IndexOf("round 1", StringComparison.Ordinal));
        Assert.Contains("SESSIONA", text);
        Assert.Contains("cxgpu", text);
    }

    [Fact]
    public async Task Search_can_keep_to_one_codebase()
    {
        var text = await Ask(("action", "search"), ("query", "the"), ("codebase", "invoice"));

        Assert.Contains("round 2", text);
        Assert.DoesNotContain("round 1", text);
        Assert.DoesNotContain("round 3", text);
    }

    [Fact]
    public async Task Nothing_found_says_so()
    {
        var text = await Ask(("action", "search"), ("query", "kubernetes"));

        Assert.Contains("Nothing", text);
        Assert.Contains("experience", text);
    }

    [Fact]
    public async Task Read_returns_one_round_in_parts()
    {
        var text = await Ask(("action", "read"), ("round", 2));
        Assert.Contains("compute the total", text);
        Assert.Contains("Bulk discount per line", text);

        var huge = Round(_invoice, "read everything", new string('x', RecallTool.MaxChars * 2 + 10), DateTimeOffset.UtcNow);
        var first = await Ask(("action", "read"), ("round", huge));
        var last = await Ask(("action", "read"), ("round", huge), ("part", 3));
        Assert.True(first.Length <= RecallTool.MaxChars);
        Assert.Contains("part 1 of 3", first);
        Assert.Contains("part 3 of 3", last);
    }

    [Fact]
    public async Task Experience_reads_the_current_version()
    {
        Library.WriteCore("Charting", "## Lessons\nPanes first.");
        Library.WriteCodebase("Charting", "cxgpu-22222222", "## Architecture\nThree panes.");

        Assert.Contains("Panes first.", await Ask(("action", "experience")));
        Assert.Contains("Three panes.", await Ask(("action", "experience"), ("codebase", "cxgpu")));
        var unknown = await Ask(("action", "experience"), ("codebase", "kernel"));
        Assert.Contains("invoice", unknown);
        Assert.Contains("cxgpu", unknown);
    }

    [Fact]
    public async Task Search_over_a_large_past_is_capped()
    {
        for (var i = 0; i < 300; i++) Round(_cxgpu, $"legend change {i}", new string('l', 400) + " legend", DateTimeOffset.UtcNow);

        var text = await Ask(("action", "search"), ("query", "legend"));

        Assert.True(text.Length <= RecallTool.MaxChars);
        Assert.Contains("more", text);
    }

    [Fact]
    public async Task Recall_output_hides_secrets()
    {
        var at = Round(_invoice, "set the key", "export OPENAI_API_KEY=sk-abcdefghijklmnopqrstuvwxyz0123456789", DateTimeOffset.UtcNow);

        var text = await Ask(("action", "read"), ("round", at));

        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz0123456789", text);
        Assert.Contains("set the key", text);
    }
}

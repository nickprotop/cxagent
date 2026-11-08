using System.Text.Json;
using CxAgent.Core.Colleagues;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Where knowledge belongs and how it is known: a round says where it read, and the distiller is given four general
/// principles — how each statement is known, that absence stays concluded, that newer evidence wins, and where each
/// statement is filed (user-approved wording, 2026-11-08). The colleague's briefing says how to weigh the marks.
/// </summary>
public sealed class DistillerPrinciplesTests : IDisposable
{
    private readonly string _root = Core.Permissions.PermissionPolicy.RealFolder(Directory.CreateTempSubdirectory("principles-").FullName);
    private readonly string _config;
    private readonly string _session;
    private readonly string _workplace;
    private readonly string _worktree;
    private readonly MockLlmProvider _model = new();

    public DistillerPrinciplesTests()
    {
        _config = Path.Combine(_root, "cfg");
        _session = Path.Combine(_root, "invoice");
        _workplace = Path.Combine(_root, "consoleex");
        _worktree = Path.Combine(_root, "consoleex-wt");
        foreach (var d in new[] { _config, _session, _workplace, _worktree, Path.Combine(_root, "opt", "lib") }) Directory.CreateDirectory(d);
        // A WORKTREE OF THE WORKPLACE'S REPOSITORY: its .git file leads to the main checkout's, one codebase.
        Directory.CreateDirectory(Path.Combine(_workplace, ".git", "worktrees", "wt"));
        File.WriteAllText(Path.Combine(_workplace, ".git", "worktrees", "wt", "commondir"), "../..");
        File.WriteAllText(Path.Combine(_worktree, ".git"), $"gitdir: {Path.Combine(_workplace, ".git", "worktrees", "wt")}");
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        Library.Update("Charting", r => r with { Workplaces = [new Workplace(Codebase.Key(_workplace), _workplace, 1, DateTimeOffset.UtcNow)] });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private static ToolCall Call(string name, object args) =>
        new() { Id = Guid.NewGuid().ToString("N"), Name = name, Arguments = JsonSerializer.SerializeToElement(args) };

    [Fact]
    public void Reads_are_sorted_into_workplaces_and_elsewhere()
    {
        var round = new List<ChatMessage>
        {
            new() { Role = "assistant", Content = "", ToolCalls =
            [
                Call("read_file", new { path = Path.Combine(_session, "invoice.py") }),
                Call("read_file", new { path = Path.Combine(_workplace, "a.cs") }),
                Call("grep", new { path = Path.Combine(_worktree, "b.cs"), pattern = "x" }),
                Call("read_file", new { path = Path.Combine(_root, "opt", "lib", "c.h") }),
                Call("run_shell", new { command = $"cat {Path.Combine(_root, "opt", "lib", "d.h")}" }),
                Call("read_file", new { path = "bad\0path" }),
            ] },
        };

        var reads = ColleagueDesk.ReadsOf(new RoundReadsRequest(round, Library.Find("Charting")!, _session));

        Assert.Equal([_workplace], reads.Workplaces);
        Assert.Equal([Path.Combine(_root, "opt", "lib")], reads.Elsewhere);
    }

    [Fact]
    public void A_round_names_where_it_read()
    {
        var round = new PastRound(4, "S", _session, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [new ChatMessage { Role = "user", Content = "x" }])
        {
            AlsoRead = [_workplace],
            ReadElsewhere = ["/home/dev/.cache/pkg"],
        };

        var text = PastRounds.Render(round);

        Assert.Contains(_workplace, text);
        Assert.Contains("/home/dev/.cache/pkg", text);
        Assert.Contains("outside every codebase", text);
    }

    [Fact]
    public void The_instructions_hold_the_principles()
    {
        var text = Distiller.InstructionsForTest;

        Assert.Contains("(seen, round N)", text);
        Assert.Contains("(concluded, round N)", text);
        Assert.Contains("absence is the easiest thing to misread", text);
        Assert.Contains("Newer evidence wins", text);
        Assert.Contains("is not new evidence", text);
        Assert.Contains("You can write only the core and the codebases listed below", text);
        Assert.Contains("in the core only if it holds generally", text);
        foreach (var specific in new[] { "CS0", "C#", "Python", "grep", "NuGet" })
            Assert.DoesNotContain(specific, text);
    }

    [Fact]
    public void The_old_correction_line_is_replaced() =>
        Assert.DoesNotContain("When a round shows an earlier statement was wrong, correct it", Distiller.InstructionsForTest);

    [Fact]
    public void Both_experiences_have_a_short_Corrected_section()
    {
        var text = Distiller.InstructionsForTest;

        Assert.Contains("## How you work\n## Lessons\n## Corrected\n## Codebases", text);
        Assert.Contains("## Open threads\n## Corrected", text);
        Assert.Contains("only the few corrections that still matter", text);
    }

    private async Task<DistillOutcome> Distil(string answer)
    {
        Library.Append("Charting", new PastRound(0, "S", _session, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new ChatMessage { Role = "user", Content = "chart it" }, new ChatMessage { Role = "assistant", Content = new string('c', 600) }]));
        _model.EnqueueResponse(new LlmResponse { Text = answer, StopReason = "end_turn" });
        return await new Distiller(Library).DrainAllAsync(_ => _ => _model, CancellationToken.None);
    }

    [Fact]
    public async Task An_answer_without_marks_is_kept()
    {
        Assert.Equal(DistillOutcome.Done, await Distil("<core>\n## Lessons\nPanes first.\n</core>"));

        Assert.Contains("Panes first.", Library.ReadExperience("Charting").Core);
    }

    /// <summary>
    /// A part for a codebase the round did not list is dropped, and an answer with nothing else is "no change", not an
    /// unreadable answer: refused three times, the round would be stepped past for a part it was told it cannot write.
    /// </summary>
    [Fact]
    public async Task A_part_for_an_unlisted_codebase_is_dropped_not_failed()
    {
        Assert.Equal(DistillOutcome.Done, await Distil("<codebase key=\"somewhere-else-12345678\">\nThree panes.\n</codebase>"));

        Assert.Empty(Library.Undistilled("Charting"));
        Assert.Equal(0, Library.Find("Charting")!.FailingRound);
        Assert.DoesNotContain("Three panes.", Library.ReadExperience("Charting").Core);
    }

    [Fact]
    public void The_briefing_explains_the_marks()
    {
        var briefing = IncarnationBriefing.For(Library.Find("Charting")!, "", new IncarnationSetting(true, []));

        Assert.Contains("seen in a round, or concluded from what was seen", briefing);
        Assert.Contains("check what was concluded before you build on it", briefing);
        Assert.Contains("including the round behind any statement", briefing);
    }
}

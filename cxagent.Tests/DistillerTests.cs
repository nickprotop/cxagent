using CxAgent.Core.Colleagues;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Folding a colleague's rounds into its experience: what the distiller is asked, what it may answer, and that no
/// answer — wrong, oversized, secret or absent — ever damages what was there before.
/// </summary>
public sealed class DistillerTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("distiller-").FullName;
    private readonly string _project = Directory.CreateTempSubdirectory("distiller-project-").FullName;
    private readonly ColleagueStore _store;
    private readonly MockLlmProvider _model = new();

    public DistillerTests()
    {
        _store = new ColleagueStore(_config);
        _store.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
    }

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private string Key => Codebase.Key(_project);

    private long Append(string answer = "I laid the chart out in three panes and wired the legend to the series list.",
        bool toolCall = true, string? folder = null, IReadOnlyList<string>? alsoRead = null)
    {
        var messages = new List<ChatMessage> { new() { Role = "user", Content = "lay the chart out" } };
        if (toolCall)
        {
            messages.Add(new ChatMessage { Role = "assistant", Content = "", ToolCalls = [new ToolCall { Id = "k", Name = "read_file", Arguments = System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone() }] });
            messages.Add(new ChatMessage { Role = "tool", ToolCallId = "k", Content = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"line {i}")) });
        }
        messages.Add(new ChatMessage { Role = "assistant", Content = answer });
        return _store.Append("Charting", new PastRound(0, "s1", folder ?? _project, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, messages)
        {
            AlsoRead = alsoRead ?? [],
        });
    }

    private void Answer(string text, string stop = "end_turn") => _model.EnqueueResponse(new LlmResponse { Text = text, StopReason = stop });

    private string Distilled(string core, string codebase, string description = "Lays out charts") =>
        $"<core>\n{core}\n</core>\n<codebase key=\"{Key}\">\n{codebase}\n</codebase>\n<description>{description}</description>";

    private Task<DistillOutcome> Drain() => Drain(new Distiller(_store));

    private Task<DistillOutcome> Drain(Distiller distiller) => distiller.DrainAllAsync(_ => _ => _model, CancellationToken.None);

    [Fact]
    public async Task A_round_is_folded_into_core_and_its_codebase()
    {
        Append();
        Answer(Distilled("## How you work\nPanes first.", "## Architecture\nThree panes."));

        Assert.Equal(DistillOutcome.Done, await Drain());

        var experience = _store.ReadExperience("Charting");
        Assert.Equal("## How you work\nPanes first.", experience.Core);
        Assert.Equal("## Architecture\nThree panes.", experience.Codebases[Key]);
        var record = _store.Find("Charting")!;
        Assert.Equal("Lays out charts", record.Description);
        Assert.Equal(1, record.DistilledThrough);
        Assert.Equal(1, record.ExperienceVersion);
        Assert.Equal([_project], record.GrownIn);

        // THE ROUND, WITH ITS TOOL RESULT CLIPPED: the full text stays in the past.
        var prompt = string.Join("\n", _model.LastMessages!.Select(m => m.Content));
        Assert.Contains("line 20", prompt);
        Assert.DoesNotContain("line 21\n", prompt);
        Assert.Contains("## Lessons", prompt);
        Assert.Contains("## Open threads", prompt);
    }

    [Fact]
    public async Task No_change_advances_the_watermark_and_writes_nothing()
    {
        Append();
        Answer("NO CHANGE");

        Assert.Equal(DistillOutcome.Done, await Drain());

        Assert.Equal("", _store.ReadExperience("Charting").Core);
        Assert.Equal(1, _store.Find("Charting")!.DistilledThrough);
        Assert.Equal(0, _store.Find("Charting")!.ExperienceVersion);
    }

    [Fact]
    public async Task A_trivial_round_is_skipped_without_a_call()
    {
        Append("ok", toolCall: false);

        Assert.Equal(DistillOutcome.Done, await Drain());

        Assert.Equal(0, _model.ChatCallCount);
        Assert.Equal(1, _store.Find("Charting")!.DistilledThrough);
    }

    [Fact]
    public async Task Over_cap_is_asked_once_to_shorten()
    {
        Append();
        Answer(Distilled(new string('x', ExperienceBudget.CoreCap * 4 + 400), "## Architecture\nPanes."));
        Answer(Distilled("## How you work\nShort.", "## Architecture\nPanes."));

        Assert.Equal(DistillOutcome.Done, await Drain());

        Assert.Equal(2, _model.ChatCallCount);
        Assert.Equal("## How you work\nShort.", _store.ReadExperience("Charting").Core);
    }

    [Fact]
    public async Task Over_cap_twice_keeps_the_previous_experience()
    {
        _store.WriteCore("Charting", "before");
        Append();
        var big = Distilled(new string('x', ExperienceBudget.CoreCap * 4 + 400), "## Architecture\nPanes.");
        Answer(big);
        Answer(big);
        string? why = null;
        var distiller = new Distiller(_store);
        distiller.Failed += (_, w) => why = w;

        Assert.Equal(DistillOutcome.Failed, await Drain(distiller));

        Assert.Equal("before", _store.ReadExperience("Charting").Core);
        Assert.Single(_store.Undistilled("Charting"));
        Assert.NotNull(why);
    }

    [Fact]
    public async Task A_secret_keeps_the_previous_and_reports()
    {
        _store.WriteCore("Charting", "before");
        Append();
        Answer(Distilled("The key is sk-ant-api03-abcdefghijklmnopqrstuvwxyz", "## Architecture\nPanes."));
        string? why = null;
        var distiller = new Distiller(_store);
        distiller.Failed += (_, w) => why = w;

        Assert.Equal(DistillOutcome.Failed, await Drain(distiller));

        Assert.Equal("before", _store.ReadExperience("Charting").Core);
        Assert.Contains("secret", why);
    }

    [Fact]
    public async Task A_thrown_call_keeps_the_previous()
    {
        _store.WriteCore("Charting", "before");
        Append();
        // NOTHING QUEUED: the mock throws, as an endpoint that is down does.

        Assert.Equal(DistillOutcome.Failed, await Drain());

        Assert.Equal("before", _store.ReadExperience("Charting").Core);
        Assert.Single(_store.Undistilled("Charting"));
    }

    [Fact]
    public async Task An_unreadable_answer_keeps_the_previous()
    {
        Append();
        Answer("Sure! Here is what I learned: charts are nice.");

        Assert.Equal(DistillOutcome.Failed, await Drain());

        Assert.Single(_store.Undistilled("Charting"));
    }

    [Fact]
    public async Task A_failed_round_is_folded_in_with_the_next()
    {
        Append("I laid the chart out in three panes — the FIRST round.");
        Assert.Equal(DistillOutcome.Failed, await Drain());

        Append("I moved the legend under the plot — the SECOND round.");
        Answer(Distilled("## How you work\nBoth.", "## Architecture\nBoth."));
        Assert.Equal(DistillOutcome.Done, await Drain());

        var prompt = string.Join("\n", _model.LastMessages!.Select(m => m.Content));
        Assert.Contains("FIRST round", prompt);
        Assert.Contains("SECOND round", prompt);
        Assert.Equal(2, _store.Find("Charting")!.DistilledThrough);
    }

    [Fact]
    public async Task A_second_drainer_finds_the_lock_busy()
    {
        Append();
        using var held = DistillingLock.TryTake(_store.Root);
        Assert.NotNull(held);

        Assert.Equal(DistillOutcome.Busy, await Drain());
        Assert.Equal(0, _model.ChatCallCount);
    }

    [Fact]
    public void A_stale_lock_file_is_no_lock()
    {
        Directory.CreateDirectory(_store.Root);
        File.WriteAllText(Path.Combine(_store.Root, "distilling.lock"), "999999");

        using var taken = DistillingLock.TryTake(_store.Root);

        Assert.NotNull(taken);
    }

    /// <summary>
    /// ANOTHER PROCESS HOLDS THE LOCK, THEN DIES: held, it keeps this one out; dead, it does not. A child dotnet process
    /// takes the lock through the same API and is killed holding it.
    /// </summary>
    [Fact]
    public async Task A_holder_that_dies_is_taken_over()
    {
        Directory.CreateDirectory(_store.Root);
        var lockPath = Path.Combine(_store.Root, "distilling.lock");
        // flock, as .NET's FileShare.None takes on Linux and macOS: the holder here is a plain process, killed outright.
        // --close KEEPS THE LOCK WITH flock ITSELF: without it the sleep it runs inherits the locked file, and a kill
        // that reaches flock before its child leaves the lock held by a process this test never waited for.
        if (OperatingSystem.IsWindows()) return;
        using var holder = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("flock",
            ["--exclusive", "--close", lockPath, "sleep", "60"]) { RedirectStandardError = true })!;
        try
        {
            for (var i = 0; i < 50 && DistillingLock.TryTake(_store.Root) is { } early; i++)
            {
                early.Dispose();
                await Task.Delay(100);
            }
            Assert.Null(DistillingLock.TryTake(_store.Root));

            holder.Kill(entireProcessTree: true);
            await holder.WaitForExitAsync();

            using var taken = DistillingLock.TryTake(_store.Root);
            Assert.NotNull(taken);
        }
        finally
        {
            if (!holder.HasExited) holder.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task A_round_that_read_another_workplace_is_distilled_into_both()
    {
        var library = Directory.CreateTempSubdirectory("distiller-library-").FullName;
        try
        {
            var libraryKey = Codebase.Key(library);
            Append(alsoRead: [library]);
            Answer($"<core>\nCore.\n</core>\n<codebase key=\"{Key}\">\nApp.\n</codebase>\n<codebase key=\"{libraryKey}\">\nLibrary.\n</codebase>\n<description>Bridges</description>");

            Assert.Equal(DistillOutcome.Done, await Drain());

            var experience = _store.ReadExperience("Charting");
            Assert.Equal("App.", experience.Codebases[Key]);
            Assert.Equal("Library.", experience.Codebases[libraryKey]);
            Assert.Contains(libraryKey, string.Join("\n", _model.LastMessages!.Select(m => m.Content)));
        }
        finally
        {
            Directory.Delete(library, recursive: true);
        }
    }

    /// <summary>
    /// A live drive's local model answered the core and the description with "..." — the template's own placeholder.
    /// A part with no words in it was not given: what was there stays.
    /// </summary>
    [Fact]
    public async Task A_placeholder_part_keeps_what_was_there()
    {
        _store.WriteCore("Charting", "## Lessons\nPanes first.");
        _store.Update("Charting", r => r with { Description = "Lays out charts" });
        Append();
        Answer($"<core>\n...\n</core>\n<codebase key=\"{Key}\">\n## Architecture\nThree panes.\n</codebase>\n<description>…</description>");

        Assert.Equal(DistillOutcome.Done, await Drain());

        Assert.Equal("## Lessons\nPanes first.", _store.ReadExperience("Charting").Core);
        Assert.Equal("Lays out charts", _store.Find("Charting")!.Description);
        Assert.Equal("## Architecture\nThree panes.", _store.ReadExperience("Charting").Codebases[Key]);
    }

    [Fact]
    public async Task The_answer_form_shows_no_placeholder_to_copy()
    {
        Append();
        Answer("NO CHANGE");

        await Drain();

        Assert.DoesNotContain("...", _model.LastMessages!.First(m => m.Role == "system").Content);
    }

    /// <summary>
    /// THE HOLDER DRAINS EVERY COLLEAGUE: another colleague's drain that found the lock busy relies on it, and a
    /// colleague never used again would otherwise keep a round nobody learns from.
    /// </summary>
    [Fact]
    public async Task A_drain_takes_every_colleague_s_waiting_rounds()
    {
        _store.Create(new ColleagueRecord { Name = "Parser", Type = "general", Created = DateTimeOffset.UtcNow });
        Append();
        _store.Append("Parser", new PastRound(0, "s2", _project, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new ChatMessage { Role = "user", Content = "parse it" }, new ChatMessage { Role = "assistant", Content = new string('p', 400) }]));
        Answer("NO CHANGE");
        Answer("NO CHANGE");

        Assert.Equal(DistillOutcome.Done, await Drain());

        Assert.Empty(_store.Undistilled("Charting"));
        Assert.Empty(_store.Undistilled("Parser"));
    }

    [Fact]
    public async Task One_colleague_s_failure_does_not_stop_another_s()
    {
        _store.Create(new ColleagueRecord { Name = "Parser", Type = "general", Created = DateTimeOffset.UtcNow });
        Append();
        _store.Append("Parser", new PastRound(0, "s2", _project, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new ChatMessage { Role = "user", Content = "parse it" }, new ChatMessage { Role = "assistant", Content = new string('p', 400) }]));
        var models = new Dictionary<string, ILlmProvider?> { ["Charting"] = null, ["Parser"] = _model };
        Answer("NO CHANGE");

        Assert.Equal(DistillOutcome.Failed, await new Distiller(_store).DrainAllAsync(name => _ => models[name], CancellationToken.None));

        Assert.Single(_store.Undistilled("Charting"));
        Assert.Empty(_store.Undistilled("Parser"));
    }

    [Theory]
    [InlineData("<core>\n...\n</core>\n<description>…</description>")]
    [InlineData("<codebase key=\"invented-12345678\">\nSomething.\n</codebase>")]
    public async Task Tags_with_nothing_usable_fail_and_the_round_waits(string answer)
    {
        Append();
        Answer(answer);

        Assert.Equal(DistillOutcome.Failed, await Drain());

        Assert.Single(_store.Undistilled("Charting"));
    }

    [Fact]
    public async Task No_change_to_a_shorten_request_fails_and_the_round_waits()
    {
        Append();
        Answer(Distilled(new string('x', ExperienceBudget.CoreCap * 4 + 400), "## Architecture\nPanes."));
        Answer("NO CHANGE");

        Assert.Equal(DistillOutcome.Failed, await Drain());

        Assert.Single(_store.Undistilled("Charting"));
        Assert.Equal("", _store.ReadExperience("Charting").Core);
    }

    [Theory]
    [InlineData("max_tokens")]
    [InlineData("length")]
    public async Task A_cut_off_answer_fails_and_writes_nothing(string stop)
    {
        Append();
        Answer(Distilled("## How you work\nPanes first.", "## Architecture\nThree"), stop);

        Assert.Equal(DistillOutcome.Failed, await Drain());

        Assert.Equal("", _store.ReadExperience("Charting").Core);
        Assert.Single(_store.Undistilled("Charting"));
    }

    [Fact]
    public async Task A_description_is_kept_on_one_line()
    {
        Append();
        Answer(Distilled("## How you work\nPanes.", "## Architecture\nPanes.", "Lays out charts\n\nand legends,   too"));

        await Drain();

        Assert.Equal("Lays out charts and legends, too", _store.Find("Charting")!.Description);
    }

    [Fact]
    public async Task A_store_failure_fails_the_drain_and_says_so()
    {
        // THE EXPERIENCE FOLDER IS A FILE: every write into it throws, as a full disk or a permission change would.
        var experience = Path.Combine(_store.FolderOf("Charting"), "experience");
        Directory.Delete(experience, recursive: true);
        File.WriteAllText(experience, "not a folder");
        Append();
        Answer(Distilled("## How you work\nPanes.", "## Architecture\nPanes."));
        string? why = null;
        var distiller = new Distiller(_store);
        distiller.Failed += (_, w) => why = w;

        Assert.Equal(DistillOutcome.Failed, await Drain(distiller));

        Assert.NotNull(why);
        Assert.Single(_store.Undistilled("Charting"));
    }

    [Fact]
    public async Task A_codebase_the_round_did_not_touch_is_never_written()
    {
        Append();
        Answer($"<core>\nCore.\n</core>\n<codebase key=\"elsewhere-12345678\">\nInvented.\n</codebase>\n<description>x</description>");

        Assert.Equal(DistillOutcome.Done, await Drain());

        Assert.DoesNotContain("elsewhere-12345678", _store.ReadExperience("Charting").Codebases.Keys);
    }
}

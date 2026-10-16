using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using CxAgent.Headless;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>A whole headless run against a scripted model, through the same path the binary takes.</summary>
public class HeadlessRunTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hrun-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;
    private readonly string _config;

    public HeadlessRunTests()
    {
        _work = Path.Combine(_dir, "work");
        _config = Path.Combine(_dir, "config");
        Directory.CreateDirectory(_work);
        Directory.CreateDirectory(_config);
        File.WriteAllText(Path.Combine(_work, "notes.txt"), "the word is heron");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } }

    private sealed record Ran(int Code, string Out, string Err);

    private IReadOnlyDictionary<string, PluginConfig> _plugins = new Dictionary<string, PluginConfig>();

    private async Task<Ran> Run(ILlmProvider model, string? stdin, params string[] args)
    {
        var options = CommandLine.Parse(args);
        Assert.Null(options.Error);
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await HeadlessRun.RunAsync(new HeadlessRequest(options, new AppPaths(_config), _work,
            new GoalInputSource(stdin is not null, new StringReader(stdin ?? "")),
            new HeadlessIo(o, e), new HeadlessSignals(CancellationToken.None, () => false))
        {
            ResolutionForTest = ResolvedConfig.ForTesting(model).WithPlugins(_plugins),
        });
        return new Ran(code, o.ToString(), e.ToString());
    }

    private static MockLlmProvider Says(params string[] answers)
    {
        var m = new MockLlmProvider("m");
        foreach (var a in answers) m.EnqueueResponse(new LlmResponse { Text = a, StopReason = "end_turn" });
        return m;
    }

    [Fact]
    public async Task Thinking_ALevelTheModelDoesNotOffer_IsAUsageError()
    {
        var model = new LevelledMock(["low", "high"]);
        var (code, _, err) = await Run(model, null, "-p", "x", "--thinking", "max");

        Assert.Equal(2, code);
        Assert.Contains("low, high", err);
        Assert.Empty(model.State.Seen);
    }

    // NO ANSWER IS NOT A WRONG LEVEL. The level may well be offered; the model was not reachable to
    // say so — a failure worth retrying, exit 1, not a usage error to fix in the command line.
    [Fact]
    public async Task Thinking_WhenTheModelDidNotAnswer_FailsWithExit1_SayingSo()
    {
        var model = new LevelledMock(["low", "high"]);
        model.State.Unanswered = true;
        var (code, _, err) = await Run(model, null, "-p", "x", "--thinking", "high");

        Assert.Equal(1, code);
        Assert.Contains("did not answer", err);
        Assert.Empty(model.State.Seen);
    }

    [Fact]
    public async Task Thinking_AnOfferedLevel_IsWhatTheRequestRunsAt()
    {
        var model = new LevelledMock(["low", "high"]);
        var (code, _, _) = await Run(model, null, "-p", "x", "--thinking", "high");

        Assert.Equal(0, code);
        Assert.Equal("high", model.LastLevel);
    }

    // THE CALLER'S OWN FLAG IS NOT NEWS: echoing it would put a line on stderr in the JSON modes,
    // where a consumer reads stderr as something going wrong.
    [Theory]
    [InlineData("json")]
    [InlineData("stream-json")]
    [InlineData("text")]
    public async Task Thinking_AnOfferedLevel_IsAppliedWithoutANotice(string format)
    {
        var model = new LevelledMock(["low", "high"]);
        var (code, _, err) = await Run(model, null, "-p", "x", "--thinking", "high",
            "--output-format", format, "--verbose");

        Assert.Equal(0, code);
        Assert.Equal("high", model.LastLevel);
        Assert.DoesNotContain("Thinking set to", err);
    }

    [Fact]
    public async Task Text_PrintsTheAnswer_AndExitsZero()
    {
        var (code, output, _) = await Run(Says("hello there"), null, "-p", "say hello");

        Assert.Equal(0, code);
        Assert.Equal("hello there", output.Trim());
    }

    [Fact]
    public async Task Json_IsTheResultObject_WithTheSessionId()
    {
        var (code, output, _) = await Run(Says("done"), null, "-p", "x", "--output-format", "json");

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("success", doc.RootElement.GetProperty("subtype").GetString());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("session_id").GetString()));
    }

    /// <summary>A goal is always a goal: "/clear" goes to the model as text.</summary>
    [Fact]
    public async Task ASlashGoal_ReachesTheModel_AsText()
    {
        var model = Says("that would clear it");
        var (code, _, _) = await Run(model, null, "-p", "/clear");

        Assert.Equal(0, code);
        Assert.Contains(model.LastMessages!, m => m.Role == "user" && m.Content.Contains("/clear"));
    }

    [Fact]
    public async Task PipedInput_IsAttachedUnderTheGoal()
    {
        var model = Says("reviewed");
        await Run(model, "diff --git a b", "-p", "review this");

        Assert.Contains(model.LastMessages!, m => m.Role == "user"
            && m.Content.Contains("review this") && m.Content.Contains("diff --git a b"));
    }

    /// <summary>--max-turns ends the run at that turn: error_max_turns, exit 3.</summary>
    [Fact]
    public async Task MaxTurns_StopsTheRun_WithExit3()
    {
        var model = new MockLlmProvider("m");
        for (var i = 0; i < 3; i++)
            model.EnqueueResponse(new LlmResponse
            {
                Text = "",
                ToolCalls = [new CxAgent.Core.Models.ToolCall
                {
                    Id = $"g{i}", Name = "glob",
                    Arguments = JsonSerializer.SerializeToElement(new { pattern = $"*{i}" }),
                }],
                StopReason = "tool_use",
                Usage = new LlmUsage(),
            });
        model.EnqueueResponse(new LlmResponse { Text = "partial summary", StopReason = "end_turn" });

        var (code, output, _) = await Run(model, null, "-p", "x", "--max-turns", "1", "--trust",
            "--output-format", "json");

        Assert.Equal(3, code);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("error_max_turns", doc.RootElement.GetProperty("subtype").GetString());
    }

    /// <summary>num_turns counts model turns, not tool calls: a tool turn and an answer are two.</summary>
    [Fact]
    public async Task NumTurns_CountsTurns_NotToolCalls()
    {
        var model = new MockLlmProvider("m");
        model.EnqueueResponse(new LlmResponse
        {
            Text = "",
            ToolCalls =
            [
                new CxAgent.Core.Models.ToolCall { Id = "a", Name = "glob", Arguments = JsonSerializer.SerializeToElement(new { pattern = "*.txt" }) },
                new CxAgent.Core.Models.ToolCall { Id = "b", Name = "glob", Arguments = JsonSerializer.SerializeToElement(new { pattern = "*.md" }) },
                new CxAgent.Core.Models.ToolCall { Id = "c", Name = "glob", Arguments = JsonSerializer.SerializeToElement(new { pattern = "*.cs" }) },
            ],
            StopReason = "tool_use",
            Usage = new LlmUsage(),
        });
        model.EnqueueResponse(new LlmResponse { Text = "found them", StopReason = "end_turn" });

        var (_, output, _) = await Run(model, null, "-p", "x", "--trust", "--output-format", "json");

        using var doc = JsonDocument.Parse(output);
        Assert.Equal(2, doc.RootElement.GetProperty("num_turns").GetInt32());
    }

    /// <summary>
    /// A USAGE ERROR MUST NOT COST THE CONVERSATION. A mistyped --plugin on a -c run exits 2; the
    /// session it meant to continue is still the one the next -c finds.
    /// </summary>
    [Fact]
    public async Task AUsageErrorOnContinue_LeavesTheSessionToContinue()
    {
        await Run(Says("step one done"), null, "-p", "plan it");

        var (code, _, _) = await Run(Says("never"), null, "-c", "-p", "next", "--plugin", "typo");
        Assert.Equal(2, code);

        var third = Says("step two done");
        await Run(third, null, "-c", "-p", "next");
        Assert.Contains(third.LastMessages!, m => m.Content.Contains("step one done"));
    }

    /// <summary>
    /// STDIN THAT NEVER ENDS DOES NOT OUTLAST A SIGNAL. `ssh host cxagent -p …` without -n leaves
    /// stdin open forever; Ctrl+C must still end the run.
    /// </summary>
    [Fact]
    public async Task AnOpenStdin_IsAbandonedOnASignal()
    {
        using var cancel = new CancellationTokenSource();
        var o = new StringWriter();
        var e = new StringWriter();
        var run = HeadlessRun.RunAsync(new HeadlessRequest(CommandLine.Parse(["-p", "x"]), new AppPaths(_config), _work,
            new GoalInputSource(true, new NeverEndingReader()),
            new HeadlessIo(o, e), new HeadlessSignals(cancel.Token, () => false))
        {
            ResolutionForTest = ResolvedConfig.ForTesting(Says("x")),
        });
        await Task.Delay(200);
        cancel.Cancel();

        Assert.Equal(130, await run.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class NeverEndingReader : TextReader
    {
        public override int Read(char[] buffer, int index, int count)
        {
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
    }

    [Fact]
    public async Task NothingToRun_IsExit2()
    {
        var (code, _, err) = await Run(Says(), null, "-p");

        Assert.Equal(2, code);
        Assert.Contains("nothing to run", err);
    }

    [Fact]
    public async Task AProviderFailure_IsExit1()
    {
        var (code, _, _) = await Run(new MockLlmProvider("m"), null, "-p", "x");   // empty queue throws

        Assert.Equal(1, code);
    }

    /// <summary>The run closes its session, and -c finds it: a script chains goals one at a time.</summary>
    [Fact]
    public async Task Continue_PicksUpTheRunBefore()
    {
        var first = Says("step one done");
        await Run(first, null, "-p", "plan it");

        var second = Says("step two done");
        var (code, _, _) = await Run(second, null, "-c", "-p", "next");

        Assert.Equal(0, code);
        Assert.Contains(second.LastMessages!, m => m.Content.Contains("step one done"));
    }

    [Fact]
    public async Task Continue_WithNothingHere_IsExit2()
    {
        var (code, _, err) = await Run(Says("x"), null, "-c", "-p", "next");

        Assert.Equal(2, code);
        Assert.Contains("Nothing to continue", err);
    }

    [Fact]
    public async Task AskUser_IsNotOffered()
    {
        var model = Says("ok");
        await Run(model, null, "-p", "x");

        Assert.DoesNotContain(model.LastTools ?? [], t => t.Name == "ask_user");
    }

    [Fact]
    public async Task ANamedPluginThatIsNotConfigured_IsExit2()
    {
        var (code, _, err) = await Run(Says("x"), null, "-p", "x", "--plugin", "nope");

        Assert.Equal(2, code);
        Assert.Contains("nope", err);
    }

    [Fact]
    public async Task Trust_IsNotStored()
    {
        await Run(Says("x"), null, "-p", "x", "--trust");

        var permissions = Path.Combine(_config, "permissions.json");
        Assert.True(!File.Exists(permissions) || !File.ReadAllText(permissions).Contains("Trusted"));
    }

    private static JsonElement Init(string streamed) =>
        JsonDocument.Parse(streamed.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]).RootElement;

    /// <summary>The init event lists what the model is offered, as Claude Code's does.</summary>
    [Fact]
    public async Task StreamInit_ListsTheOfferedTools()
    {
        var (_, output, _) = await Run(Says("ok"), null, "-p", "x", "--output-format", "stream-json");

        var tools = Init(output).GetProperty("tools").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Contains("read_file", tools);
        Assert.DoesNotContain("ask_user", tools);
    }

    /// <summary>A plugin that was chosen but would not load is skipped, with why — never listed as loaded.</summary>
    [Fact]
    public async Task APluginThatFailsToLoad_IsSkipped_NotLoaded()
    {
        var folder = Path.Combine(_work, ".cxagent", "plugins");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "broken.dll"), "not an assembly");
        _plugins = new Dictionary<string, PluginConfig> { ["broken"] = new("broken.dll") };

        var (_, output, _) = await Run(Says("ok"), null,
            "-p", "x", "--plugin", "broken", "--output-format", "stream-json");

        var init = Init(output);
        Assert.Empty(init.GetProperty("plugins").EnumerateArray());
        Assert.Contains(init.GetProperty("cxagent").GetProperty("skipped").GetProperty("plugins").EnumerateArray(),
            s => s.GetString()!.StartsWith("broken ("));
    }

    /// <summary>A provider's failure is said once on stderr, not again as the run's closing error.</summary>
    [Fact]
    public async Task AProviderFailure_IsPrintedOnce()
    {
        var (_, _, err) = await Run(new MockLlmProvider("m"), null, "-p", "x");   // empty queue throws

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(err, "Queue empty"));
    }

    private void CommandFile(string name, string text)
    {
        var folder = Path.Combine(_work, ".cxagent", "commands");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".md"), text);
    }

    [Fact]
    public async Task ACustomCommand_Expands_InAHeadlessRun()
    {
        CommandFile("review", "Review PR $1.");
        var model = Says("reviewed");

        var (code, output, _) = await Run(model, null, "-p", "/review 7");

        Assert.Equal(0, code);
        Assert.Equal("reviewed", output.Trim());
        Assert.Contains(model.LastMessages!, m => m.Role == "user" && m.Content == "Review PR 7.");
    }

    [Fact]
    public async Task PipedInput_GoesUnderTheExpansion_NotIntoArguments()
    {
        CommandFile("summarise", "Summarise: $ARGUMENTS");
        var model = Says("ok");

        await Run(model, "line one\nline two", "-p", "/summarise briefly");

        var sent = model.LastMessages!.Last(m => m.Role == "user").Content;
        Assert.StartsWith("Summarise: briefly", sent);
        Assert.Contains("line one", sent);
    }

    /// <summary>A shell line that would prompt is refused headless and listed as a denial.</summary>
    [Fact]
    public async Task AShellLineThatWouldPrompt_IsRefused_AndListed()
    {
        // NOT /diff: that is a built-in, which wins over a file of its name and goes to the model as text.
        CommandFile("changes", "Changes:\n!`git diff`");
        var model = Says("ok");

        var (_, output, _) = await Run(model, null, "-p", "/changes", "--output-format", "json");

        using var doc = JsonDocument.Parse(output);
        Assert.NotEmpty(doc.RootElement.GetProperty("permission_denials").EnumerateArray());
        Assert.Contains(model.LastMessages!, m => m.Content.Contains("(not run: refused)"));
    }

    /// <summary>A file named like a built-in does not run headless either: /clear is still text.</summary>
    [Fact]
    public async Task AFileNamedLikeABuiltIn_StaysText_InAHeadlessRun()
    {
        CommandFile("clear", "Not the real clear — expanded anyway.");
        var model = Says("ok");

        await Run(model, null, "-p", "/clear");

        Assert.Contains(model.LastMessages!, m => m.Role == "user" && m.Content == "/clear");
    }

    /// <summary>HEADLESS SPAWNS WAIT (spec decision 8): the run's one answer is built from the child's.</summary>
    [Fact]
    public async Task AHeadlessSpawn_RunsInTheForeground()
    {
        var model = new MockLlmProvider("m");
        model.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use",
            ToolCalls = [Calls.Named(Tool.Agent, new { description = "look", prompt = "find the word" }, id: "s1")],
        });
        model.EnqueueResponse(new LlmResponse { Text = "the word is heron", StopReason = "end_turn" });   // the child
        model.EnqueueResponse(new LlmResponse { Text = "It is heron.", StopReason = "end_turn" });        // the parent

        var (code, output, _) = await Run(model, null, "-p", "what is the word?");

        Assert.Equal(0, code);
        Assert.Contains("It is heron.", output);
        Assert.Contains("state=\"completed\"", model.Text());
        Assert.DoesNotContain("state=\"running\"", model.Text());
    }

    /// <summary>
    /// A HEADLESS agent_send WAITS AND ANSWERS, as a headless spawn does: nothing reads a report that
    /// arrives after the run's one answer, so a resume backgrounded here would be a lost answer.
    /// </summary>
    [Fact]
    public async Task AHeadlessAgentSend_WaitsAndAnswers()
    {
        var model = new MockLlmProvider("m");
        model.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use",
            ToolCalls = [Calls.Named(Tool.Agent, new { description = "look", prompt = "find the word" }, id: "s1")],
        });
        model.EnqueueResponse(new LlmResponse { Text = "the word is heron", StopReason = "end_turn" });   // the child
        model.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use",
            ToolCalls = [Calls.Named(Tool.AgentSend, new { to = "look", prompt = "and the next one?" }, id: "s2")],
        });
        model.EnqueueResponse(new LlmResponse { Text = "the next is egret", StopReason = "end_turn" });   // the child again
        model.EnqueueResponse(new LlmResponse { Text = "It is egret.", StopReason = "end_turn" });        // the parent

        var (code, output, _) = await Run(model, null, "-p", "what are the words?");

        Assert.Equal(0, code);
        Assert.Contains("It is egret.", output);
        Assert.Contains("the next is egret", model.Text());
        Assert.DoesNotContain("in the background", model.Text());
    }
}

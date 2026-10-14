using CxAgent.Core.Agents;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// One side question: forked from a published conversation, offered the main tool list, allowed to
/// read and nothing else, and asking permission exactly as the session it was forked from would.
/// </summary>
public class AsideRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aside-" + Guid.NewGuid().ToString("N"));

    public AsideRunnerTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "the secret word is heron");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    private sealed class Seen : IAsideObserver
    {
        public ConcurrentQueue<string> Events { get; } = new();
        public ConcurrentQueue<string> Rows { get; } = new();
        public string Answer = "";
        public AsideEnd? End;
        public string? Detail;
        public void Asked(int aside, string question) => Events.Enqueue($"asked:{question}");
        public void Waiting(int aside, bool waiting) => Events.Enqueue($"waiting:{waiting}");
        public void AnswerAppended(int aside, string text) => Answer += text;
        public void LookedUp(int aside, string row) => Rows.Enqueue(row);
        public void Finished(int aside, AsideEnd end, string? detail) { End = end; Detail = detail; }
    }

    private static readonly IReadOnlyList<ToolDefinition> MainTools =
        ToolBindings.For(Enum.GetValues<BuiltinTool>(), JobRegistry.CreateWithBuiltins()).ToList();

    private static PublishedContext Fork() => new(
    [
        new ChatMessage { Role = "system", Content = "you are the main agent" },
        new ChatMessage { Role = "user", Content = "refactor the loader" },
        new ChatMessage { Role = "assistant", Content = "I split it in two." },
    ], MainTools);

    /// <summary>Stands in for the session's gate: records who asked, and answers as it is told.</summary>
    private sealed class SessionGate(PermissionOutcome answer) : IPermissionGate
    {
        public ConcurrentQueue<PermissionRequest> Asked { get; } = new();

        public Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            Asked.Enqueue(request);
            return Task.FromResult(answer);
        }
    }

    private AsideRunner Runner(MockLlmProvider provider, Seen seen, IPermissionGate? gate = null,
        Action<LlmUsage>? spent = null, Action<AsideRun>? ran = null) =>
        new(1, new AsideRuntime
        {
            Provider = provider, From = Fork(), Observer = seen,
            Executors = JobRegistry.CreateWithBuiltins(null, gate ?? PermissionGate.AllowAll),
            WorkingDir = _root, Spent = spent, Ran = ran,
        });

    private static ToolCall Call(string name, string json, string id = "c1") => new()
    {
        Id = id, Name = name, Arguments = JsonDocument.Parse(json).RootElement,
    };

    private static LlmResponse Calls(params ToolCall[] calls) =>
        new() { ToolCalls = [.. calls], StopReason = "tool_use" };

    private static LlmResponse Says(string text) => new() { Text = text, StopReason = "end_turn" };

    [Fact]
    public async Task TheFork_IsTheMainConversation_ThenOneUserMessage_WithTheNoteAndTheQuestion()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(Says("it reads config"));
        var seen = new Seen();

        await Runner(mock, seen).Ask("what does the loader do?")!;

        var sent = mock.LastMessages!;
        Assert.Equal(Fork().Messages, sent.Take(3));
        Assert.Equal(4, sent.Count);
        Assert.Equal("user", sent[3].Role);
        Assert.Contains("side question", sent[3].Content);
        Assert.EndsWith("what does the loader do?", sent[3].Content);
        Assert.Equal("it reads config", seen.Answer);
        Assert.Equal(AsideEnd.Answered, seen.End);
    }

    /// <summary>The same list, so llama.cpp's cached prefix still matches the fork.</summary>
    [Fact]
    public async Task TheMainToolList_IsOfferedUnchanged()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(Says("ok"));

        await Runner(mock, new Seen()).Ask("q")!;

        Assert.Equal(MainTools.Select(t => t.Name), mock.LastTools!.Select(t => t.Name));
    }

    [Theory]
    [InlineData("write_file", "{\"path\":\"x\",\"content\":\"y\"}")]
    [InlineData("run_shell", "{\"command\":\"ls\"}")]
    [InlineData("agent", "{\"prompt\":\"go\"}")]
    [InlineData("csharp_definition", "{\"symbol\":\"X\"}")]
    [InlineData("mcp__fs__read", "{}")]
    public async Task AnythingButAReadingTool_IsRefusedWithTheNamedReason(string tool, string args)
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(Calls(Call(tool, args)));
        mock.EnqueueResponse(Says("fine"));

        await Runner(mock, new Seen()).Ask("q")!;

        var result = mock.LastMessages!.Last(m => m.Role == "tool");
        Assert.Equal(AsideRunner.Refusal, result.Content);
        Assert.False(File.Exists(Path.Combine(_root, "x")));
    }

    [Fact]
    public async Task ARead_TheSessionAllows_Runs()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(Calls(Call("read_file", $"{{\"path\":\"notes.txt\"}}")));
        mock.EnqueueResponse(Says("heron"));
        var seen = new Seen();

        await Runner(mock, seen).Ask("what is the word?")!;

        Assert.Contains("heron", mock.LastMessages!.Last(m => m.Role == "tool").Content);
        Assert.Single(seen.Rows);
    }

    [Fact]
    public async Task GlobAndGrep_Run()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(Calls(
            Call("glob", "{\"pattern\":\"*.txt\"}", "a"),
            Call("grep", "{\"pattern\":\"heron\"}", "b")));
        mock.EnqueueResponse(Says("found"));

        await Runner(mock, new Seen()).Ask("q")!;

        var results = mock.LastMessages!.Where(m => m.Role == "tool").Select(m => m.Content).ToList();
        Assert.All(results, r => Assert.Contains("notes.txt", r));
    }

    /// <summary>
    /// THE SESSION'S GATE DECIDES, as it does for a sub-agent: its edit mode, its stored rules, its
    /// trust, and its prompt when one is needed. The aside adds no rule of its own; it only names
    /// itself, so a prompt says who is asking.
    /// </summary>
    [Fact]
    public async Task ARead_AsksTheSessionsGate_AsTheAside()
    {
        var gate = new SessionGate(PermissionOutcome.Allow);
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(Calls(Call("read_file", "{\"path\":\"notes.txt\"}")));
        mock.EnqueueResponse(Says("heron"));

        await Runner(mock, new Seen(), gate).Ask("q")!;

        Assert.Equal("aside", Assert.Single(gate.Asked).Requester);
        Assert.Contains("heron", mock.LastMessages!.Last(m => m.Role == "tool").Content);
    }

    /// <summary>A "no" from the session reaches the aside in the words the main agent would read.</summary>
    [Fact]
    public async Task TheSessionsRefusal_IsWhatTheAsideIsTold()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(Calls(Call("read_file", "{\"path\":\"notes.txt\"}")));
        mock.EnqueueResponse(Says("could not"));

        await Runner(mock, new Seen(), new SessionGate(new PermissionOutcome(false))).Ask("q")!;

        var result = mock.LastMessages!.Last(m => m.Role == "tool").Content;
        Assert.DoesNotContain("heron", result);
        Assert.Contains("permission denied by the user", result);
    }

    /// <summary>Eight rounds, then one request told to answer from what it has, whose calls are not run.</summary>
    [Fact]
    public async Task EightRounds_ThenItIsToldToAnswer()
    {
        var mock = new MockLlmProvider();
        for (var i = 0; i < AsideRunner.MaxToolRounds; i++)
            mock.EnqueueResponse(Calls(Call("glob", "{\"pattern\":\"*\"}", $"c{i}")));
        mock.EnqueueResponse(new LlmResponse
        {
            Text = "from what I have", ToolCalls = [Call("glob", "{\"pattern\":\"*\"}", "late")], StopReason = "tool_use",
        });
        var seen = new Seen();

        await Runner(mock, seen).Ask("q")!;

        Assert.Equal(AsideRunner.MaxToolRounds + 1, mock.ChatCallCount);
        Assert.Contains("answer from what you have", mock.LastMessages![^1].Content);
        Assert.Equal(AsideEnd.Capped, seen.End);
        Assert.Equal("from what I have", seen.Answer);
    }

    /// <summary>A follow-up continues the same thread: the second request holds the first answer.</summary>
    [Fact]
    public async Task AFollowUp_ContinuesTheSameThread()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(Says("first answer"));
        mock.EnqueueResponse(Says("second answer"));
        var runner = Runner(mock, new Seen());

        await runner.Ask("one")!;
        await runner.Ask("two")!;

        var sent = mock.LastMessages!;
        Assert.Contains(sent, m => m.Role == "assistant" && m.Content == "first answer");
        Assert.Equal("two", sent[^1].Content);
    }

    [Fact]
    public async Task AskingWhileAnswering_IsRefused()
    {
        var gate = new TaskCompletionSource();
        var runner = new AsideRunner(1, new AsideRuntime
        {
            Provider = new HeldProvider(gate.Task), From = Fork(), Observer = new Seen(), Executors = JobRegistry.CreateWithBuiltins(),
        });

        var first = runner.Ask("one");
        Assert.True(runner.IsAnswering);
        Assert.Null(runner.Ask("two"));

        gate.SetResult();
        await first!;
        Assert.False(runner.IsAnswering);
    }

    [Fact]
    public async Task Stop_EndsTheAnswer_AsStopped()
    {
        var seen = new Seen();
        var runner = new AsideRunner(1, new AsideRuntime
        {
            Provider = new HeldProvider(new TaskCompletionSource().Task), From = Fork(), Observer = seen, Executors = JobRegistry.CreateWithBuiltins(),
        });

        var answering = runner.Ask("q")!;
        runner.Stop();
        await answering.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AsideEnd.Stopped, seen.End);
        Assert.False(runner.IsAnswering);
    }

    [Fact]
    public async Task AProviderError_IsReported_NotThrown()
    {
        var seen = new Seen();
        var runner = new AsideRunner(1, new AsideRuntime
        {
            Provider = new ThrowingProvider(), From = Fork(), Observer = seen, Executors = JobRegistry.CreateWithBuiltins(),
        });

        await runner.Ask("q")!;

        Assert.Equal(AsideEnd.Failed, seen.End);
        Assert.False(string.IsNullOrEmpty(seen.Detail));
    }

    [Fact]
    public async Task Spend_IsReportedPerRequest_AndTheRunOnce()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(Calls(Call("glob", "{\"pattern\":\"*\"}")) with { Usage = new LlmUsage { InputTokens = 10, OutputTokens = 1 } });
        mock.EnqueueResponse(Says("ok") with { Usage = new LlmUsage { InputTokens = 20, OutputTokens = 2 } });
        var spent = new List<LlmUsage>();
        AsideRun? run = null;

        await Runner(mock, new Seen(), null, spent.Add, r => run = r).Ask("q")!;

        Assert.Equal(2, spent.Count);
        Assert.Equal(30, run!.Spent.InputTokens);
        Assert.Equal(1, run.ToolCalls);
        Assert.Equal(AsideEnd.Answered, run.End);
    }

    [Fact]
    public async Task WaitingIsShown_UntilTheFirstChunk()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(Says("ok"));
        var seen = new Seen();

        await Runner(mock, seen).Ask("q")!;

        Assert.Equal(["asked:q", "waiting:True", "waiting:False"], seen.Events.ToArray());
    }

    /// <summary>
    /// AN EMPTY ANSWER IS A FAILURE, NOT A TURN. An empty assistant message shows nothing, and a
    /// provider that requires content on every non-final message rejects every follow-up after it.
    /// </summary>
    [Fact]
    public async Task AnEmptyAnswer_Fails_AndNeverEntersTheThread()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(Says("<think>only thinking</think>"));
        mock.EnqueueResponse(Says("second"));
        var seen = new Seen();
        var runner = Runner(mock, seen);

        await runner.Ask("one")!;

        Assert.Equal(AsideEnd.Failed, seen.End);
        Assert.Contains("no answer", seen.Detail);
        Assert.DoesNotContain(runner.Messages, m => m.Role == "assistant" && string.IsNullOrWhiteSpace(m.Content)
                                                     && m.ToolCalls is not { Count: > 0 });

        await runner.Ask("two")!;
        Assert.DoesNotContain(mock.LastMessages!, m => m.Role == "assistant" && string.IsNullOrWhiteSpace(m.Content)
                                                       && m.ToolCalls is not { Count: > 0 });
    }

    /// <summary>Holds every request until released, or until cancelled.</summary>
    private sealed class HeldProvider(Task release) : ILlmProvider
    {
        public string ProviderId => "held";
        public string DisplayName => "Held";
        public string ModelId => "held-model";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => true;
        public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct) =>
            throw new NotSupportedException();
        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> m, List<ToolDefinition>? t,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await release.WaitAsync(TimeSpan.FromSeconds(20), ct);
            yield return new LlmStreamChunk("released", null, true, StopReason: "end_turn");
        }
    }
}

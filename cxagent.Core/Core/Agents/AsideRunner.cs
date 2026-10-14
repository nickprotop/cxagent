using System.Text;
using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Jobs.Builtin;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;

namespace CxAgent.Core.Agents;

/// <summary>
/// One side question and its follow-ups: a thread of its own, forked from what the main agent last
/// sent, that can read and do nothing else.
/// </summary>
public sealed class AsideRunner
{
    public const int MaxToolRounds = 8;

    public const string Refusal = "not available in a side question — only read_file, glob and grep run here";

    // FIRST QUESTION ONLY, AND IN THE SAME USER MESSAGE AS IT. A second adjacent user turn is one more
    // ordering a provider has to accept; one message says the same thing.
    private const string Note =
        "This is a side question from the user, asked while the conversation above continues "
        + "elsewhere. Treat the conversation as reference. Answer concisely and do not continue the "
        + "main task. Only read_file, glob and grep will run; every other tool is refused.\n\n";

    private const string CapNote =
        "You have used every tool round a side question gets. Do not call tools; answer from what "
        + "you have.";

    // THE THREE THAT ONLY READ, by their built-in identities, which is what InvokeAsync checks a call
    // against. A plugin or MCP tool never reaches it: manifests do not say which of their tools only read.
    private static readonly IReadOnlyList<BuiltinTool> Reading =
        [BuiltinTool.ReadFile, BuiltinTool.ListFiles, BuiltinTool.SearchFiles];

    private static readonly HashSet<string> ReadingNames = ["read_file", "glob", "grep"];

    private readonly AsideRuntime _runtime;
    private readonly List<ChatMessage> _messages;
    private readonly List<ToolDefinition> _tools;
    private CancellationTokenSource? _answering;
    private bool _asked;

    public AsideRunner(int number, AsideRuntime runtime)
    {
        Number = number;
        _runtime = runtime;
        _messages = [.. runtime.From.Messages];

        // NO TOOL LIST IS A RESUMED SESSION'S FORK: what was saved is the conversation, not the
        // request that carried it. Offering only the three that run costs nothing in cache terms — a
        // resumed conversation has no warm prefix on the server to keep.
        _tools = runtime.From.Tools.Count > 0
            ? [.. runtime.From.Tools]
            : [.. ToolBindings.For(Reading, runtime.Executors)];
    }

    public int Number { get; }

    public bool IsAnswering => Volatile.Read(ref _answering) is not null;

    /// <summary>Its own thread, for tests: it must never be the main agent's.</summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>Starts answering, or returns null while an answer is still running.</summary>
    public Task? Ask(string question)
    {
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _answering, cts, null) is not null)
        {
            cts.Dispose();
            return null;
        }

        var text = _asked ? question : Note + question;
        _asked = true;
        _messages.Add(new ChatMessage { Role = "user", Content = text, Timestamp = DateTimeOffset.UtcNow });
        _runtime.Observer.Asked(Number, question);

        return Task.Run(() => AnswerAsync(cts));
    }

    public void Stop()
    {
        try { Volatile.Read(ref _answering)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task AnswerAsync(CancellationTokenSource cts)
    {
        var started = DateTimeOffset.UtcNow;
        var spent = new LlmUsage();
        var rounds = 0;
        var calls = 0;
        var end = AsideEnd.Answered;
        string? detail = null;

        try
        {
            for (; ; rounds++)
            {
                var last = rounds >= MaxToolRounds;
                if (last)
                    _messages.Add(new ChatMessage { Role = "user", Content = CapNote, Timestamp = DateTimeOffset.UtcNow });

                var response = await RequestAsync(cts.Token);
                spent = Sum(spent, response.Usage);
                _runtime.Spent?.Invoke(response.Usage);

                if (response.ToolCalls.Count == 0 || last)
                {
                    var answer = ModelOutput.StripReasoning(response.Text);

                    // NOTHING VISIBLE IS NO ANSWER: all thinking, nothing at all, or a capped round
                    // that called a tool instead of answering. Said, and kept out of the thread — an
                    // empty assistant message shows the user nothing, and a provider that requires
                    // content on every non-final message refuses each follow-up after it.
                    if (string.IsNullOrWhiteSpace(answer))
                    {
                        end = AsideEnd.Failed;
                        detail = "the model returned no answer";
                        break;
                    }

                    _messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = answer,
                        Timestamp = DateTimeOffset.UtcNow,
                    });
                    if (last) end = AsideEnd.Capped;
                    break;
                }

                _messages.Add(new ChatMessage
                {
                    Role = "assistant", Content = response.Text ?? "", ToolCalls = [.. response.ToolCalls],
                });

                var answered = 0;
                try
                {
                    foreach (var call in response.ToolCalls)
                    {
                        var result = await RunAsync(call, cts.Token);
                        _messages.Add(new ChatMessage { Role = "tool", ToolCallId = call.Id ?? call.Name, Content = result });
                        answered++;
                        calls++;
                    }
                }
                catch (OperationCanceledException)
                {
                    // EVERY CALL ANSWERED BEFORE LEAVING, so a follow-up continues a valid thread
                    // rather than one a provider rejects for an orphaned call.
                    foreach (var call in response.ToolCalls.Skip(answered))
                        _messages.Add(new ChatMessage
                        {
                            Role = "tool", ToolCallId = call.Id ?? call.Name,
                            Content = "stopped: the user stopped this answer before the call ran.",
                        });
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            end = AsideEnd.Stopped;
        }
        catch (Exception ex)
        {
            // REPORTED, NOT THROWN: nothing awaits an aside but its own block, and a fault here must
            // never reach the main turn.
            end = AsideEnd.Failed;
            detail = ex.Message;
        }
        finally
        {
            Volatile.Write(ref _answering, null);
            cts.Dispose();
        }

        _runtime.Observer.Finished(Number, end, detail);
        _runtime.Ran?.Invoke(new AsideRun(spent, rounds, calls, end, started,
            (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds));
    }

    private async Task<LlmResponse> RequestAsync(CancellationToken ct)
    {
        var text = new StringBuilder();
        var toolCalls = new List<ToolCall>();
        LlmUsage usage = new();
        var stop = "";
        var shown = 0;
        var first = true;

        _runtime.Observer.Waiting(Number, true);
        try
        {
            await foreach (var chunk in _runtime.Provider.ChatStreamAsync([.. _messages], [.. _tools], ct))
            {
                if (first)
                {
                    first = false;
                    _runtime.Observer.Waiting(Number, false);
                }

            if (!string.IsNullOrEmpty(chunk.TextDelta))
            {
                text.Append(chunk.TextDelta);
                var visible = ModelOutput.StripReasoning(text.ToString());
                if (visible.Length > shown)
                {
                    _runtime.Observer.AnswerAppended(Number, visible[shown..]);
                    shown = visible.Length;
                }
            }

            if (chunk.ToolCallDelta is { } tc) toolCalls.Add(tc);
            if (chunk.Usage is { } u) usage = u;
            if (chunk.StopReason is { Length: > 0 } sr) stop = sr;
            }
        }
        finally
        {
            // CLEARED ONCE, on the way out of a request that never produced a chunk: stopped or failed
            // while still waiting, the block must not keep saying it is waiting.
            if (first) _runtime.Observer.Waiting(Number, false);
        }

        return new LlmResponse { Text = text.ToString(), ToolCalls = toolCalls, Usage = usage, StopReason = stop };
    }

    private async Task<string> RunAsync(ToolCall call, CancellationToken ct)
    {
        var args = call.Arguments.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "" : call.Arguments.GetRawText();
        var shownArgs = args.Length > 80 ? args[..80] + "…" : args;

        if (!ReadingNames.Contains(call.Name))
        {
            _runtime.Observer.LookedUp(Number, $"{call.Name} {shownArgs} — refused");
            return Refusal;
        }

        _runtime.Observer.LookedUp(Number, $"{call.Name} {shownArgs}");
        var ctx = new JobContext("aside", Helpers.UlidGenerator.NewId(), new Dictionary<string, JobResult>(), null)
        {
            Requester = "aside",
            WorkingDirectory = _runtime.WorkingDir,
        };
        // THE SESSION'S REGISTRY, so the read meets its gate. Requester names the aside on any
        // prompt it raises, the way a sub-agent's name appears there. Stopping the aside cancels
        // `ct`, which withdraws a prompt still waiting for an answer.
        var outcome = await ToolBindings.InvokeAsync(call, Reading, _runtime.Executors, ctx, ct);
        return outcome.Text;
    }

    private static LlmUsage Sum(LlmUsage a, LlmUsage b) => a with
    {
        InputTokens = a.InputTokens + b.InputTokens,
        OutputTokens = a.OutputTokens + b.OutputTokens,
    };
}

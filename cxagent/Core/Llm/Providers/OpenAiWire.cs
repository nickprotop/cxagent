using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Models;

namespace CxAgent.Core.Llm.Providers;

/// <summary>Neutral &lt;-&gt; OpenAI Chat Completions wire mapping. Pure functions, no I/O.</summary>
/// <remarks>
/// PUBLIC only so <see cref="StreamingToolCallAccumulator"/> can be unit-tested directly. It held a
/// single-call assumption for a long time precisely because nothing tested it in isolation: the bug
/// only appears on a turn carrying two calls, which no integration test drove.
/// </remarks>
public static class OpenAiWire
{
    public static JsonObject BuildRequestBody(string model, List<ChatMessage> messages,
        List<ToolDefinition>? tools, bool stream)
    {
        var msgs = new JsonArray();
        foreach (var m in messages)
        {
            var obj = new JsonObject { ["role"] = m.Role };
            if (m.ToolCallId is not null)
            {
                obj["role"] = "tool";
                obj["tool_call_id"] = m.ToolCallId;
                obj["content"] = m.Content;
            }
            else if (m.ToolCalls is { Count: > 0 })
            {
                obj["content"] = m.Content.Length == 0 ? null : m.Content;
                var arr = new JsonArray();
                foreach (var tc in m.ToolCalls)
                    arr.Add(new JsonObject
                    {
                        ["id"] = tc.Id ?? tc.Name,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = tc.Name,
                            ["arguments"] = tc.Arguments.GetRawText()
                        }
                    });
                obj["tool_calls"] = arr;
            }
            else obj["content"] = m.Content;
            msgs.Add(obj);
        }

        var body = new JsonObject { ["model"] = model, ["messages"] = msgs };
        if (stream)
        {
            body["stream"] = true;
            // Opt-in per the OpenAI streaming spec: without this, no usage chunk is ever emitted (verified
            // against a live llama.cpp endpoint — a plain streaming request carries no usage at all), which
            // would leave any per-goal token budget unenforceable on the streaming path.
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }
        if (tools is { Count: > 0 })
        {
            var arr = new JsonArray();
            foreach (var t in tools)
                arr.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = JsonNode.Parse(t.InputSchema.GetRawText())
                    }
                });
            body["tools"] = arr;
        }
        return body;
    }

    public static string NormalizeStopReason(string? finishReason) => finishReason switch
    {
        "stop" => "end_turn",
        "tool_calls" => "tool_use",
        "length" => "max_tokens",
        "content_filter" => "refusal",
        _ => finishReason ?? "end_turn"
    };

    /// <summary>Parses a non-streaming choices[0] response into a neutral LlmResponse.</summary>
    public static LlmResponse ParseResponse(JsonElement root)
    {
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");

        string? text = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() : null;

        var calls = new List<ToolCall>();
        if (message.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            foreach (var tc in tcs.EnumerateArray())
            {
                var fn = tc.GetProperty("function");
                var argsRaw = fn.GetProperty("arguments").GetString() ?? "{}";
                calls.Add(new ToolCall
                {
                    Name = fn.GetProperty("name").GetString() ?? "",
                    Id = tc.TryGetProperty("id", out var id) ? id.GetString() : null,
                    Arguments = JsonDocument.Parse(argsRaw).RootElement.Clone()
                });
            }

        string? finish = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
        var usage = new LlmUsage();
        if (root.TryGetProperty("usage", out var u))
            usage = new LlmUsage
            {
                InputTokens = u.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0,
                OutputTokens = u.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0
            };

        return new LlmResponse
        {
            Text = text,
            ToolCalls = calls,
            StopReason = NormalizeStopReason(finish),
            Usage = usage
        };
    }

    /// <summary>
    /// Accumulates a streamed tool call across SSE chunks.
    ///
    /// In OpenAI streaming, a tool call's `arguments` is delivered as INCREMENTAL FRAGMENTS: only the
    /// first delta carries `function.name` and `id`; every continuation carries just an `arguments`
    /// slice, and the call ends with an EMPTY delta bearing `finish_reason`. A real capture of one
    /// `create_plan` call against llama.cpp had 103 such deltas — the first `{"name":…,"arguments":"{"}`
    /// and the rest single tokens like `"\"jobs\":"`, `"["`, … `"}"`.
    ///
    /// Parsing any single fragment as JSON therefore always fails ("Expected depth to be zero at the
    /// end of the JSON payload"), which is exactly what shipped before this type existed: no goal could
    /// be planned against a real streaming provider at all. `--mock` hid it by returning one complete
    /// response.
    ///
    /// Usage: feed every chunk to <see cref="Accept"/>; when it returns a non-null ToolCall the call is
    /// complete and its arguments are parsed once, from the joined buffer.
    /// </summary>
    public sealed class StreamingToolCallAccumulator
    {
        /// <summary>
        /// One partial call per stream INDEX. The wire numbers each entry of `tool_calls`, and that
        /// number is the only thing distinguishing them: name and id arrive once, on the first
        /// fragment, while arguments arrive across many.
        ///
        /// <para>This was a single name/id/buffer, which silently assumed ONE call per turn. A model
        /// emitting two in one turn had the second's argument fragments appended to the first's
        /// buffer, and only one call ever came out. It held for a long time because nothing drove
        /// multi-call turns: a planning turn returns one create_plan, and a worker rarely batches.
        /// The single-agent loop batches constantly, and the symptom was twelve consecutive
        /// `read_file {}` calls — a name whose arguments had gone into someone else's buffer.</para>
        /// </summary>
        private readonly SortedDictionary<int, Partial> _calls = new();

        private sealed class Partial
        {
            public readonly System.Text.StringBuilder Args = new();
            public string? Name;
            public string? Id;
        }

        /// <summary>True once any tool call has started arriving.</summary>
        public bool HasPending => _calls.Values.Any(c => c.Name is not null);

        /// <summary>
        /// Feeds one parsed chunk. Returns EVERY completed call when <paramref name="finishReason"/>
        /// signals the end, otherwise empty — a turn may carry several, and returning only the first
        /// is how the others were lost.
        /// </summary>
        public IReadOnlyList<ToolCall> Accept(JsonElement delta, string? finishReason)
        {
            if (delta.ValueKind == JsonValueKind.Object
                && delta.TryGetProperty("tool_calls", out var tcs)
                && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    // Absent index means a single-call stream: 0 is then the right bucket, and a
                    // provider that omits it cannot be emitting more than one anyway.
                    var index = tc.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number
                        ? ix.GetInt32() : 0;

                    if (!_calls.TryGetValue(index, out var partial))
                        _calls[index] = partial = new Partial();

                    if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        partial.Id ??= id.GetString();

                    if (tc.TryGetProperty("function", out var fn))
                    {
                        // name arrives once, on the FIRST fragment of its call.
                        if (fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                            partial.Name ??= nm.GetString();
                        // arguments arrive on nearly every fragment, to be concatenated.
                        if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String)
                            partial.Args.Append(a.GetString());
                    }
                }
            }

            // Only emit once the stream says the turn is done — a partial buffer never parses.
            if (finishReason is null) return Array.Empty<ToolCall>();

            var completed = new List<ToolCall>();
            foreach (var partial in _calls.Values)          // SortedDictionary: wire order preserved
            {
                if (partial.Name is null) continue;

                var raw = partial.Args.ToString();
                JsonElement parsed;
                try
                {
                    parsed = JsonDocument.Parse(raw.Length == 0 ? "{}" : raw).RootElement.Clone();
                }
                catch (JsonException)
                {
                    // A truncated stream (length cap, dropped connection) leaves unbalanced JSON.
                    // Surface an empty argument object rather than throwing out of an async
                    // iterator — the caller reports a failed call instead of the app dying.
                    parsed = JsonDocument.Parse("{}").RootElement.Clone();
                }

                completed.Add(new ToolCall { Name = partial.Name, Id = partial.Id, Arguments = parsed });
            }

            _calls.Clear();
            return completed;
        }
    }

    /// <summary>
    /// Parses one OpenAI SSE `data:` chunk into its text delta, finish reason, and the RAW `delta`
    /// element — which the caller feeds to a <see cref="StreamingToolCallAccumulator"/> so a tool call
    /// spread across many chunks can be reassembled. Returns a default JsonElement for a chunk with no
    /// `delta` (e.g. the terminator), which the accumulator treats as "nothing to append".
    /// </summary>
    /// <remarks>
    /// A `stream_options.include_usage`-requested usage chunk has an EMPTY `choices` array (its usage
    /// is read separately via <see cref="TryParseUsageOnlyChunk"/>) — indexing `choices[0]`
    /// unconditionally throws out of the async iterator and kills the goal mid-stream, so an empty
    /// array returns the same "nothing here" shape as a chunk with no `delta`.
    /// </remarks>
    public static (string? textDelta, string? finishReason, JsonElement delta) ParseStreamDelta(string dataJson)
    {
        // Clone the delta: the JsonDocument is disposed on return, and the accumulator reads it after.
        using var doc = JsonDocument.Parse(dataJson);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return (null, null, default);

        var choice = choices[0];
        string? finish = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String
            ? fr.GetString() : null;

        if (!choice.TryGetProperty("delta", out var delta))
            return (null, finish, default);

        string? text = delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() : null;

        return (text, finish, delta.Clone());
    }

    /// <summary>
    /// Parses a usage-only SSE `data:` chunk — the final chunk emitted when the request opts in via
    /// `stream_options: {"include_usage": true}`, shaped <c>{"choices":[],...,"usage":{...}}</c>.
    /// Returns false (and a default <see cref="LlmUsage"/>) for any chunk without a `usage` object.
    /// </summary>
    public static bool TryParseUsageOnlyChunk(string dataJson, out LlmUsage usage)
    {
        using var doc = JsonDocument.Parse(dataJson);
        if (!doc.RootElement.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
        {
            usage = new LlmUsage();
            return false;
        }

        usage = new LlmUsage
        {
            InputTokens = u.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0,
            OutputTokens = u.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0
        };
        return true;
    }

    /// <summary>Parses one OpenAI SSE `data:` chunk JSON into a delta. Returns null for [DONE]/empty.</summary>
    /// <remarks>
    /// The returned <c>toolCall</c> is only meaningful for providers that deliver a COMPLETE tool call in
    /// a single chunk. For real streaming providers use <see cref="StreamingToolCallAccumulator"/> —
    /// see its remarks. Kept for callers that parse whole non-streamed messages.
    /// </remarks>
    public static (string? textDelta, ToolCall? toolCall, string? finishReason) ParseStreamChunk(string dataJson)
    {
        using var doc = JsonDocument.Parse(dataJson);
        var choice = doc.RootElement.GetProperty("choices")[0];
        string? finish = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String
            ? fr.GetString() : null;

        if (!choice.TryGetProperty("delta", out var delta))
            return (null, null, finish);

        string? text = delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() : null;

        ToolCall? call = null;
        if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array
            && tcs.GetArrayLength() > 0)
        {
            var tc = tcs[0];
            if (tc.TryGetProperty("function", out var fn) && fn.TryGetProperty("name", out var nm)
                && nm.ValueKind == JsonValueKind.String)
            {
                var argsRaw = fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";
                call = new ToolCall
                {
                    Name = nm.GetString() ?? "",
                    Id = tc.TryGetProperty("id", out var id) ? id.GetString() : null,
                    Arguments = JsonDocument.Parse(argsRaw).RootElement.Clone()
                };
            }
        }
        return (text, call, finish);
    }
}

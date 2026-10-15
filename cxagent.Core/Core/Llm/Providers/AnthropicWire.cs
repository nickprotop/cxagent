using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Models;

namespace CxAgent.Core.Llm.Providers;

/// <summary>Neutral &lt;-&gt; Anthropic Messages wire mapping. Pure functions, no I/O.</summary>
internal static class AnthropicWire
{
    /// <summary>The Messages API request body for one call.</summary>
    /// <param name="model">The model id sent to the endpoint.</param>
    /// <param name="maxTokens">The response's output cap, thinking included.</param>
    /// <param name="messages">The conversation; system messages are hoisted to <c>system</c>.</param>
    /// <param name="tools">The tools offered, or null for none.</param>
    /// <param name="stream">Ask for server-sent events rather than one response.</param>
    /// <param name="cache">
    /// Ask Anthropic to cache the prefix — it caches nothing without a <c>cache_control</c> marker.
    /// Two of them: an explicit one on the system prompt, which covers the tools rendered before it
    /// and survives anything later in the conversation; and the top-level automatic one, which moves
    /// to the last block as the conversation grows, so each turn reads what the previous one wrote.
    /// In a tool loop the history is most of every request, so the system marker alone saves little.
    /// </param>
    public static JsonObject BuildRequestBody(string model, int maxTokens,
        List<ChatMessage> messages, List<ToolDefinition>? tools, bool stream, bool cache = false)
    {
        var systemParts = new List<string>();
        var msgs = new JsonArray();

        foreach (var m in messages)
        {
            if (m.Role == "system") { systemParts.Add(m.Content); continue; }

            if (m.ToolCallId is not null)
            {
                // A tool result -> a tool_result block in a user turn. ONE turn for all of an
                // assistant message's results: the API requires every tool_use to be answered in
                // the user turn immediately after it, so results sent as separate turns lean on
                // server-side merging, and anything spliced between them breaks the pairing.
                var block = new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = m.ToolCallId,
                    ["content"] = m.Content
                };
                if (msgs.Count > 0 && msgs[^1] is JsonObject prev
                    && prev["role"]?.GetValue<string>() == "user"
                    && prev["content"] is JsonArray prevContent
                    && prevContent.All(b => b?["type"]?.GetValue<string>() == "tool_result"))
                    prevContent.Add(block);
                else
                    msgs.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray { block } });
            }
            else if (m.ToolCalls is { Count: > 0 })
            {
                var content = new JsonArray();
                if (m.Content.Length > 0)
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
                foreach (var tc in m.ToolCalls)
                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = tc.Id ?? tc.Name,
                        ["name"] = tc.Name,
                        ["input"] = JsonNode.Parse(tc.Arguments.GetRawText())
                    });
                msgs.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
            }
            else
            {
                msgs.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
            }
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["messages"] = msgs
        };
        if (systemParts.Count > 0)
        {
            var system = string.Join("\n\n", systemParts);
            body["system"] = cache
                ? new JsonArray { new JsonObject {
                    ["type"] = "text",
                    ["text"] = system,
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } } }
                : system;
        }
        if (cache)
        {
            body["cache_control"] = new JsonObject { ["type"] = "ephemeral" };

            // THE TASK LIST MOVES, so the automatic marker on it never matches next turn: the
            // agent removes it and re-appends it after the new results, and the cached prefix that
            // ended with the old copy is gone. A marker on the block BEFORE it ends on history that
            // only ever grows, which the next request does contain.
            if (messages.Count > 1 && messages[^1].IsTaskList && msgs.Count > 1)
                MarkLastBlock((JsonObject)msgs[^2]!);
        }
        if (stream) body["stream"] = true;
        if (tools is { Count: > 0 })
        {
            var arr = new JsonArray();
            foreach (var t in tools)
                arr.Add(new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["input_schema"] = JsonNode.Parse(t.InputSchema.GetRawText())
                });
            body["tools"] = arr;
        }
        return body;
    }

    private static void MarkLastBlock(JsonObject message)
    {
        if (message["content"] is JsonValue text)
            message["content"] = new JsonArray { new JsonObject {
                ["type"] = "text", ["text"] = text.GetValue<string>() } };
        if (message["content"] is JsonArray { Count: > 0 } blocks && blocks[^1] is JsonObject last)
            last["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
    }

    /// <summary>
    /// Reassembles one streamed Anthropic message, fed one SSE data-line JSON at a time.
    ///
    /// <para>A TOOL CALL'S INPUT IS NOT IN ITS START EVENT. <c>content_block_start</c> carries the
    /// name and id with an empty <c>input</c>; the arguments follow as <c>input_json_delta</c>
    /// fragments and are only whole at <c>content_block_stop</c>. A call emitted at its start would
    /// reach its tool as <c>{}</c>: the tool fails for missing arguments, and the stuck guard fires
    /// on the identical empty calls.</para>
    ///
    /// <para>END IS <c>message_stop</c>, not <c>message_delta</c>: the delta carries the stop reason
    /// and the output token count, and is followed by the stop.</para>
    /// </summary>
    public sealed class StreamAccumulator
    {
        private readonly Dictionary<int, (string Name, string? Id, System.Text.StringBuilder Json)> _open = new();

        public string? StopReason { get; private set; }
        public bool IsDone { get; private set; }

        /// <summary>Input and cache figures arrive in <c>message_start</c>, the final output
        /// count in <c>message_delta</c>; each later object overrides only the fields it carries.</summary>
        public LlmUsage? Usage { get; private set; }

        /// <summary>Feeds one event. Returns its text delta and any tool call it completed.</summary>
        public (string? textDelta, ToolCall? toolCall) Accept(string dataJson)
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            var index = root.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number
                ? ix.GetInt32() : 0;

            switch (type)
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("usage", out var mu))
                        ReadUsage(mu);
                    return (null, null);

                case "content_block_start":
                    if (root.TryGetProperty("content_block", out var cb)
                        && cb.TryGetProperty("type", out var cbt) && cbt.GetString() == "tool_use")
                        _open[index] = (
                            cb.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                            cb.TryGetProperty("id", out var id) ? id.GetString() : null,
                            new System.Text.StringBuilder());
                    return (null, null);

                case "content_block_delta":
                    if (!root.TryGetProperty("delta", out var d) || !d.TryGetProperty("type", out var dt))
                        return (null, null);
                    switch (dt.GetString())
                    {
                        case "text_delta":
                            return (d.TryGetProperty("text", out var txt) ? txt.GetString() : null, null);
                        case "input_json_delta":
                            if (_open.TryGetValue(index, out var acc) && d.TryGetProperty("partial_json", out var pj))
                                acc.Json.Append(pj.GetString());
                            return (null, null);
                        default:
                            return (null, null);
                    }

                case "content_block_stop":
                    return (null, Close(index));

                case "message_delta":
                    if (root.TryGetProperty("delta", out var md)
                        && md.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
                        StopReason = NormalizeStopReason(sr.GetString());
                    if (root.TryGetProperty("usage", out var du))
                        ReadUsage(du);
                    return (null, null);

                case "message_stop":
                    IsDone = true;
                    return (null, null);

                default:
                    return (null, null);
            }
        }

        /// <summary>Tool calls whose block never closed — a stream cut short — and whose input
        /// is nonetheless whole. One whose input does not parse is dropped by <c>Close</c>.</summary>
        public List<ToolCall> Flush()
        {
            var calls = _open.Keys.Order().ToList().Select(Close).OfType<ToolCall>().ToList();
            _open.Clear();
            return calls;
        }

        private ToolCall? Close(int index)
        {
            if (!_open.Remove(index, out var acc)) return null;
            var json = acc.Json.ToString();
            JsonElement args;
            try { args = JsonDocument.Parse(json.Length == 0 ? "{}" : json).RootElement.Clone(); }
            // Malformed only when the output cap cut the input mid-argument. DROPPED, not run with
            // {}: the empty call fails for missing arguments and the model takes it for its own
            // mistake. The stop reason says max_tokens, and the agent asks for the call again.
            catch (JsonException) { return null; }
            return new ToolCall { Name = acc.Name, Id = acc.Id, Arguments = args };
        }

        private void ReadUsage(JsonElement u) => Usage = AnthropicWire.ReadUsage(u, Usage);
    }

    /// <summary>
    /// One Anthropic <c>usage</c> object in the neutral shape. Anthropic's <c>input_tokens</c> is
    /// only the UNCACHED remainder; the neutral InputTokens is the whole prompt, as OpenAI's
    /// <c>prompt_tokens</c> is, so the cache reads and writes are added back in. Reported as cache
    /// figures only when Anthropic sends the fields — absent is "unknown", not a zero hit rate.
    /// </summary>
    internal static LlmUsage ReadUsage(JsonElement u, LlmUsage? prior = null)
    {
        int Get(string name, int fallback) =>
            u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

        var p = prior ?? new LlmUsage();
        var uncached = Get("input_tokens", p.InputTokens - p.CachedInputTokens - p.CacheWriteTokens);
        var read = Get("cache_read_input_tokens", p.CachedInputTokens);
        var written = Get("cache_creation_input_tokens", p.CacheWriteTokens);
        return new LlmUsage
        {
            InputTokens = uncached + read + written,
            OutputTokens = Get("output_tokens", p.OutputTokens),
            CachedInputTokens = read,
            CacheWriteTokens = written,
            CacheReported = p.CacheReported
                || u.TryGetProperty("cache_read_input_tokens", out _)
                || u.TryGetProperty("cache_creation_input_tokens", out _),
        };
    }

    public static string NormalizeStopReason(string? stopReason) => stopReason switch
    {
        "end_turn" => "end_turn",
        "tool_use" => "tool_use",
        "max_tokens" => "max_tokens",
        "stop_sequence" => "end_turn",
        "refusal" => "refusal",
        _ => stopReason ?? "end_turn"
    };

    public static LlmResponse ParseResponse(JsonElement root)
    {
        string? text = null;
        var calls = new List<ToolCall>();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
            {
                var type = block.GetProperty("type").GetString();
                if (type == "text")
                    text = (text ?? "") + (block.TryGetProperty("text", out var t) ? t.GetString() : "");
                else if (type == "tool_use")
                    calls.Add(new ToolCall
                    {
                        Name = block.GetProperty("name").GetString() ?? "",
                        Id = block.TryGetProperty("id", out var id) ? id.GetString() : null,
                        Arguments = block.TryGetProperty("input", out var inp) ? inp.Clone()
                            : JsonDocument.Parse("{}").RootElement.Clone()
                    });
            }

        string? stop = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
        var usage = root.TryGetProperty("usage", out var u) ? ReadUsage(u) : new LlmUsage();

        return new LlmResponse
        {
            Text = text,
            ToolCalls = calls,
            StopReason = NormalizeStopReason(stop),
            Usage = usage
        };
    }
}

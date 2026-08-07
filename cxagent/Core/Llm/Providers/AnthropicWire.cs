using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Models;

namespace CxAgent.Core.Llm.Providers;

/// <summary>Neutral &lt;-&gt; Anthropic Messages wire mapping. Pure functions, no I/O.</summary>
internal static class AnthropicWire
{
    public static JsonObject BuildRequestBody(string model, int maxTokens,
        List<ChatMessage> messages, List<ToolDefinition>? tools, bool stream)
    {
        var systemParts = new List<string>();
        var msgs = new JsonArray();

        foreach (var m in messages)
        {
            if (m.Role == "system") { systemParts.Add(m.Content); continue; }

            if (m.ToolCallId is not null)
            {
                // A tool result -> user turn with a tool_result content block.
                msgs.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray {
                        new JsonObject {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = m.ToolCallId,
                            ["content"] = m.Content
                        }
                    }
                });
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
        if (systemParts.Count > 0) body["system"] = string.Join("\n\n", systemParts);
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

    /// <summary>
    /// Parses one Anthropic SSE data-line JSON. Returns a text delta (content_block_delta/text_delta),
    /// a tool_use start (content_block_start), or a final marker (message_delta/message_stop).
    /// </summary>
    public static (string? textDelta, ToolCall? toolCall, bool isFinal) ParseStreamEvent(string dataJson)
    {
        using var doc = JsonDocument.Parse(dataJson);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

        switch (type)
        {
            case "content_block_delta":
                if (root.TryGetProperty("delta", out var d)
                    && d.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta")
                    return (d.TryGetProperty("text", out var txt) ? txt.GetString() : null, null, false);
                return (null, null, false);

            case "content_block_start":
                if (root.TryGetProperty("content_block", out var cb)
                    && cb.TryGetProperty("type", out var cbt) && cbt.GetString() == "tool_use")
                    return (null, new ToolCall
                    {
                        Name = cb.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                        Id = cb.TryGetProperty("id", out var id) ? id.GetString() : null,
                        Arguments = JsonDocument.Parse("{}").RootElement.Clone()
                    }, false);
                return (null, null, false);

            case "message_delta":
            case "message_stop":
                return (null, null, true);

            default:
                return (null, null, false);
        }
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
        var usage = new LlmUsage();
        if (root.TryGetProperty("usage", out var u))
            usage = new LlmUsage
            {
                InputTokens = u.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0,
                OutputTokens = u.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0
            };

        return new LlmResponse
        {
            Text = text,
            ToolCalls = calls,
            StopReason = NormalizeStopReason(stop),
            Usage = usage
        };
    }
}

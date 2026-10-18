using System.Text.Json;

namespace CxAgent.Core.Llm;

/// <summary>
/// What a tool tells a model about itself: its name, what it does, and the schema of its arguments.
///
/// <para>IN CORE, NOT THE PLUGIN CONTRACT. <see cref="Jobs.IAgentTool"/> returns one, and an
/// embedder implements that; a plugin describes its tools in its manifest instead, and no
/// plugin-facing signature names this type.</para>
/// </summary>
public record ToolDefinition(string Name, string Description, JsonElement InputSchema);

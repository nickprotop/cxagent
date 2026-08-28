using System.Text.Json;

namespace CxAgent.Core.Llm;

/// <summary>
/// What a tool tells a model about itself: its name, what it does, and the schema of its arguments.
///
/// <para>IN THE ABSTRACTIONS ASSEMBLY, not beside the provider types it is named among.
/// <see cref="Jobs.IAgentTool"/> returns one, and a plugin implements that — so this is contract
/// surface, and a plugin referencing it must not thereby reference the LLM vocabulary.</para>
/// </summary>
public record ToolDefinition(string Name, string Description, JsonElement InputSchema);

using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Agents;

/// <summary>
/// The main agent's conversation as it last sent it to the model, with the tools it offered.
///
/// <para>WHAT A SIDE QUESTION FORKS FROM. Copied by the loop's own thread at a moment it is not
/// mutating the list, which a copy taken from another thread cannot promise. And a request about to
/// be sent is always a valid conversation, every tool call answered, so a fork never ends on a call
/// with no result, which a provider rejects.</para>
///
/// <para>THE TOOLS TRAVEL WITH IT because an aside offers exactly this list. On llama.cpp the
/// definitions are part of the prompt, and a different list would reprocess the whole fork.</para>
/// </summary>
public sealed record PublishedContext(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ToolDefinition> Tools);

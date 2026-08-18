namespace CxAgent.Core.Sessions;

/// <summary>
/// Identifies one turn in a conversation.
///
/// <para>MINTED BY THE SESSION, not by whatever is observing it. Each observer used to generate its
/// own — <c>ChatTranscriptSink</c> incremented a counter, <c>BufferedChatSink</c> called NextId — so
/// turn identity originated in the presentation layer and flowed back into the session. Two observers
/// therefore disagreed about which turn was which, which is why there could only ever be one.</para>
/// </summary>
public readonly record struct ChatMessageId(long Value);

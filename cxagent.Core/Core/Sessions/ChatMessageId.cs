namespace CxAgent.Core.Sessions;

/// <summary>
/// Identifies one turn in a conversation.
///
/// <para>MINTED BY THE SESSION, not by whatever is observing it. An observer generating its own —
/// incrementing a counter, calling NextId — would originate turn identity in the presentation layer
/// and flow it back into the session, so two observers would disagree about which turn was which and
/// only one could ever be attached. One minter upstream of all of them keeps them consistent.</para>
/// </summary>
public readonly record struct ChatMessageId(long Value);

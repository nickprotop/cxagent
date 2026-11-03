namespace CxAgent.Core.Models;

/// <summary>
/// A block the user pasted into a message, which the message showed as a short label.
/// </summary>
/// <param name="Label">What the line showed in the paste's place — <c>[Pasted text #1 +18 lines]</c>.</param>
/// <param name="Text">What was pasted, and what the model was sent in the label's place.</param>
public sealed record PastedText(string Label, string Text);

namespace CxAgent.Core.Models;

/// <summary>
/// What a typed line carries besides its words: the blocks pasted into it, and the images attached to it.
///
/// <para>ONE BUNDLE, BECAUSE THEY TRAVEL TOGETHER. Both ride the same route from the composer to the
/// message the model is sent — submit, the queue, a steer, an aside, an agent's tab, a custom command —
/// and two lists side by side on each of those signatures put a fifth parameter on several of them,
/// where transposing a pair would still compile.</para>
/// </summary>
/// <param name="Pastes">The pasted blocks, each shown in its line as a label.</param>
/// <param name="Images">The images, each sent to the model beside the text.</param>
public sealed record Attachments(IReadOnlyList<PastedText> Pastes, IReadOnlyList<AttachedImage> Images)
{
    /// <summary>Nothing attached.</summary>
    public static readonly Attachments None = new([], []);

    public bool IsEmpty => Pastes.Count == 0 && Images.Count == 0;

    /// <summary>Only pasted blocks.</summary>
    public static Attachments Of(IReadOnlyList<PastedText> pastes) => new(pastes, []);

    /// <summary>
    /// Both lines' attachments, this one's first — for the queue, which sends lines typed while a turn
    /// ran as one message.
    /// </summary>
    public Attachments Plus(Attachments other) =>
        other.IsEmpty ? this : IsEmpty ? other : new([.. Pastes, .. other.Pastes], [.. Images, .. other.Images]);
}

/// <summary>An image attached to a message.</summary>
public sealed record AttachedImage;

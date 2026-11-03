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

/// <summary>
/// An image attached to a message: the PNG the model is sent, and how it is named and sized.
///
/// <para>THE BYTES LIVE IN MEMORY, NOT IN THE SAVED CONTEXT. <c>context.json</c> is rewritten whole on
/// every save, so a screenshot inline in it would be written again every turn. The session store writes
/// each image once, as <c>images/&lt;Hash&gt;.png</c> in the folder of the agent saving it, and reads it
/// back into <see cref="Png"/> on load. The wire builders read <see cref="Png"/> and nothing else, which
/// keeps them free of I/O.</para>
/// </summary>
/// <param name="Label">The token's label, which the message text carries in the image's place —
/// <c>[Image #1 1568×882]</c>.</param>
/// <param name="Hash">SHA-256 of the PNG, lowercase hex: the file's name, so one image pasted twice is
/// stored once.</param>
/// <param name="Width">The width sent, in pixels.</param>
/// <param name="Height">The height sent, in pixels.</param>
public sealed record AttachedImage(string Label, string Hash, int Width, int Height)
{
    /// <summary>The PNG sent, or null when its file could not be read back.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[]? Png { get; init; }

    /// <summary>
    /// What the image weighs in the context, as characters of text would.
    ///
    /// <para>ITS TOKENS AT FOUR CHARACTERS EACH. The context measures itself in characters and scales a
    /// provider's token reading by them, and an image has tokens but no characters: left at zero, one
    /// screenshot raises the measured density of every character of text, and a short message carrying
    /// one reads as more tokens than characters, which the context rejects as an impossible reading.
    /// The token count is Anthropic's <c>width × height / 750</c>, which lies within the range of what
    /// OpenAI's tiles and Qwen-VL's patches charge for the sizes sent.</para>
    /// </summary>
    public int WeightChars => (int)Math.Ceiling((double)Width * Height / 750) * 4;

    /// <summary>What a model that cannot see the image is sent in its place.</summary>
    public string Placeholder => $"[image: {Name}, {Width}×{Height}]";

    /// <summary>What is sent when the image's bytes are gone.</summary>
    public string Unavailable => $"[image unavailable: {Name}]";

    /// <summary>"Image #1", from a label of <c>[Image #1 1568×882]</c>.</summary>
    private string Name
    {
        get
        {
            var inner = Label.Trim('[', ']');
            var space = inner.LastIndexOf(' ');
            return space > 0 ? inner[..space] : inner;
        }
    }
}

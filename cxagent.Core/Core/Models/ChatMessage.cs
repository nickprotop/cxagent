using System.Text.Json;

namespace CxAgent.Core.Models;

public record ChatMessage
{
    // "user", "assistant", "system", "tool". NOTE: for a tool RESULT the wire builders ignore this
    // and set the role themselves — OpenAiWire overwrites it to "tool", AnthropicWire emits a "user"
    // turn carrying a tool_result block. ToolCallId below, not this, is what makes it a tool result.
    public required string Role { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }  // assistant messages with tool use
    public string? ToolCallId { get; init; }         // tool result messages — the ONLY marker of one

    /// <summary>
    /// What the transcript showed for this user message, when that differs from what the model was
    /// sent — <c>/review 42</c> for a command's expanded prompt, <c>/init</c> for its briefing.
    ///
    /// <para>KEPT FOR THE SESSION LIST, which titles a session by its first message: the expansion is
    /// what the model needed, and the typed line is what the user will recognise. Never sent to a
    /// provider — the wire builders read <see cref="Content"/>.</para>
    /// </summary>
    public string? Shown { get; init; }

    /// <summary>
    /// The blocks pasted into this message, each shown in <see cref="Shown"/> as its label.
    ///
    /// <para>KEPT FOR A READABLE EXPORT, which prints the line as it was typed and then each paste under
    /// its label: <see cref="Shown"/> alone would leave a person reading the export with "[Pasted text #1
    /// +18 lines]" and not the stack trace it stood for. <see cref="Content"/> already holds the text in
    /// place, so this repeats it, which a paste's size makes worth paying once per message for. Never
    /// sent to a provider, for the same reason as <see cref="Shown"/>.</para>
    /// </summary>
    public IReadOnlyList<PastedText>? Pastes { get; init; }

    /// <summary>
    /// The images attached to this message, sent beside <see cref="Content"/>, which carries each one's
    /// label where it was typed.
    ///
    /// <para>UNLIKE <see cref="Pastes"/>, THESE REACH THE PROVIDER: they are what the model sees, not a
    /// record of how the line looked. Saved as references; see <see cref="AttachedImage"/>.</para>
    /// </summary>
    public IReadOnlyList<AttachedImage>? Images { get; init; }

    /// <summary>
    /// The message's size as the context counts it: its text, and each image at its weight.
    /// </summary>
    public int SizeChars => (Content?.Length ?? 0) + (Images?.Sum(i => i.WeightChars) ?? 0);

    /// <summary>
    /// True for the ONE regenerated message carrying the model's task list.
    ///
    /// <para>A PROPERTY, NOT A TEXT MATCH. The list is rewritten every turn, so the previous copy has
    /// to be found and replaced; locating it by its rendered prose would delete a user message that
    /// happened to quote the plan back.</para>
    ///
    /// <para>A BOOL RATHER THAN A GENERAL TAG because there is exactly one kind of regenerated
    /// message. Inventing a tagging scheme for a single user is speculative.</para>
    ///
    /// <para>It never reaches a provider: the wire builders construct each message field-by-field
    /// into a fresh JsonObject rather than serialising this record, so a new property cannot change
    /// the bytes that get cached.</para>
    /// </summary>
    public bool IsTaskList { get; init; }

    /// <summary>
    /// The thinking blocks this assistant turn was given, to send back while its tool loop runs.
    ///
    /// <para>NOT SAVED. They matter only within the loop that produced them — a later request
    /// leaves them out — so writing them to the session would store weight nothing reads.</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<Llm.ThinkingBlock>? ThinkingBlocks { get; init; }
}

public record ToolCall
{
    public string Name { get; init; } = "";
    public JsonElement Arguments { get; init; }
    public string? Id { get; init; }
}

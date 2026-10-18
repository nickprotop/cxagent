namespace CxAgent.Core.Export;

/// <summary>How much of a conversation an export covers.</summary>
public enum ExportScope
{
    /// <summary>Everything recorded, across clears and compactions.</summary>
    Whole,

    /// <summary>From the last turn's start.</summary>
    LastTurn,

    /// <summary>From the start of the last <see cref="ExportOptions.Turns"/> turns.</summary>
    LastTurns,
}

/// <summary>What an export includes, beyond the messages themselves.</summary>
[Flags]
public enum ExportParts
{
    None = 0,

    /// <summary>Each tool call as one line.</summary>
    ToolCalls = 1,

    /// <summary>A file change as a diff under its call.</summary>
    Diffs = 2,

    /// <summary>Title, dates, folder, model and spend at the top.</summary>
    Header = 4,

    /// <summary>Each sub-agent's own conversation, where it happened; without it, only its report.</summary>
    SubAgents = 8,

    /// <summary>What each tool returned, in full.</summary>
    ToolResults = 16,

    /// <summary>The model's reasoning, where it was kept.</summary>
    Thinking = 32,

    /// <summary>The summary compaction replaced the context with; the marker is always shown.</summary>
    Compaction = 64,

    /// <summary>The system prompt, and what the application injected — the task list.</summary>
    SystemAndInjected = 128,
}

/// <summary>One export's choices.</summary>
/// <param name="Scope">How much of the conversation.</param>
/// <param name="Turns">For <see cref="ExportScope.LastTurns"/>: how many.</param>
/// <param name="Parts">What to include.</param>
public sealed record ExportOptions(ExportScope Scope, int Turns, ExportParts Parts)
{
    /// <summary>For a person: what was said and done, without what only the model needed.</summary>
    public const ExportParts Readable = ExportParts.ToolCalls | ExportParts.Diffs | ExportParts.Header | ExportParts.SubAgents;

    /// <summary>For an agent: everything the model saw.</summary>
    public const ExportParts Full = Readable | ExportParts.ToolResults | ExportParts.Thinking | ExportParts.Compaction
        | ExportParts.SystemAndInjected;

    /// <summary>"Readable", "Full", or "Custom" for any other mix.</summary>
    public string PresetName => Parts switch
    {
        Readable => "Readable",
        Full => "Full",
        _ => "Custom",
    };
}

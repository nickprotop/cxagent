namespace CxAgent.Core.Config;

/// <summary>
/// What a running tab does with a saved change that would break its prompt cache — its model changed,
/// or its tools did. The user chooses at save time; the file is saved whatever they choose.
/// </summary>
public enum CacheChoice
{
    /// <summary>Move the tab now (after its turn when busy): the whole conversation is re-sent once.</summary>
    ApplyNow,

    /// <summary>Compress the conversation first, so only the summary is re-sent.</summary>
    CompressThenApply,

    /// <summary>Wait for the tab's next <c>/clear</c>, compaction or <c>/model</c> — the cache is rebuilt then anyway.</summary>
    AtNextClearOrCompression,

    /// <summary>Leave running tabs as they are; new tabs and the next start get the change.</summary>
    NewTabsOnly,
}

namespace CxAgent.Core.Llm;

/// <summary>
/// What a spend breakdown says about cost where no figure exists — ONE TEXT FOR THE TERMINAL'S PANEL AND THE BROWSER'S, so
/// neither words it differently.
///
/// <para>SAID, NEVER PRICED. A provider that does not report a cost (Anthropic's API, a local server) leaves no figure,
/// and none is invented: "$0.00" would claim a measurement never made, and silence read as a cost line that had gone
/// missing (user, 2026-11-13). "Not reported" is true of both a paid endpoint that does not say and a free one.</para>
/// </summary>
public static class CostWords
{
    /// <summary>Under an instance that never reported a cost, and as the whole cost line when none did.</summary>
    public const string NotReported = "no cost reported";

    /// <summary>
    /// What a total covers when only some instances reported — "1 of 2 models reported" — or null when every one did
    /// and the total needs no qualifying.
    /// </summary>
    public static string? Coverage(int reported, int models) =>
        reported >= models ? null : $"{reported} of {models} model{(models == 1 ? "" : "s")} reported";

    /// <summary>
    /// The line under the session's total: what it covers when some models reported, that none did when there is no
    /// total — or null. ONE MODEL NEEDS NO SUCH LINE when it reported nothing: its own block already says so, and the
    /// same words twice, one under the other, read as two facts.
    /// </summary>
    public static string? SessionNote(bool hasTotal, int reported, int models) =>
        hasTotal ? Coverage(reported, models) : models > 1 ? NotReported : null;
}

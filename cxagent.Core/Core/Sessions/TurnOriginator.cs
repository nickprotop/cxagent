namespace CxAgent.Core.Sessions;

/// <summary>
/// Who asked for a turn.
///
/// <para>WHY THIS EXISTS AT ALL: unwire outranks a turn a PLUGIN started and defers to one the USER
/// started. Without an originator the two are indistinguishable, and a plugin that submits can hold
/// off its own revocation for as long as it keeps submitting — `Submit` makes the session busy, and
/// a busy session defers its unwire.</para>
///
/// <para>A RECORD RATHER THAN A BOOL, because "which plugin" is what a message has to name: "cancelled
/// events-plugin's turn to unwire it" tells a user what happened; "cancelled a plugin turn" does not.</para>
/// </summary>
public sealed record TurnOriginator(string? PluginName)
{
    /// <summary>Someone typed it.</summary>
    public static readonly TurnOriginator User = new((string?)null);

    /// <summary>A plugin submitted it, by name.</summary>
    public static TurnOriginator Plugin(string name) => new(name);

    /// <summary>Whether this turn belongs to the named plugin, and may therefore be cancelled to sever it.</summary>
    public bool IsFrom(string pluginName) =>
        PluginName is { } mine && string.Equals(mine, pluginName, StringComparison.Ordinal);
}

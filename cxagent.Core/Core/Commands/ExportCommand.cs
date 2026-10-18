using CxAgent.Core.Export;

namespace CxAgent.Core.Commands;

/// <summary>What <c>/export</c>'s words set in the dialog; null leaves the remembered choice.</summary>
/// <param name="Scope">The scope asked for.</param>
/// <param name="Turns">How many turns, for <see cref="ExportScope.LastTurns"/>.</param>
/// <param name="Full">True for the full preset.</param>
public sealed record ExportPrefill(ExportScope? Scope, int? Turns, bool? Full)
{
    public static ExportPrefill None { get; } = new(null, null, null);
}

/// <summary>
/// Reads <c>/export</c>'s arguments: <c>last</c>, <c>last N</c>, <c>full</c>, in any order.
///
/// <para>A PREFILL, NOT A COMMAND: the words open the dialog already set, so a typed shortcut and a choice made in
/// the dialog end the same way. An unknown word is ignored rather than refused — the dialog shows what was
/// understood before anything is written.</para>
/// </summary>
public static class ExportCommand
{
    public static ExportPrefill Parse(string arguments)
    {
        var words = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ExportScope? scope = null;
        int? turns = null;
        bool? full = null;
        for (var i = 0; i < words.Length; i++)
        {
            if (words[i].Equals("full", StringComparison.OrdinalIgnoreCase))
                full = true;
            else if (words[i].Equals("last", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < words.Length && int.TryParse(words[i + 1], out var n) && n > 0)
                {
                    scope = ExportScope.LastTurns;
                    turns = n;
                    i++;
                }
                else
                    scope = ExportScope.LastTurn;
            }
        }
        return new ExportPrefill(scope, turns, full);
    }
}

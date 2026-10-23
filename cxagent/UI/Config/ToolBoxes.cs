using CxAgent.Core.Config;
using CxAgent.Core.Jobs;

namespace CxAgent.UI.Config;

/// <summary>
/// A <c>tools</c> list as the sixteen boxes show it, and back — the one reading the General and Agents tabs share,
/// so a list means the same in both and neither opens dirty over a list it would write another way.
///
/// <para>NULL AND EMPTY DIFFER throughout: no key offers every tool the base gives, an empty list none.</para>
/// </summary>
public static class ToolBoxes
{
    /// <summary>
    /// What a saved list says, for the boxes. NO TOOLS IS EVERY BOX UNTICKED: <see cref="ToolTerms.Read"/> takes an
    /// empty list as the inherited base with nothing taken away, which would tick every box over a type that has none.
    /// </summary>
    public static ToolTerms Read(IReadOnlyList<string>? terms)
    {
        var read = ToolTerms.Read(terms);
        return terms is { Count: 0 } ? read with { Removed = [.. Tool.KnownNames] } : read;
    }

    /// <summary>What the boxes would write: null for no key, otherwise the list in its <see cref="Canonical"/> form.</summary>
    public static IReadOnlyList<string>? Written(string baseTerm, IReadOnlyList<string> removed, IReadOnlyList<string> added) =>
        baseTerm == "inherited" && removed.Count == 0 && added.Count == 0
            ? null
            : Canonical(ToolTerms.Write(new ToolTerms(baseTerm, removed, added)));

    /// <summary>
    /// A selection in the one form the boxes write, so two that say the same are equal: what the file said is
    /// compared in this form too, and a list written another way — no base, the base alone — is not a change.
    /// Every tool taken from the inherited base, with none added back, is the empty list: no tools.
    /// </summary>
    public static IReadOnlyList<string>? Canonical(IReadOnlyList<string>? terms)
    {
        if (terms is null) return null;
        if (terms.Count == 0) return [];
        var read = ToolTerms.Read(terms);
        if (read.Base == "inherited" && read.Added.Count == 0 && Tool.KnownNames.All(read.Removed.Contains)) return [];
        return read.Base == "inherited" && read.Removed.Count == 0 && read.Added.Count == 0 ? null : ToolTerms.Write(read);
    }

    /// <summary>Whether two canonical selections say the same — no key and the empty list are not the same.</summary>
    public static bool Same(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        (a is null) == (b is null) && (a ?? []).SequenceEqual(b ?? [], StringComparer.Ordinal);
}

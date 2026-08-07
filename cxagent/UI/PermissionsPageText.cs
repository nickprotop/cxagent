using System.Collections.Generic;
using System.Linq;
using CxAgent.Core.Permissions;

namespace CxAgent.UI;

/// <summary>
/// Pure page-text builder for the Settings dialog's Permissions page (spec Decision 7: the page
/// is READ-ONLY — no revoke, no trust toggle. Every other control in the Settings dialog applies
/// on Save; a trust toggle applies immediately, and mixing those two semantics in one dialog is a
/// UX trap. P12 deliberately attached trust changes to real friction (the "Always allow?" prompt),
/// which a settings page is not. Revoke needs store methods a separate plan (P13) owns — until
/// then, the page tells the user to edit permissions.json directly.
///
/// Split out from <see cref="SettingsDialog"/> so escaping (P7) and the LoadError/trust-state/
/// other-scope-count text are unit-testable without a live window — same seam as
/// <see cref="SettingsDialog.ProviderRowLabels"/>.
/// </summary>
public static class PermissionsPageText
{
    /// <summary>One escaped Markup-ready line per row: the LoadError warning (only when non-null),
    /// trust state with a one-line explanation, this scope's rules grouped by kind, an honest
    /// other-scope count, the permissions.json path, and the revoke note.</summary>
    public static IReadOnlyList<string> Build(PermissionRulesStore store, string workingDir)
    {
        var lines = new List<string>();

        if (store.LoadError is { } error)
        {
            lines.Add($"[red]{SharpConsoleUI.Parsing.MarkupParser.Escape(error)}[/]");
            lines.Add(string.Empty);
        }

        var trust = store.GetTrust(workingDir);
        lines.Add($"[bold]Trust:[/] {TrustLabel(trust)}");
        lines.Add($"[dim]{TrustExplanation(trust)}[/]");
        lines.Add(string.Empty);

        var (rules, otherScopeCount) = store.RulesFor(workingDir);
        if (rules.Count == 0)
        {
            lines.Add("[dim]No always-allow rules for this folder.[/]");
        }
        else
        {
            lines.Add("[bold]Always-allow rules for this folder:[/]");
            foreach (var group in rules.GroupBy(r => r.Kind).OrderBy(g => g.Key.ToString()))
            {
                lines.Add($"[bold]{group.Key}:[/]");
                foreach (var rule in group)
                    lines.Add($"  {SharpConsoleUI.Parsing.MarkupParser.Escape(rule.Pattern)}");
            }
        }
        lines.Add(string.Empty);

        lines.Add(otherScopeCount switch
        {
            0 => "[dim]No other folders have rules.[/]",
            1 => "[dim]1 other folder has rules (not shown here).[/]",
            _ => $"[dim]{otherScopeCount} other folders have rules (not shown here).[/]",
        });
        lines.Add(string.Empty);

        lines.Add($"[dim]Stored at: {SharpConsoleUI.Parsing.MarkupParser.Escape(store.FilePath)}[/]");
        lines.Add("[dim]This page is read-only. To revoke a rule, edit permissions.json directly.[/]");

        return lines;
    }

    private static string TrustLabel(TrustState trust) => trust switch
    {
        TrustState.Trusted => "Trusted",
        TrustState.Untrusted => "Untrusted",
        _ => "Unknown",
    };

    private static string TrustExplanation(TrustState trust) => trust switch
    {
        TrustState.Trusted => "silent actions in this folder are allowed without asking.",
        TrustState.Untrusted => "silent actions in this folder will always be asked about.",
        _ => "you will be asked when a goal needs it.",
    };
}

using SharpConsoleUI;
using SharpConsoleUI.Themes;

namespace CxAgent.UI;

/// <summary>
/// cxagent's palette, expressed as <see cref="ColorRole"/> wherever a control supports it.
///
/// <para>ColorRole is the framework's semantic layer: a role resolves through the active theme, so
/// Danger is whatever red THIS theme means, and a theme change moves every control at once. The
/// older cx apps predate it and hard-code literal colours (cxpost's Grey23 rules, cxtop's #ff6b6b);
/// matching those literals would freeze cxagent to one theme and re-create the coupling the roles
/// exist to remove.</para>
///
/// <para>Literals survive only where a control takes no role — a markup token inside a string
/// cannot be a role, and the framework offers no role-to-markup bridge. Those are named here rather
/// than scattered, so the mapping is in one place if a bridge appears.</para>
/// </summary>
public static class ColorScheme
{
    // --- Roles: prefer these. Every control implementing IColorRoleableControl takes one. ---

    /// <summary>Accent: the agent's own voice, active state, primary affordance.</summary>
    public const ColorRole Accent = ColorRole.Primary;

    /// <summary>Rules, separators, and other structural lines.</summary>
    public const ColorRole Structure = ColorRole.Secondary;

    /// <summary>An affirmative action that changes nothing dangerous — "Allow once".</summary>
    public const ColorRole Affirmative = ColorRole.Success;

    /// <summary>A consequential-but-expected action — "Always allow", which persists a rule.</summary>
    public const ColorRole Caution = ColorRole.Warning;

    /// <summary>Refusal and destruction — "Deny".</summary>
    public const ColorRole Destructive = ColorRole.Danger;

    // --- Markup tokens, for text inside a string, where a role cannot reach. ---

    /// <summary>Keybinds and hints: present but skippable.</summary>
    public const string MutedMarkup = "grey50";

    /// <summary>A label beside an accented value.</summary>
    public const string LabelMarkup = "grey70";

    /// <summary>The accent, as markup. Kept beside <see cref="Accent"/> so the two cannot drift.</summary>
    public const string AccentMarkup = "cyan1";

    /// <summary>
    /// Colour for a percentage readout, by how alarming it is. cxtop's thresholds — teal below 60,
    /// amber below 85, red above — which is the family's only precedent for a live percentage, and
    /// the one number a user reads at a glance rather than by reading.
    /// </summary>
    public static string ThresholdMarkup(double percent) =>
        percent < 60 ? AccentMarkup : percent < 85 ? "yellow" : "red";
}

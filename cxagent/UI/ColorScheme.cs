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

    /// <summary>
    /// The session panel's surface — one step off the window background, so the column reads as a
    /// different KIND of thing rather than as narrow transcript. opencode's backgroundElement.
    ///
    /// <para>DELIBERATELY NOT the same as <see cref="CodeBackground"/>. Both started at #141414 —
    /// opencode's backgroundPanel — and the collision was visible immediately: inline code spans in
    /// the transcript painted the identical grey, so the panel stopped reading as a surface and read
    /// as more scattered code. Two different meanings cannot share one colour when they appear side
    /// by side.</para>
    /// </summary>
    public static readonly Color PanelSurface = new(0x1e, 0x1e, 0x1e);

    // --- Markdown ---------------------------------------------------------------
    // Values from opencode's default dark theme (packages/tui/src/theme/assets/
    // opencode.json), which is the palette this was compared against.

    /// <summary>Headings. Purple, and the SAME colour at every level — opencode does not step the
    /// hue down, it distinguishes h1 by underline alone. Ours was three shades of blue for H1-H3
    /// and nothing below, which reads as one muddy family rather than a hierarchy.</summary>
    public static readonly Color Heading = new(0x9d, 0x7c, 0xd8);

    /// <summary>Code, inline and fenced. Green — the one element that must never be mistaken for
    /// prose.</summary>
    public static readonly Color Code = new(0x7f, 0xd8, 0x8f);

    /// <summary>
    /// Code background. opencode uses the WINDOW background here (no fill at all), letting colour
    /// alone separate code from prose. A near-black panel tint is kept instead: the transcript is a
    /// chat, so a fenced block sits inside flowing text rather than on its own screen, and it needs
    /// an edge the eye can find without reading.
    /// </summary>
    public static readonly Color CodeBackground = new(0x14, 0x14, 0x14);

    /// <summary>Blockquotes. Sand, italic in opencode's styling.</summary>
    public static readonly Color Quote = new(0xe5, 0xc0, 0x7b);

    /// <summary>Links. Peach — opencode's primary, the colour it gives whatever the user should
    /// reach for.</summary>
    public static readonly Color Link = new(0xfa, 0xb2, 0x83);

    /// <summary>Table and rule borders. Recedes.</summary>
    public static readonly Color MarkdownBorder = new(0x48, 0x48, 0x48);

    /// <summary>
    /// Reasoning and any "thinking" label. Amber — opencode's warning hue, which it reuses for
    /// exactly this, and the colour in the screenshot that prompted the comparison.
    /// </summary>
    public const string ThinkingMarkup = "#f5a742";
}

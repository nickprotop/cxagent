using SharpConsoleUI;
using SharpConsoleUI.Themes;

using SharpConsoleUI.Helpers;

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


    /// <summary>The accent, as markup. Kept beside <see cref="Accent"/> so the two cannot drift.</summary>
    public const string AccentMarkup = "cyan1";

    /// <summary>
    /// Failure, as markup — for text inside a string, where <see cref="Destructive"/> cannot reach.
    ///
    /// <para>A failed tool row used to be distinguished only by NOT being muted, on the reasoning
    /// that it must not recede. True but insufficient: "does not recede" left it the same colour as
    /// ordinary text, so the one row the user has to act on looked like every other finished one.</para>
    /// </summary>
    public const string DangerMarkup = "red";

    /// <summary>
    /// The accent as an RGB value — cyan1 is #00ffff — for the places a markup NAME cannot go.
    ///
    /// <para>Interpolation needs channels. The banner fades its wordmark from this to
    /// <see cref="Heading"/> across its width, and a per-column colour cannot be expressed as a
    /// palette name, so the value is spelled once here rather than inline at the one call site.</para>
    /// </summary>
    public static readonly Color AccentRgb = new(0x00, 0xff, 0xff);

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

    /// <summary>
    /// The LEFT column's surface — the transcript and the composer cell under it: almost black.
    ///
    /// <para>opencode's relationship, and the one worth copying: the chat column is the dark field
    /// you read against, and the side panel is the lighter surface beside it. Ours had it inverted —
    /// the panel was a shade lighter than a chat that was simply the app background — so the two
    /// columns did not read as two panes at all.</para>
    /// </summary>
    public static readonly Color ChatSurface = new(0x0d, 0x0d, 0x0d);


    /// <summary>
    /// The composer's surface — the prompt box AND the mode line under it.
    ///
    /// <para>ONE CONSTANT FOR BOTH, because they are one control as far as the eye is concerned. The
    /// prompt was picking up the framework's focused-edit background while the mode line sat on the
    /// app background, so the composer read as a grey box with an unrelated caption floating beneath
    /// it. Naming the surface here also means it no longer depends on which theme the framework
    /// resolves at focus time.</para>
    /// </summary>
    public static readonly Color ComposerSurface = new(0x2a, 0x2a, 0x2a);



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
    /// The line between the transcript and the composer.
    ///
    /// <para>DERIVED, not picked. It is the two surfaces it divides, mixed and lifted a little —
    /// which is what a divider between them actually is, and it means the line follows if either
    /// surface is ever retuned instead of quietly falling out of step with a hardcoded hex.</para>
    ///
    /// <para>ColorRole.Secondary — what it used first — is a THEME colour meant to carry meaning,
    /// and a divider carries none; borrowing it made the line the most saturated thing on a screen
    /// whose whole point is the text above it.</para>
    /// </summary>
    public static readonly Color Separator =
        PaletteColors.Tint(PaletteColors.Mix(ChatSurface, ComposerSurface, 0.5), 0.08);

    /// <summary>
    /// The permission prompt's surface: raised, but only just.
    ///
    /// <para>A prompt is the one moment the app stops and asks, and it needs to read as a different
    /// plane rather than as more content in the column. Dimming everything else was the first answer
    /// and it sat badly: it darkened a whole screen to highlight six rows, and the boundary between
    /// dimmed and undimmed landed wherever the prompt's height happened to fall. Raising ONE surface
    /// says the same thing, and nothing else on screen has to change.</para>
    ///
    /// <para>THE SEPARATOR'S COLOUR. A first attempt tinted the composer by 0.22 and read as a grey
    /// box pasted over the app — louder than the question printed on it. The separator is already
    /// the palette's answer to "one step up from these two surfaces", so reusing it gives the app
    /// ONE raised tone instead of two nearly-equal ones kept in sync by hand.</para>
    /// </summary>
    public static readonly Color PromptSurface = Separator;

    /// <summary>
    /// The user's own turns: a flat block, a step above the transcript field.
    ///
    /// <para>opencode marks whose turn it is with a SURFACE rather than a border. Ours drew a rounded
    /// box, which is chrome around the text instead of the text sitting on its own ground — and a box
    /// competes with the code blocks and tables that appear inside assistant answers, so the loudest
    /// frame on screen belonged to the shortest message.</para>
    ///
    /// <para>Derived from the chat field, like every other surface here, so retuning the field carries
    /// through instead of leaving this behind.</para>
    /// </summary>
    /// <summary>
    /// The grip: the vertical rule down the prompt's left edge.
    ///
    /// <para>opencode marks the two surfaces the USER owns — their turn and the composer — with a
    /// rule at the left. It is the same idea as <see cref="UserSurface"/> in a different register:
    /// the surface says "this block is yours", the grip says "this is where you type".</para>
    ///
    /// <para>Accent, because it is the one piece of chrome that tracks the app's active colour, and
    /// a grip that recedes has no reason to exist.</para>
    /// </summary>
    public static readonly Color Grip = AccentRgb;

    public static readonly Color UserSurface = PaletteColors.Tint(ChatSurface, 0.10);

    /// <summary>
    /// The assistant's prose: the same idea as <see cref="UserSurface"/>, one step quieter.
    ///
    /// <para>Both turns get ground of their own so the conversation reads as alternating blocks, but
    /// the user's sits higher: their turns are short and scanned for orientation ("what did I ask?"),
    /// while the assistant's are long and read continuously. A lighter block behind a screenful of
    /// prose would be the brightest region on the display.</para>
    ///
    /// <para>Tool rows are deliberately NOT given a surface — they are chrome, and they stay on the
    /// field so the two conversational voices are the only things raised off it.</para>
    /// </summary>
    public static readonly Color AssistantSurface = PaletteColors.Tint(ChatSurface, 0.05);

    /// <summary>
    /// Reasoning and any "thinking" label. Amber — opencode's warning hue, which it reuses for
    /// exactly this, and the colour in the screenshot that prompted the comparison.
    /// </summary>
    public const string ThinkingMarkup = "#f5a742";
}

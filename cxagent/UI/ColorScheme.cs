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
    // --- Deriving the palette from the active theme -------------------------------------

    /// <summary>
    /// The theme every derived colour here is currently expressed in, or null before
    /// <see cref="DeriveFrom"/> has run. Markup lookups fall back to their literal defaults while
    /// it is null, which is what keeps a headless or test host — one that never builds a window
    /// system — rendering exactly as it did before any of this existed.
    /// </summary>
    private static ITheme? _theme;

    /// <summary>
    /// Re-expresses every surface and markup token in <paramref name="theme"/>.
    ///
    /// <para>STATIC, DELIBERATELY, THOUGH AN INSTANCE READS BETTER. The palette has roughly seventy
    /// call sites across the UI, every one of them <c>ColorScheme.Something</c>; making it an
    /// instance would rewrite all of them to reach a field, for a type that has exactly one live
    /// value per process. The seam that matters — re-derivation on a theme change — is served just
    /// as well by a method the window system calls.</para>
    ///
    /// <para>THE DEFAULTS ARE TODAY'S LITERALS, and they stay as the field initialisers rather than
    /// being deleted, so a host that never calls this renders the palette this app shipped with.</para>
    /// </summary>
    /// <param name="theme">The active theme to express the palette in.</param>
    public static void DeriveFrom(ITheme theme)
    {
        _theme = theme;

        // THE CHAT SURFACE IS THE BASIS, not one derivation among several: every other surface here
        // is a step away from it, so taking it from the theme moves the whole palette together and
        // keeps the relationships the old literals encoded by hand.
        ChatSurface = theme.WindowBackgroundColor;

        // THE STEPS ARE THE OLD DISTANCES, re-measured. The literals were 0x0d, 0x1e and 0x2a — a
        // seventeen-point step to the panel and a further twelve to the composer, out of 255. Those
        // ratios are what the amounts below reproduce, so ModernGray renders as it did before.
        PanelSurface = Raised(ChatSurface, 0.0705);
        ComposerSurface = Raised(ChatSurface, 0.12);

        // THE ACCENT AS CHANNELS, for the two places a markup NAME cannot go — the wordmark's
        // per-column gradient and the grip. Derived here rather than read at each site so it moves
        // with a theme change like everything else.
        AccentRgb = ColorRoleResolver.Resolve(Accent, theme).Text;

        Separator = PaletteColors.Tint(PaletteColors.Mix(ChatSurface, ComposerSurface, 0.5), 0.08);
        UserSurface = Raised(ChatSurface, 0.10);
        AssistantSurface = Raised(ChatSurface, 0.05);
    }

    /// <summary>
    /// A surface that reads as RAISED above <paramref name="basis"/>, whichever way the theme runs.
    ///
    /// <para>ON A DARK THEME THAT MEANS LIGHTER; ON A LIGHT THEME IT MEANS DARKER. Tinting
    /// unconditionally is precisely what makes a dark-only palette look broken under a light theme
    /// rather than merely different: every panel blows out toward white and the separations between
    /// them disappear. The direction has to follow the surface, not the author's habits.</para>
    /// </summary>
    private static Color Raised(Color basis, double amount) =>
        basis.IsDark() ? PaletteColors.Tint(basis, amount) : PaletteColors.Shade(basis, amount);

    /// <summary>
    /// The markup token for <paramref name="role"/> under the active theme, or
    /// <paramref name="fallback"/> when no theme has been derived from yet.
    ///
    /// <para>THE BRIDGE THIS FILE ONCE SAID DID NOT EXIST. Its own summary claimed "the framework
    /// offers no role-to-markup bridge", which was true when it was written: what closes the gap is
    /// <c>ColorRoleResolver.Resolve</c> — already used for controls — composed with
    /// <c>Color.ToMarkup</c>. Neither is new; nobody had put them together.</para>
    /// </summary>
    private static string Markup(ColorRole role, string fallback) =>
        _theme is { } theme
            ? ColorRoleResolver.Resolve(role, theme).Text.ToMarkup()
            : fallback;

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
    /// <para>DERIVED, NOT NAMED. There is no "muted" role — muted is a RELATIONSHIP, the foreground
    /// carried most of the way toward the background, which is a different literal under every theme
    /// and unreadable if it stays grey50 on a light one.</para>
    public static string MutedMarkup =>
        _theme is { } theme
            ? PaletteColors.Mix(theme.WindowForegroundColor, theme.WindowBackgroundColor, 0.55).ToMarkup()
            : "grey50";


    /// <summary>The accent, as markup. Kept beside <see cref="Accent"/> so the two cannot drift.</summary>
    public static string AccentMarkup => Markup(Accent, "cyan1");

    /// <summary>
    /// Failure, as markup — for text inside a string, where <see cref="Destructive"/> cannot reach.
    ///
    /// <para>A failed tool row used to be distinguished only by NOT being muted, on the reasoning
    /// that it must not recede. True but insufficient: "does not recede" left it the same colour as
    /// ordinary text, so the one row the user has to act on looked like every other finished one.</para>
    /// </summary>
    public static string DangerMarkup => Markup(Destructive, "red");

    /// <summary>
    /// The accent as an RGB value — cyan1 is #00ffff — for the places a markup NAME cannot go.
    ///
    /// <para>Interpolation needs channels. The banner fades its wordmark from this to
    /// <see cref="Heading"/> across its width, and a per-column colour cannot be expressed as a
    /// palette name, so the value is spelled once here rather than inline at the one call site.</para>
    /// </summary>
    public static Color AccentRgb { get; private set; } = new(0xe8, 0x9e, 0x64);

    /// <summary>
    /// Colour for a percentage readout, by how alarming it is. cxtop's thresholds — teal below 60,
    /// amber below 85, red above — which is the family's only precedent for a live percentage, and
    /// the one number a user reads at a glance rather than by reading.
    /// </summary>
    /// <para>THE THRESHOLDS ARE UNCHANGED; only the colours they name now follow the theme. The
    /// three bands are accent, caution and refusal — the same three meanings the buttons carry, so
    /// they resolve through the same roles rather than through three more literals.</para>
    public static string ThresholdMarkup(double percent) =>
        percent < 60 ? AccentMarkup
        : percent < 85 ? Markup(Caution, "yellow")
        : DangerMarkup;

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
    public static Color PanelSurface { get; private set; } = new(0x1e, 0x1e, 0x1e);

    /// <summary>
    /// The LEFT column's surface — the transcript and the composer cell under it: almost black.
    ///
    /// <para>opencode's relationship, and the one worth copying: the chat column is the dark field
    /// you read against, and the side panel is the lighter surface beside it. Ours had it inverted —
    /// the panel was a shade lighter than a chat that was simply the app background — so the two
    /// columns did not read as two panes at all.</para>
    /// </summary>
    public static Color ChatSurface { get; private set; } = new(0x0d, 0x0d, 0x0d);


    /// <summary>
    /// The composer's surface — the prompt box AND the mode line under it.
    ///
    /// <para>ONE CONSTANT FOR BOTH, because they are one control as far as the eye is concerned. The
    /// prompt was picking up the framework's focused-edit background while the mode line sat on the
    /// app background, so the composer read as a grey box with an unrelated caption floating beneath
    /// it. Naming the surface here also means it no longer depends on which theme the framework
    /// resolves at focus time.</para>
    /// </summary>
    public static Color ComposerSurface { get; private set; } = new(0x2a, 0x2a, 0x2a);



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
    public static Color Separator { get; private set; } =
        PaletteColors.Tint(PaletteColors.Mix(new Color(0x0d, 0x0d, 0x0d), new Color(0x2a, 0x2a, 0x2a), 0.5), 0.08);

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
    public static Color PromptSurface => Separator;

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
    public static Color Grip => AccentRgb;

    public static Color UserSurface { get; private set; } = PaletteColors.Tint(new Color(0x0d, 0x0d, 0x0d), 0.10);

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
    public static Color AssistantSurface { get; private set; } = PaletteColors.Tint(new Color(0x0d, 0x0d, 0x0d), 0.05);

    /// <summary>
    /// Reasoning and any "thinking" label. Amber — opencode's warning hue, which it reuses for
    /// exactly this, and the colour in the screenshot that prompted the comparison.
    /// </summary>
    /// <para>THROUGH THE CAUTION ROLE, because amber IS this theme's warning hue and a theme that
    /// picks a different one means it for the same reason. The literal stays as the fallback so a
    /// host with no theme renders the colour the screenshots were taken with.</para>
    public static string ThinkingMarkup => Markup(Caution, "#f5a742");

    // --- Diff -------------------------------------------------------------------
    //
    // GitHub's dark-theme diff colours, and the reason to borrow rather than invent is that these
    // are the two colours every developer already reads without thinking. A palette of our own would
    // be a small novelty tax on the one surface where recognition matters most.
    //
    // FOUR VALUES, NOT TWO. Each direction needs a ROW ground and a stronger SPAN ground on top of
    // it: the row says "this line changed", the span says "this is the part that changed". One
    // colour for both loses the intra-line highlight that is the whole point of the word-diff.
    //
    // NOT DangerMarkup / a green from Code. Those are semantic — failure and source code — and a
    // removed line is neither failing nor code-as-such. Reusing them would mean a theme change that
    // wanted louder errors also repainted every diff.

    /// <summary>Removed text.</summary>
    public const string DiffRemovedMarkup = "#f85149";

    /// <summary>The ground under a removed row.</summary>
    public const string DiffRemovedRow = "#3a0d0d";

    /// <summary>The ground under the changed RUN inside a removed row — brighter than the row, so
    /// the eye lands on the words that moved rather than on the line that holds them.</summary>
    public const string DiffRemovedSpan = "#5a1a1a";

    /// <summary>Added text.</summary>
    public const string DiffAddedMarkup = "#7ee787";

    /// <summary>The ground under an added row.</summary>
    public const string DiffAddedRow = "#0d3a1a";

    /// <summary>The ground under the changed RUN inside an added row.</summary>
    public const string DiffAddedSpan = "#1a5a2a";
}

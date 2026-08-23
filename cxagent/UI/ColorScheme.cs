using SharpConsoleUI;
using SharpConsoleUI.Configuration;
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
    /// <para>THE FIELD INITIALISERS ARE KEPT as real literals rather than being removed once this
    /// derives them, so a host that never calls this still renders the app's own palette.</para>
    /// </summary>
    /// <param name="theme">The active theme to express the palette in.</param>
    public static void DeriveFrom(ITheme theme)
    {
        _theme = theme;

        // THE CHAT SURFACE IS THE BASIS, not one derivation among several: every other surface here
        // is a step away from it, so taking it from the theme moves the whole palette together and
        // keeps the relationships between surfaces fixed across themes.
        ChatSurface = theme.WindowBackgroundColor;

        // THE STEPS ARE HAND-MEASURED DISTANCES. The reference greys are 0x0d, 0x1e and 0x2a — a
        // seventeen-point step to the panel and a further twelve to the composer, out of 255. The
        // ratios below reproduce those, which is what makes ModernGray render as designed.
        PanelSurface = Raised(ChatSurface, 0.0705);
        ComposerSurface = Raised(ChatSurface, 0.12);

        // THE ACCENT AS CHANNELS, for the two places a markup NAME cannot go — the wordmark's
        // per-column gradient and the grip. Derived here rather than read at each site so it moves
        // with a theme change like everything else.
        AccentRgb = ColorRoleResolver.Resolve(Accent, theme).Text;

        Separator = PaletteColors.Tint(PaletteColors.Mix(ChatSurface, ComposerSurface, 0.5), 0.08);
        UserSurface = Raised(ChatSurface, 0.10);
        AssistantSurface = Raised(ChatSurface, 0.05);

        // MARKDOWN FOLLOWS THE THEME, UNLESS THE THEME HAS ALREADY CHOSEN. A crimson theme must
        // give crimson headings, not a fixed purple wearing crimson chrome around it — so every
        // theme but cxagent's own derives from AccentRgb rather than adjusting the contrast of a
        // EVERY THEME DERIVES, cxagent's own included. A palette declared per-theme would make
        // the app's markdown answer to two different rules depending on which theme is active, and
        // the one that opted out would be the one nobody ever saw adapt.
        MarkdownStyle style = BuildMarkdownStyle(theme);
        MarkdownStyle.Default = style;

        // HEADING AND CODE SURVIVE AS PROPERTIES, read back from the style just installed rather
        // than recomputed, so non-markdown readers match the transcript's own colours bit-for-bit
        // instead of drifting if the two derivations were ever allowed to diverge. The banner's
        // wordmark fades between them (Heading alone collapsed the fade flat once Heading became
        // the accent itself — Code is guaranteed >=30 luminance away by BuildMarkdownStyle, so the
        // gradient always travels), and the theme portal's foreground uses Heading directly.
        Heading = style.H1Color!.Value;
        Code = style.CodeForeground;
    }

    /// <summary>
    /// Builds the markdown style for <paramref name="theme"/>. Split out from
    /// <see cref="DeriveFrom"/> so a test can read the style back without going through
    /// <see cref="MarkdownStyle.Default"/>'s process-wide setter.
    ///
    /// <para>HEADING AND CODE ARE ONE HUE, SEPARATED BY LUMINANCE, not two hues. Both come from the
    /// theme's accent — the same fact <see cref="DeriveFrom"/> resolves separately for
    /// <see cref="AccentRgb"/>, used by the wordmark and the grip — because a document's structure
    /// is the theme's to colour, and inventing a second accent-independent hue for code would leave
    /// code the one element that does not move when the theme does. Code is heading itself, stepped
    /// further in the SAME direction <see cref="PaletteColors.EnsureContrast"/> already moved heading
    /// away from the surface — see the comment on that step for why deriving code from the raw
    /// accent independently is not safe.</para>
    ///
    /// <para>TWO GAPS, BY ROLE. Heading, code, quote and link carry text, so they run through
    /// <see cref="PaletteColors.EnsureContrast"/> at the default 80-point gap — what makes text
    /// readable. The border is chrome: a rule is meant to recede, and demanding text-grade
    /// separation from it asks the wrong question, so it gets its own smaller 55-point gap instead
    /// of the 80 the others use.</para>
    /// </summary>
    private static MarkdownStyle BuildMarkdownStyle(ITheme theme)
    {
        Color surface = theme.WindowBackgroundColor;
        Color foreground = theme.WindowForegroundColor;
        Color accent = ColorRoleResolver.Resolve(Accent, theme).Text;

        // HEADINGS ARE THE ACCENT ITSELF — the strongest, least-mixed expression of it, since
        // structure is the most prominent thing on the page after the text itself.
        Color heading = PaletteColors.EnsureContrast(accent, surface);

        // CODE BACKGROUND IS DERIVED FROM THE WINDOW BACKGROUND, not a fixed near-black literal — a
        // fixed #141414 is a black box on a light theme, wrong in DIRECTION rather than merely
        // low-contrast, so it has to be stepped off the surface's own side rather than nudged toward
        // more contrast. Computed BEFORE code, because code's own contrast has to be checked against
        // the ground it actually renders on.
        Color codeBackground = Raised(surface, 0.10);

        // CODE IS `heading` STEPPED FURTHER AWAY FROM THE SURFACE, NOT the raw accent adjusted on
        // its own. Deriving code from the accent independently and only afterward checking it
        // differs from heading does not work: running both through EnsureContrast separately can
        // nudge them toward the SAME legible luminance (measured: as close as 5 points apart) and
        // collapse the separation this whole scheme exists to preserve. Stepping FURTHER in the same
        // direction EnsureContrast already moved heading — away from the surface — guarantees the
        // gap by construction instead of hoping two independent derivations land apart, and it
        // cannot reduce heading's own contrast because it moves the same way, only more. 0.6 is
        // measured, not guessed: 0.5 left ModernGray's own accent 27 points apart, just under the
        // 30 this must clear, so the step is the smallest amount that keeps every case checked here
        // — a saturated dark-theme accent, a light-theme one already pushed to its EnsureContrast
        // floor, and ModernGray's own default accent — at or above the required gap.
        //
        // THE FINAL EnsureContrast IS AGAINST codeBackground, NOT surface — code renders on the code
        // background, not the window background, so that is the pair that has to stay readable.
        // codeBackground is only a 0.10 step off surface, so this cannot undo the separation above:
        // it can only push code further in the SAME direction the Tint/Shade step already moved it.
        Color code = PaletteColors.EnsureContrast(
            surface.IsDark() ? heading.Tint(0.6) : heading.Shade(0.6), codeBackground);

        // QUOTE IS A MIX OF FOREGROUND AND BACKGROUND — a secondary voice with no hue of its own,
        // so it recedes under any theme instead of competing with the accent that headings and code
        // both carry.
        Color quote = PaletteColors.EnsureContrast(PaletteColors.Mix(foreground, surface, 0.35), surface);

        // LINKS ARE THE ACCENT TOO: "the thing to reach for" is exactly what an accent means, and a
        // link that did not track the theme's own idea of "reach for this" would be the odd one out
        // among headings and code, which do.
        Color link = PaletteColors.EnsureContrast(accent, surface);

        // BORDER IS A FOREGROUND/BACKGROUND MIX, further toward the background than quote — chrome,
        // not content, so it carries no hue and gets the smaller 55-point gap: enough to stay
        // visible, not enough to compete with the text it frames.
        Color border = PaletteColors.EnsureContrast(
            PaletteColors.Mix(foreground, surface, 0.5), surface, minGap: 55.0);

        return new MarkdownStyle
        {
            // ONE colour at every level. The hue does not step down by depth — h1 is distinguished
            // by underline alone — and stepping it produced exactly the muddiness this fixes.
            H1Color = heading,
            H2Color = heading,
            H3Color = heading,
            H4Color = heading,
            H5Color = heading,
            H6Color = heading,

            CodeForeground = code,
            CodeBackground = codeBackground,
            QuoteColor = quote,
            LinkColor = link,
            BorderColor = border,
        };
    }

    /// <summary>Test seam: the style <see cref="DeriveFrom"/> installs, read back without going
    /// through <see cref="MarkdownStyle.Default"/>'s "an app has explicitly chosen" side effect.
    /// Public rather than internal because this assembly grants no InternalsVisibleTo, and the
    /// ForTest suffix follows the seam convention used elsewhere here.</summary>
    public static MarkdownStyle MarkdownStyleForTest(ITheme theme) => BuildMarkdownStyle(theme);


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
    /// <para>A failed tool row needs a colour of its own, not merely the absence of muting. "Does
    /// not recede" is true but insufficient: unmuted alone leaves it the same colour as ordinary
    /// text, so the one row the user has to act on looks like every other finished one.</para>
    /// </summary>
    public static string DangerMarkup => Markup(Destructive, "red");

    /// <summary>
    /// Caution, as markup — for text inside a string, where <see cref="Caution"/> cannot reach.
    ///
    /// <para>Added beside <see cref="DangerMarkup"/> for the observer's <c>Warning</c> severity: a
    /// sink colouring by <c>Message.Severity</c> needs the same bridge for the amber case that
    /// already exists for the red one, rather than reaching for a role that resolves to nothing
    /// inside an interpolated string.</para>
    /// </summary>
    public static string CautionMarkup => Markup(Caution, "yellow");

    /// <summary>
    /// Affirmative, as markup — for text inside a string, where <see cref="Affirmative"/> cannot
    /// reach.
    ///
    /// <para>The third of the severity-shaped bridges, beside <see cref="DangerMarkup"/> and
    /// <see cref="CautionMarkup"/>: a startup line reporting that something WORKED resolves its
    /// green through the theme rather than naming one, so it stays legible on a light ground.</para>
    /// </summary>
    public static string AffirmativeMarkup => Markup(Affirmative, "green");

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
        : percent < 85 ? CautionMarkup
        : DangerMarkup;

    /// <summary>
    /// The session panel's surface — one step off the window background, so the column reads as a
    /// different KIND of thing rather than as narrow transcript.
    ///
    /// <para>DELIBERATELY NOT the same as the markdown code background. Both started at #141414 and
    /// the collision was visible immediately: inline code spans in the transcript painted the
    /// identical grey, so the panel stopped reading as a surface and read
    /// as more scattered code. Two different meanings cannot share one colour when they appear side
    /// by side.</para>
    /// </summary>
    public static Color PanelSurface { get; private set; } = new(0x1e, 0x1e, 0x1e);

    /// <summary>
    /// The LEFT column's surface — the transcript and the composer cell under it: almost black.
    ///
    /// <para>THE CHAT COLUMN IS THE DARK FIELD you read against, and the side panel is the lighter
    /// surface beside it. Ours had it inverted — the panel was a shade lighter than a chat that was
    /// simply the app background — so the two
    /// columns did not read as two panes at all.</para>
    /// </summary>
    public static Color ChatSurface { get; private set; } = new(0x0d, 0x0d, 0x0d);


    /// <summary>
    /// The composer's surface — the prompt box AND the mode line under it.
    ///
    /// <para>ONE CONSTANT FOR BOTH, because they are one control as far as the eye is concerned.
    /// Left to their defaults the prompt picks up the framework's focused-edit background while the
    /// mode line sits on the app background, so the composer reads as a grey box with an unrelated
    /// caption floating beneath it. Naming the surface here also keeps it independent of whichever
    /// theme the framework resolves at focus time.</para>
    /// </summary>
    public static Color ComposerSurface { get; private set; } = new(0x2a, 0x2a, 0x2a);



    // --- Markdown ---------------------------------------------------------------
    //
    // The markdown palette lives in BuildMarkdownStyle: every colour there derives from AccentRgb,
    // the window foreground or the window background, so there is nothing left to seed here as a
    // literal. Heading and Code survive as properties, read back from the style DeriveFrom just
    // installed, because two non-markdown surfaces read them — the banner's wordmark fade and the
    // theme portal's foreground.

    /// <summary>
    /// The markdown heading colour, kept readable outside <see cref="BuildMarkdownStyle"/> because
    /// the theme portal's foreground borrows it rather than inventing a colour of its own.
    ///
    /// <para>THE SAME AS <see cref="AccentRgb"/> ON MOST THEMES, and deliberately: headings ARE the
    /// accent, so anything wanting a SECOND colour must take <see cref="Code"/> instead — see the
    /// banner, whose fade collapses to one flat colour if it lerps between these two.</para>
    /// </summary>
    public static Color Heading { get; private set; } = new(0xe8, 0x9e, 0x64);

    /// <summary>
    /// The markdown code colour — the app's genuine SECOND colour.
    ///
    /// <para>Guaranteed a visible luminance step from <see cref="Heading"/> by construction, which
    /// is what makes it the right endpoint for anything needing two colours from one theme. The
    /// banner's wordmark fade is the case that proves it: heading and accent are the same value on
    /// any theme whose accent already reads against its ground, so a fade between THOSE two travels
    /// nowhere.</para>
    /// </summary>
    public static Color Code { get; private set; } = new(0xf4, 0xce, 0xb2);


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
    /// <para>THE SEPARATOR'S COLOUR, not a tint of its own. Lifting the composer by its own amount
    /// (0.22 was measured) reads as a grey box pasted over the app — louder than the question
    /// printed on it. The separator is already the palette's answer to "one step up from these two
    /// surfaces", so reusing it gives the app ONE raised tone instead of two nearly-equal ones kept
    /// in sync by hand.</para>
    /// </summary>
    public static Color PromptSurface => Separator;

    /// <summary>
    /// The user's own turns: a flat block, a step above the transcript field.
    ///
    /// <para>A SURFACE RATHER THAN A BORDER marks whose turn it is. Ours drew a rounded box, which
    /// is chrome around the text instead of the text sitting on its own ground — and a box
    /// competes with the code blocks and tables that appear inside assistant answers, so the loudest
    /// frame on screen belonged to the shortest message.</para>
    ///
    /// <para>Derived from the chat field, like every other surface here, so retuning the field carries
    /// through instead of leaving this behind.</para>
    /// </summary>
    /// <summary>
    /// The grip: the vertical rule down the prompt's left edge.
    ///
    /// <para>The two surfaces the USER owns — their turn and the composer — are marked with a rule
    /// at the left. It is the same idea as <see cref="UserSurface"/> in a different register:
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
    /// Reasoning and any "thinking" label. Amber — the palette's warning hue, reused for exactly
    /// this.
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

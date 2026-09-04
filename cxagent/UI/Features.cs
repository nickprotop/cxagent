namespace CxAgent.UI;

/// <summary>
/// The decisions a developer makes about this app and rebuilds — what is offered, and what it looks
/// like when nothing else says.
///
/// <para>ONE PLACE, SO NOBODY HAS TO GO LOOKING. A switch living inside the thing it governs reads
/// naturally to whoever wrote it and is invisible to everyone else; a reader wondering "why does it
/// do that, and can I change it?" should have one file to open rather than a codebase to search.</para>
///
/// <para>NOT CONFIGURATION, AND THE DISTINCTION MATTERS. These are values a developer flips and
/// rebuilds, never keys a user sets. Anything that becomes a matter of a user's taste rather than
/// this app's judgement belongs in config.json with their real preferences instead.</para>
///
/// <para>TWO KINDS LIVE HERE, and the doc on each says which it is. A GATE covers work that is built
/// but not finished — <see cref="ThemePicker"/> — and is expected to be short-lived: one that has
/// been false for a long time is either a feature nobody finished or one nobody wanted, and both
/// deserve a decision rather than being left switched off forever. A DEFAULT is settled and stays —
/// <see cref="DefaultTheme"/>, <see cref="CollapsedPeek"/> — a choice made once about how the app
/// behaves, written down where it can be found and reconsidered rather than buried at its call
/// site.</para>
///
/// <para>READONLY FIELDS RATHER THAN CONSTS for the gates, deliberately. A <c>const false</c> makes
/// every call behind it literally unreachable and the compiler says so — a warning per gated
/// feature, in a codebase that builds at zero. A static readonly reads identically at the call site
/// and leaves the gated code compiling like any other. A default that nothing is gated on can be a
/// const.</para>
/// </summary>
public static class Features
{
    /// <summary>
    /// Whether the theme PICKER is offered — the F9 shortcut, the status-bar item and its portal.
    ///
    /// <para>OFF. The remaining edges are cosmetic but visible: text already in the transcript keeps
    /// the colours it was written with (a deliberate decision — scrollback is append-only), and a
    /// live switch can leave stray escape sequences that even a full repaint does not always clear.
    /// Everything behind this is built and tested; set it true to bring the picker back.</para>
    ///
    /// <para>Independent of <see cref="ThemeSelection"/>: choosing a theme in config works whether or
    /// not the interactive picker is offered.</para>
    /// </summary>
    public static readonly bool ThemePicker = true;

    /// <summary>
    /// Whether a theme may be chosen at all — the <c>theme</c> key in config.json and the
    /// <c>--theme</c> command-line argument.
    ///
    /// <para>ON. Selecting a theme at startup has none of the picker's difficulty: the palette is
    /// derived once before anything is painted, so there is no live switch to leave artifacts and no
    /// transcript already written in the previous theme's colours.</para>
    ///
    /// <para>With this off, cxagent always starts in <see cref="DefaultTheme"/> and both the config
    /// key and the argument are ignored.</para>
    /// </summary>
    public static readonly bool ThemeSelection = true;

    /// <summary>
    /// The theme cxagent starts in when nothing else names one, and the fallback when something
    /// names one that does not exist.
    ///
    /// <para>Its own palette rather than the framework's default: ModernGray's background is lighter
    /// than the near-black this app was designed against, so starting there would change every
    /// surface. See <see cref="CxAgentTheme"/>.</para>
    /// </summary>
    public const string DefaultTheme = CxAgentTheme.Name;

    /// <summary>
    /// Whether a collapsed message shows a one-line preview of what it is hiding, with a clickable
    /// <c>expand…</c> cue.
    ///
    /// <para>OFF, BECAUSE THE FIRST LINE IS RARELY THE CONTENT. Only tool rows start collapsed here,
    /// and their opening line is scaffolding: a worker's body begins with the frontmatter above its
    /// report, so the preview reads <c>- type: explore</c>, and a folded tool row's begins with the
    /// head of a markdown table. Both take a full row to say nothing, and the fade that marks them as
    /// a preview makes them look like content that failed to render.</para>
    ///
    /// <para>THE TRIANGLE STILL SAYS THERE IS SOMETHING. Losing the cue costs a word that named the
    /// gesture; the row is one line either way and still opens on a click.</para>
    ///
    /// <para>A DEFAULT, NOT A GATE: the preview works, and this is a judgement about how a folded row
    /// should read. Set it true to bring the preview back.</para>
    /// </summary>
    public const bool CollapsedPeek = false;
}

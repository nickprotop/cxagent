namespace CxAgent.UI;

/// <summary>
/// Switches for work that is BUILT BUT NOT FINISHED, and the defaults that go with them.
///
/// <para>ONE PLACE, SO NOBODY HAS TO GO LOOKING. A flag living inside the feature it gates reads
/// naturally to whoever wrote it and is invisible to everyone else; a reader wondering "is this off,
/// and why?" should have one file to open rather than a codebase to search.</para>
///
/// <para>NOT CONFIGURATION, AND THE DISTINCTION MATTERS. These are values a developer flips and
/// rebuilds, never keys a user sets. A setting says "you may prefer this off"; a flag here says
/// "this is not ready", which is a claim only the person shipping it can make. Anything that
/// outgrows that belongs in config.json with the user's real preferences instead.</para>
///
/// <para>READONLY FIELDS RATHER THAN CONSTS, deliberately. A <c>const false</c> makes every call
/// behind it literally unreachable and the compiler says so — a warning per gated feature, in a
/// codebase that builds at zero. A static readonly reads identically at the call site and leaves the
/// gated code compiling like any other.</para>
///
/// <para>These are expected to be SHORT-LIVED. A flag that has been false for a long time is either
/// a feature nobody finished or one nobody wanted, and both deserve a decision rather than being
/// left switched off forever.</para>
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
    public static readonly bool ThemePicker = false;

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
}

namespace CxAgent.Core.Commands;

/// <summary>
/// The tones Core's own text is written in.
///
/// <para>TEMPORARY REMNANT — SCHEDULED FOR DELETION. <c>Md</c> replaces this class; its
/// <c>Escape</c> is gone because it escaped for markup, the format Core is leaving, not markdown, the
/// one it is arriving at. What survives here is the four colour constants below, because 42 call
/// sites across ten files still reference them (<c>DiffCommand</c>, <c>StatsDashboard</c>,
/// <c>Session.Commands</c>, <c>ModelCommand</c>, <c>AgentsCommand</c>, <c>StatsCommand</c>,
/// <c>SkillsCommand</c>, <c>SessionsCommand</c>, <c>ModeCommand</c>, <c>Session.Turn</c>). Later
/// tasks rewrite each of those in markdown terms — some are sentences, some are table layouts, some
/// are bar charts, so no single mechanical replacement covers them — and this file is deleted once
/// the last one is gone.</para>
///
/// <para>MARKUP IS A TEXT FORMAT, NOT A UI DEPENDENCY — the same way markdown is. A reader renders
/// it, strips it or logs it, and Core has written it for a long time: PermissionDecider's
/// <c>[yellow]</c> for "did not work, nothing was denied", ModelSwitchNotice's window warnings. What
/// would be a dependency is reaching into a specific front end's palette class to ask what its grey
/// is, which is what these constants replace.</para>
///
/// <para>SEMANTIC NAMES, DELIBERATELY. A caller writing <c>Muted</c> is saying "this is secondary",
/// not "this is grey" — so a front end that renders emphasis some other way has something to map
/// rather than a colour to obey. The values match the TUI's palette today because there is one front
/// end; the names are what survive a second.</para>
/// </summary>
public static class Markup
{
    /// <summary>Secondary text — explanations, counts, the parts a reader skims.</summary>
    public const string Muted = "grey50";

    /// <summary>The subject of the line: a model name, a command, a heading.</summary>
    public const string Accent = "cyan1";

    /// <summary>Something failed and nothing happened.</summary>
    public const string Danger = "red";

    /// <summary>Something did not work as asked but was not a failure — a fallback taken, a request
    /// declined, a mode that changes nothing here.</summary>
    public const string Caution = "yellow";
}

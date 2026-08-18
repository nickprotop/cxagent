namespace CxAgent.Core.Sessions;

/// <summary>
/// When a file write happens without asking.
///
/// <para>A SECOND AXIS ON <see cref="WorkingMode"/>, which is what that record was built to hold: its
/// doc named "whether it may write files" as the next one, and until now there was nothing in it but
/// delegation.</para>
///
/// <para>MODES NARROW, TRUST BOUNDS. A mode may add friction and may never remove it below what the
/// folder's trust permits — <c>AcceptEdits</c> on an untrusted folder still asks. Claude Code lets
/// its mode override everything because it has no per-folder trust concept; cxagent has one, and a
/// user who declined to trust a folder said something specific. Overriding that would make the trust
/// prompt's promise false, and a promise that is sometimes false should not be shown.</para>
/// </summary>
public enum EditMode
{
    /// <summary>
    /// Every file write asks, even inside the working directory on a trusted folder.
    ///
    /// <para>FIRST, SO IT IS THE ZERO VALUE, and that ordering is load-bearing rather than
    /// alphabetical. <c>WorkingMode</c> is a record STRUCT, so <c>new WorkingMode()</c> and
    /// <c>default(WorkingMode)</c> zero-initialise and IGNORE the parameter defaults — a struct that
    /// gets default-constructed anywhere lands here. Putting the permissive mode at zero would mean
    /// every such site silently opted into silent writes; putting the strict one here means the
    /// worst a forgotten initialiser can do is ask too often.</para>
    ///
    /// <para>Use <see cref="WorkingMode.Default"/> when you want the session default. It is the only
    /// thing that says <c>AcceptEdits</c>, and it says it explicitly.</para>
    ///
    /// <para>STORED RULES STILL APPLY. This suppresses the boundary free pass — the silent path
    /// nobody opted into per-item — not the decisions a user made one at a time. A mode that voided
    /// saved rules would read as the rules feature being broken, and the user would never be told
    /// the mode had done it.</para>
    /// </summary>
    AlwaysAsk,

    /// <summary>
    /// Writes inside the working directory are silent on a trusted folder; everywhere else asks.
    ///
    /// <para>THE DEFAULT, because it NAMES WHAT CXAGENT ALREADY DID. The axis ships as a pure
    /// addition — no session behaves differently on the day it lands — and that is the only reason a
    /// permissive default is defensible here.</para>
    ///
    /// <para>FILE TOOLS ONLY. Shell is unchanged in every mode. An earlier design also silenced a
    /// list of in-cwd write commands (<c>mkdir touch mv cp sed</c>), mirroring Claude Code; security
    /// review killed it. <see cref="Permissions.ReadOnlyCommands"/> matches the FIRST WORD ONLY and
    /// never inspects arguments, which is correct for read-only verbs — <c>grep</c> is harmless
    /// wherever it points — and exactly wrong for write verbs, whose entire safety lives in their
    /// arguments. <c>mkdir</c> is safe; <c>mkdir /etc/x</c> is not.</para>
    /// </summary>
    AcceptEdits,

    /// <summary>
    /// A classifier model reviews each otherwise-prompting write and answers allow-or-ask.
    ///
    /// <para>NOT OFFERED UNLESS CONFIGURED, and it FAILS CLOSED: any outcome that is not an explicit
    /// allow — timeout, transport error, malformed body, unrecognised verdict — asks.</para>
    ///
    /// <para>A CONVENIENCE, NOT A SECURITY BOUNDARY. Its input derives from file contents and command
    /// strings, so it is attacker-influenced by construction; trust still floors it.</para>
    /// </summary>
    Auto,
}

/// <summary>Parsing and display for <see cref="EditMode"/>, shared by the CLI, <c>/mode</c> and the
/// composer so the three can never disagree about what a mode is called.</summary>
public static class EditModes
{
    /// <summary>What the user types and reads. Hyphenated for the same reason "fan-out" is: nobody
    /// types "alwaysask" at a prompt, and "always_ask" is not a word either.</summary>
    public static string Name(EditMode mode) => mode switch
    {
        EditMode.AlwaysAsk => "always-ask",
        EditMode.Auto => "auto",
        _ => "accept-edits",
    };

    /// <summary>
    /// Every value a user may select, for an error that says what to type instead of only saying they
    /// were wrong.
    ///
    /// <para><c>auto</c> appears only when a classifier is configured — a mode that claims background
    /// review while nothing reviews is worse than no mode at all.</para>
    /// </summary>
    public static string Valid => ValidWith(classifierConfigured: false);

    /// <summary>The selectable values, given whether a classifier exists. See <see cref="Valid"/>.
    /// </summary>
    public static string ValidWith(bool classifierConfigured) =>
        classifierConfigured ? "always-ask, accept-edits, auto" : "always-ask, accept-edits";

    /// <summary>
    /// Parses a mode name, or returns null.
    ///
    /// <para>Tolerant of the near-misses people actually type — "alwaysask", "acceptedits" — because
    /// rejecting those teaches nothing and costs a restart when it comes from the command line. Not
    /// tolerant of anything else: a value that silently defaults is how someone concludes the mode is
    /// broken when they merely misspelled it.</para>
    ///
    /// <para><c>auto</c> is unparseable unless a classifier is configured, so neither a CLI flag nor
    /// <c>/mode</c> can reach a mode that would do nothing.</para>
    /// </summary>
    public static EditMode? Parse(string? text, bool classifierConfigured = false) =>
        text?.Trim().ToLowerInvariant() switch
        {
            "always-ask" or "alwaysask" or "always_ask" or "ask" => EditMode.AlwaysAsk,
            "accept-edits" or "acceptedits" or "accept_edits" or "edits" => EditMode.AcceptEdits,
            "auto" when classifierConfigured => EditMode.Auto,
            _ => null,
        };
}

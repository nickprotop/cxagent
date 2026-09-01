using CxAgent.Core.Llm;

namespace CxAgent.Core.Commands;

/// <summary>What <c>/plugin</c> asked for, parsed from the raw argument text.</summary>
public abstract record PluginRequest
{
    private PluginRequest() { }

    /// <summary>Bare <c>/plugin</c> — list every configured plugin and anything loaded by path.</summary>
    public sealed record List : PluginRequest;

    /// <summary><c>/plugin load &lt;name|path&gt;</c>.</summary>
    /// <param name="Target">The plugin to load — a configured name, a bare filename searched the
    /// plugin folders, or a path.</param>
    /// <param name="Settings">Inline settings written after the target as a JSON object, or null
    /// when none were given — see <see cref="PluginCommand.Parse"/>.</param>
    public sealed record Load(string Target, string? Settings = null) : PluginRequest;

    /// <summary><c>/plugin unwire &lt;name&gt;</c>.</summary>
    public sealed record Unwire(string Name) : PluginRequest;

    /// <summary>
    /// <c>/plugin enable &lt;name&gt;</c> — flips <c>enabled</c> on a configured entry without
    /// removing it.
    ///
    /// <para>A NAME, NEVER A PATH, unlike <see cref="Load"/>. Loading acts on a FILE and may be given
    /// one that config never declared; enabling acts on a config ENTRY, and entries are keyed by name
    /// — <c>config.sample.json</c> ships two names pointing at one binary, so a path could not say
    /// which of them was meant.</para>
    /// </summary>
    public sealed record Enable(string Name) : PluginRequest;

    /// <summary><c>/plugin disable &lt;name&gt;</c> — the same entry, the other way. A loaded plugin
    /// is unwired: "off" that leaves the tools live is what a user reads as a broken button.</summary>
    public sealed record Disable(string Name) : PluginRequest;

    /// <summary>A subcommand this command does not recognise.</summary>
    public sealed record Unrecognised(string Word) : PluginRequest;
}

/// <summary>One configured plugin's state, for the listing — see <see cref="PluginCommand.Render"/>.</summary>
/// <param name="Name">The name it is configured under in <c>config.json</c>.</param>
/// <param name="State">Which of the three states it is in right now.</param>
public readonly record struct PluginRow(string Name, PluginRowState State);

/// <summary>
/// THREE STATES, NOT TWO — PLUGINS design doc: a listing that only said "loaded / not loaded" would
/// leave a user asking why <c>/plugin load x</c> refused a name the list just offered them. Naming
/// <see cref="Disabled"/> is what makes that refusal legible before they hit it.
/// </summary>
public enum PluginRowState
{
    /// <summary>Running now, its tools offered.</summary>
    Loaded,

    /// <summary>Config permits it; nothing has loaded it yet.</summary>
    Declared,

    /// <summary>Config says no. <c>/plugin load</c> on this name refuses unless <c>--once</c> is
    /// added.</summary>
    Disabled,
}

/// <summary>
/// The decision behind <c>/plugin</c> — parsing what was typed and rendering the listing. Loading
/// and unwiring themselves are <see cref="Sessions.Session.LoadPlugin"/> and
/// <see cref="Sessions.Session.UnwirePluginAsync"/>, which this does not duplicate: they already do
/// the load-gate, the collision check and the four-step unwire, and reimplementing that decision
/// here would be a second place for it to drift from the first.
///
/// <para>DECIDES AND RENDERS ONLY, like <see cref="TrustCommand"/> — everything here is a pure
/// function of the argument and state already in hand, so it is testable with no session, no
/// registry and no disk.</para>
/// </summary>
public static class PluginCommand
{
    private const string OnceFlag = "--once";

    /// <summary>Parses everything after <c>/plugin</c>.</summary>
    public static PluginRequest Parse(string argument)
    {
        var words = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return new PluginRequest.List();

        var verb = words[0];

        if (verb.Equals("load", StringComparison.OrdinalIgnoreCase))
        {
            // INLINE SETTINGS ARE SPLIT OFF BEFORE WORDS ARE, and everything from the first '{' to
            // the end is taken verbatim. A settings object contains spaces and quotes, so tokenising
            // it and rejoining would depend on how the user spaced it; taking the tail whole means
            // `/plugin load p { "server": "x y" }` survives, and the JSON parser downstream is the
            // one thing that decides whether the block is valid.
            //
            // WHY INLINE SETTINGS EXIST AT ALL: /plugin load carries none otherwise, so a plugin
            // needing any could only be TRIED by editing config first — a bad trade for the case
            // this command is for, which is finding out whether a plugin is worth configuring.
            var brace = argument.IndexOf('{');
            var head = brace < 0 ? argument : argument[..brace];
            var settings = brace < 0 ? null : argument[brace..].Trim();

            var headWords = head.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // --once IS STILL ACCEPTED AND IGNORED. `enabled` means auto-load only, so there is no
            // refusal left for it to override — but it was documented, and a command that suddenly
            // rejects a flag someone has in their notes is a worse answer than one that quietly
            // does what they wanted anyway.
            var rest = headWords.Skip(1)
                .Where(w => !w.Equals(OnceFlag, StringComparison.OrdinalIgnoreCase)).ToList();

            return rest.Count == 0
                ? new PluginRequest.Unrecognised("load")
                : new PluginRequest.Load(string.Join(' ', rest), settings);
        }

        if (verb.Equals("unwire", StringComparison.OrdinalIgnoreCase))
        {
            return words.Length < 2
                ? new PluginRequest.Unrecognised("unwire")
                : new PluginRequest.Unwire(words[1]);
        }

        if (verb.Equals("enable", StringComparison.OrdinalIgnoreCase))
            return words.Length < 2
                ? new PluginRequest.Unrecognised("enable")
                : new PluginRequest.Enable(words[1]);

        if (verb.Equals("disable", StringComparison.OrdinalIgnoreCase))
            return words.Length < 2
                ? new PluginRequest.Unrecognised("disable")
                : new PluginRequest.Disable(words[1]);

        return new PluginRequest.Unrecognised(verb);
    }

    /// <summary>
    /// Every configured plugin's row — the plugin design's three states. <paramref name="configured"/> is
    /// <see cref="ResolvedConfig.Plugins"/>: Core knows every name and whether config permits it,
    /// which is what makes the disabled row and the loaded row both answerable without asking the
    /// registry for anything it does not hold.
    /// </summary>
    public static IReadOnlyList<PluginRow> Rows(
        IReadOnlyDictionary<string, PluginConfig> configured, IReadOnlyList<string> loadedNames)
    {
        var loaded = new HashSet<string>(loadedNames, StringComparer.Ordinal);
        var rows = new List<PluginRow>();

        foreach (var (name, config) in configured.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var state = loaded.Contains(name) ? PluginRowState.Loaded
                : config.Enabled ? PluginRowState.Declared
                : PluginRowState.Disabled;
            rows.Add(new PluginRow(name, state));
        }

        // LOADED BUT NOT IN CONFIG — a path-loaded plugin, exactly the case a config edit was never
        // required for. Omitting these would make `/plugin load <path>` invisible on the very
        // listing meant to show what is running.
        foreach (var name in loaded.Where(n => !configured.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal))
            rows.Add(new PluginRow(name, PluginRowState.Loaded));

        return rows;
    }

    /// <summary>The reply for a bare <c>/plugin</c>.</summary>
    public static string Render(IReadOnlyList<PluginRow> rows)
    {
        if (rows.Count == 0)
            return "No plugins configured. Add one under \"plugins\" in config.json, "
                 + "or `/plugin load <path>` to try one that is not configured yet.";

        var lines = new List<string> { "| plugin | state |", "|---|---|" };
        foreach (var row in rows)
        {
            var state = row.State switch
            {
                PluginRowState.Loaded => "loaded",
                PluginRowState.Declared => "declared, not loaded",
                _ => "disabled",
            };
            lines.Add($"| `{Md.Escape(row.Name)}` | {state} |");
        }
        lines.Add("");
        lines.Add("`/plugin load <name>` to load a declared one · "
                 + "`/plugin load <path>` for one not in config · `/plugin unwire <name>` to stop one");
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The refusal for <c>/plugin load</c> on a name config disables — the plugin design's gate: false
    /// means no process, no tools, no prompt, nothing to select from. Names <c>--once</c>, or the
    /// exception is undiscoverable and the feature may as well not exist.
    /// </summary>
    public static string DisabledRefusal(string name) =>
        $"plugin '{name}' is disabled in config.\n"
        + $"`/plugin load {name} --once` loads it for this session only.";

    /// <summary>
    /// Every verb this command accepts, in one string.
    ///
    /// <para>HERE RATHER THAN AT THE CALL SITE, because it was hardcoded inside the session's
    /// unrecognised branch — so adding a verb meant remembering a string in another file, and the
    /// one that told users what exists was the one most easily forgotten.</para>
    /// </summary>
    public const string Usage =
        "Usage: /plugin [load <name|path> [--once] [{ settings }] | unwire <name> "
      + "| enable <name> | disable <name>]";
}

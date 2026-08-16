using CxAgent.Core.Llm;

namespace CxAgent.UI;

/// <summary>What <c>/model</c> decided, and the line to show for it.</summary>
/// <param name="SwitchTo">The instance to switch to, or null when nothing is switching.</param>
/// <param name="Reply">The message for the transcript. Never empty.</param>
public readonly record struct ModelCommandResult(string? SwitchTo, string Reply);

/// <summary>
/// <c>/model</c> — which configured instance this session talks to.
///
/// <para>AN INSTANCE, NOT A VENDOR. A <c>providers</c> entry is a name bound to one endpoint and one
/// model, so <c>fast</c> and <c>careful</c> can be the same server with different models. Switching
/// "the model" and switching "the provider" are therefore the same act here, and the command is
/// named for what a user thinks they are changing.</para>
///
/// <para>THE CONVERSATION IS KEPT. That is the whole point — a user switches because the work got
/// harder or cheaper, not to start again. What must follow the switch is the context WINDOW: it
/// belongs to the model, and a conversation measured against the old one either never compacts (too
/// large) or compacts constantly (too small).</para>
///
/// <para>NOT PERSISTED, matching <c>/mode</c>. A model chosen for one conversation should not
/// silently become the default at next launch; config is Settings' job.</para>
/// </summary>
public static class ModelCommand
{
    /// <summary>
    /// Decides what <c>/model</c> or <c>/model &lt;name&gt;</c> should do.
    /// </summary>
    /// <param name="argument">Everything after the command word.</param>
    /// <param name="registry">The configured instances, live — never a copy of config.</param>
    /// <param name="current">The instance in use, or null when it cannot be named.</param>
    public static ModelCommandResult Decide(
        string argument, ProviderRegistry? registry, string? current)
    {
        if (registry is null || registry.InstanceNames.Count == 0)
            return new(null, $"[{ColorScheme.MutedMarkup}]No providers configured — press F5 to set "
                           + "one up.[/]");

        var wanted = argument.Trim();
        if (wanted.Length == 0) return new(null, Render(registry, current));

        // EXACT NAME FIRST, then a unique prefix. Instance names are short and chosen by the user,
        // so typing three characters of one is the common case; an exact match must still win, in
        // case someone has both `claude` and `claude-fast`.
        var names = registry.InstanceNames;
        var exact = names.FirstOrDefault(n => n.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return Switch(exact, current);

        var matches = names
            .Where(n => n.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            return new(null, $"[yellow]No provider called '{Escape(wanted)}'. "
                           + $"Configured: {Escape(string.Join(", ", names))}.[/]");

        // AMBIGUITY IS REPORTED, NEVER RESOLVED — the same rule /sessions follows. Picking one
        // silently is how a user ends up spending a conversation on a model they did not choose.
        if (matches.Count > 1)
            return new(null, $"[yellow]'{Escape(wanted)}' matches "
                           + $"{Escape(string.Join(", ", matches))}. Be more specific.[/]");

        return Switch(matches[0], current);
    }

    private static ModelCommandResult Switch(string target, string? current)
    {
        // ALREADY THERE IS NOT A SWITCH. Re-wiring would rebuild the agent and reset what a re-wire
        // resets, for no change at all — and the user would have no way to tell that happened.
        if (string.Equals(target, current, StringComparison.OrdinalIgnoreCase))
            return new(null, $"[{ColorScheme.MutedMarkup}]Already using {Escape(target)}.[/]");

        return new(target, "");
    }

    /// <summary>
    /// What the session is using, and what else it could use.
    ///
    /// <para>THE WINDOW IS SHOWN because it is the thing that changes behaviour. Switching to a
    /// smaller model does not fail — the turn loop measures pressure before every send and compacts
    /// — but it means a conversation that fitted now has to be summarised to continue, and that is
    /// worth knowing BEFORE choosing rather than watching it happen.</para>
    /// </summary>
    private static string Render(ProviderRegistry registry, string? current)
    {
        var accent = ColorScheme.AccentMarkup;
        var muted = ColorScheme.MutedMarkup;
        var models = registry.InstanceModels;
        var windows = registry.InstanceWindows;

        var lines = new List<string>
        {
            $"[{accent}]Models[/] [{muted}]· {registry.InstanceNames.Count} configured[/]",
            "",
        };

        foreach (var name in registry.InstanceNames)
        {
            var here = string.Equals(name, current, StringComparison.OrdinalIgnoreCase);
            var window = windows.TryGetValue(name, out var w) && w is { } size
                ? Compact(size)
                : "window unknown";

            // instance:model, one column, so the listing reads the same way as every other place
            // the UI names a model — and is the exact string `/model <name>` takes.
            var label = $"{name}:{models.GetValueOrDefault(name, "?")}";

            lines.Add($"  {(here ? $"[{accent}]▸[/]" : " ")} [{accent}]{Escape(label),-44}[/]"
                    + $" [{muted}]{window,-14}[/]"
                    + (here ? $"[{muted}]· in use[/]" : ""));
        }

        lines.Add("");
        lines.Add($"  [{muted}]/model <name> to switch · the conversation is kept[/]");

        return string.Join('\n', lines);
    }

    /// <summary>
    /// What to say after a switch — including what it costs, when it costs something.
    ///
    /// <para>A WARNING ONLY WHEN THERE IS ONE. The common case is a switch that changes nothing the
    /// user has to think about, and a line that always warns is a line nobody reads. The case worth
    /// naming is a window smaller than what the conversation already occupies: nothing breaks, but
    /// the next turn will compact, and being told afterwards reads as a fault rather than a
    /// consequence of a choice just made.</para>
    /// </summary>
    public static string Switched(
        string name, string model, int? window, int? previousWindow, int? used)
    {
        var accent = ColorScheme.AccentMarkup;
        var muted = ColorScheme.MutedMarkup;

        var lines = new List<string>
        {
            // instance:model, the same shape every other readout uses — and the same string a user
            // would type to switch back.
            $"[{accent}]{Escape(name)}:{Escape(model)}[/]"
            + $" [{muted}]{(window is { } w ? $"· {Compact(w)} window" : "· window unknown")}[/]",
        };

        // THE CONVERSATION SURVIVES, and that is worth stating rather than assuming: every other
        // way of changing the model in this app (Settings, a restart) starts a new session, so a
        // user has no reason to expect this one does not.
        lines.Add($"[{muted}]The conversation is kept. Sub-agents use this too unless their type "
                + "names another provider.[/]");

        if (window is { } newWindow && used is { } occupied && occupied >= newWindow * 0.8)
            lines.Add($"[yellow]This conversation is already {Compact(occupied)} — at or near "
                    + $"{Compact(newWindow)}. The next turn will summarise it to fit.[/]");
        else if (window is null)
            lines.Add($"[{muted}]No window is configured for this instance, so compaction falls "
                    + "back to a fixed threshold. Set contextWindow to track the real one.[/]");
        else if (previousWindow is { } old && window is { } now && now < old)
            lines.Add($"[{muted}]Smaller window than before ({Compact(old)} → {Compact(now)}); "
                    + "long conversations will compact sooner.[/]");

        return string.Join('\n', lines);
    }


    private static string Compact(int tokens) =>
        tokens >= 1_000_000 ? $"{tokens / 1_000_000.0:0.#}M" : $"{tokens / 1000}k";

    private static string Escape(string text) => SharpConsoleUI.Parsing.MarkupParser.Escape(text);
}

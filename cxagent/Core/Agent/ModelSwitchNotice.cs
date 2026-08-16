namespace CxAgent.Core.Agent;

/// <summary>
/// What a session says when it changes model.
///
/// <para>IN CORE, BESIDE THE THING IT DESCRIBES. This lived in the UI, and the composition root read
/// the session's context window and usage — in that order, before the switch — to call it. That made
/// the ordering a caller's problem and the sentence a front end's responsibility: a second one would
/// have reimplemented both, and the first to get the order wrong reports the new window against the
/// old usage without anything catching it.</para>
///
/// <para>MARKUP, USING BARE COLOUR NAMES rather than the UI's palette constants — the same vocabulary
/// <see cref="Permissions.PermissionDecider"/> already writes in. A front end that does not render
/// markup strips it, exactly as it would for a model's own output.</para>
///
/// <para>A PURE FUNCTION, so what it says under each condition is testable without a session, a
/// provider or a window.</para>
/// </summary>
public static class ModelSwitchNotice
{
    /// <summary>The line, or lines, describing a switch that has just happened.</summary>
    /// <param name="previousWindow">The window of the model being LEFT — used to warn when the new
    /// one is smaller, which is the case where a long conversation starts compacting sooner.</param>
    /// <param name="used">How much of that window was in use at the moment of the switch.</param>
    public static string For(string name, string model, int? window, int? previousWindow, int? used)
    {
        var lines = new List<string>
        {
            // instance:model, the same shape every other readout uses — and the same string a user
            // would type to switch back.
            $"[cyan1]{Escape(name)}:{Escape(model)}[/]"
            + $" [grey50]{(window is { } w ? $"· {Compact(w)} window" : "· window unknown")}[/]",
        };

        // THE CONVERSATION SURVIVES, and that is worth stating rather than assuming: every other way
        // of changing the model in this app starts a new session, so a user has no reason to expect
        // this one does not.
        lines.Add("[grey50]The conversation is kept. Sub-agents use this too unless their type "
                + "names another provider.[/]");

        if (window is { } newWindow && used is { } occupied && occupied >= newWindow * 0.8)
            lines.Add($"[yellow]This conversation is already {Compact(occupied)} — at or near "
                    + $"{Compact(newWindow)}. The next turn will summarise it to fit.[/]");
        else if (window is null)
            lines.Add("[grey50]No window is configured for this instance, so compaction falls "
                    + "back to a fixed threshold. Set contextWindow to track the real one.[/]");
        else if (previousWindow is { } old && window is { } now && now < old)
            lines.Add($"[grey50]Smaller window than before ({Compact(old)} → {Compact(now)}); "
                    + "long conversations will compact sooner.[/]");

        return string.Join('\n', lines);
    }

    // The same escape SharpConsoleUI's MarkupParser applies — inlined rather than referenced so
    // Core keeps no dependency on the UI toolkit. A model id containing '[' is unlikely; a crash in
    // the one line reporting a switch is not worth the risk of finding out.
    private static string Escape(string text) => text.Replace("[", "[[");

    private static string Compact(int tokens) =>
        tokens >= 1_000_000 ? $"{tokens / 1_000_000.0:0.#}M" : $"{tokens / 1000}k";
}

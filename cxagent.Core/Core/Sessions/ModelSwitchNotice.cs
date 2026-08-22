using CxAgent.Core.Commands;
using CxAgent.Core.Helpers;
namespace CxAgent.Core.Sessions;

/// <summary>
/// What a session says when it changes model.
///
/// <para>IN CORE, BESIDE THE THING IT DESCRIBES. This lived in the UI, and the composition root read
/// the session's context window and usage — in that order, before the switch — to call it. That made
/// the ordering a caller's problem and the sentence a front end's responsibility: a second one would
/// have reimplemented both, and the first to get the order wrong reports the new window against the
/// old usage without anything catching it.</para>
///
/// <para>MARKDOWN, WITH ONE SEVERITY FOR THE WHOLE NOTICE. This used to colour individual lines with
/// bare tag names — the same vocabulary <see cref="Permissions.PermissionDecider"/> used to write in
/// — but <see cref="Message"/> carries one <see cref="Severity"/> per notice, not per line, so the
/// near-full-window caution now decides the tone of the whole block instead of just one line of it:
/// that is the one fact in here a reader must not miss.</para>
///
/// <para>A PURE FUNCTION, so what it says under each condition is testable without a session, a
/// provider or a window.</para>
/// </summary>
public static class ModelSwitchNotice
{
    /// <summary>The message describing a switch that has just happened.</summary>
    /// <param name="previousWindow">The window of the model being LEFT — used to warn when the new
    /// one is smaller, which is the case where a long conversation starts compacting sooner.</param>
    /// <param name="used">How much of that window was in use at the moment of the switch.</param>
    /// <param name="name">The instance being switched to.</param>
    /// <param name="model">The model that instance serves.</param>
    /// <param name="window">Its context window, or null when unknown.</param>
    public static Message For(string name, string model, int? window, int? previousWindow, int? used)
    {
        var lines = new List<string>
        {
            // instance:model, the same shape every other readout uses — and the same string a user
            // would type to switch back.
            $"**{Md.Escape(name)}:{Md.Escape(model)}** "
            + (window is { } w ? $"· {Compact(w)} window" : "· window unknown"),
        };

        // THE CONVERSATION SURVIVES, and that is worth stating rather than assuming: every other way
        // of changing the model in this app starts a new session, so a user has no reason to expect
        // this one does not.
        lines.Add("The conversation is kept. Sub-agents use this too unless their type "
                + "names another provider.");

        // NEAR-FULL WINDOW IS THE ONLY BRANCH THAT RAISES THE SEVERITY. The other two are routine
        // configuration notes — no window configured, a smaller window than before — worth stating
        // but not worth a warning tone; this one means the very next turn triggers a summarisation
        // the user did not ask for.
        var severity = Severity.Info;
        if (window is { } newWindow && used is { } occupied && occupied >= newWindow * 0.8)
        {
            lines.Add($"This conversation is already {Compact(occupied)} — at or near "
                    + $"{Compact(newWindow)}. The next turn will summarise it to fit.");
            severity = Severity.Warning;
        }
        else if (window is null)
            lines.Add("No window is configured for this instance, so compaction falls "
                    + "back to a fixed threshold. Set contextWindow to track the real one.");
        else if (previousWindow is { } old && window is { } now && now < old)
            lines.Add($"Smaller window than before ({Compact(old)} → {Compact(now)}); "
                    + "long conversations will compact sooner.");

        return new Message(string.Join('\n', lines), severity);
    }

    private static string Compact(int tokens) =>
        tokens >= 1_000_000 ? $"{DisplayNumber.Trimmed(tokens / 1_000_000.0)}M" : $"{tokens / 1000}k";
}

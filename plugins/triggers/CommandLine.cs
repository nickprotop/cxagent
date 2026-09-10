namespace CxAgent.Plugins.Triggers;

/// <summary>
/// Renders triggers the way a person or a model reads them back — one line each, soonest first
/// because that is the order <see cref="TriggerStore.For"/> already hands them in.
/// </summary>
public static class CommandLine
{
    /// <summary>The listing shown by both the <c>trigger_list</c> tool and the slash command.</summary>
    public static string Render(IReadOnlyList<Trigger> triggers)
    {
        if (triggers.Count == 0) return "no triggers pending in this session.";

        return string.Join('\n', triggers.Select(t =>
            $"{t.Id}. {t.When.Describe()} — {Clip(FirstLine(t.Prompt), 60)}"));
    }

    private static string FirstLine(string prompt)
    {
        var newline = prompt.IndexOf('\n');
        return newline < 0 ? prompt : prompt[..newline];
    }

    // AN ELLIPSIS, NOT A HARD CUT, so a listing of long prompts still fits one line per trigger
    // without the reader mistaking a truncated line for the whole prompt.
    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}

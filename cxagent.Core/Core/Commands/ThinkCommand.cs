using CxAgent.Core.Llm;

namespace CxAgent.Core.Commands;

/// <summary>What <c>/think</c> decided: a level to set, the default to return to, or only a reply.</summary>
/// <param name="SetTo">The level to set, or null.</param>
/// <param name="Save">Also make it the default for new sessions, in config.</param>
/// <param name="Clear">Return to the model's own default.</param>
/// <param name="Reply">What to say — the list, or why nothing changed. Empty when the session will
/// say what changed itself.</param>
internal readonly record struct ThinkCommandResult(string? SetTo, bool Save, bool Clear, Message Reply);

/// <summary>
/// <c>/think</c>: how hard this session's model thinks.
///
/// <para>FOR THIS SESSION, with <c>--save</c> the one way to change the default — the same split
/// <c>/model</c> and config make. A level tried for one conversation should not silently become
/// how every later one runs.</para>
/// </summary>
internal static class ThinkCommand
{
    public const string Default = ThinkingLevels.Default;
    private const string SaveFlag = "--save";

    /// <param name="argument">Everything after the command word.</param>
    /// <param name="options">What the model offers.</param>
    /// <param name="current">The level in use, or null for the model's default.</param>
    public static ThinkCommandResult Decide(string argument, ThinkingOptions options, string? current)
    {
        if (!options.Any)
            return new(null, false, false, new Message($"No thinking levels: {options.Why}."));

        var words = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var save = words.Contains(SaveFlag, StringComparer.Ordinal);
        var wanted = words.FirstOrDefault(w => w != SaveFlag);

        if (wanted is null) return new(null, false, false, Render(options, current));
        if (wanted == Default) return new(null, save, true, new Message(""));
        if (options.Levels.Contains(wanted)) return new(wanted, save, false, new Message(""));

        return new(null, false, false, new Message(
            $"'{Md.Escape(wanted)}' is not offered here. Levels: {string.Join(", ", options.Levels)}.",
            Severity.Warning));
    }

    /// <summary>The levels, the one in use marked, and how to change it.</summary>
    private static Message Render(ThinkingOptions options, string? current)
    {
        var lines = new List<string> { "## Thinking", "" };
        foreach (var level in options.Levels.Append(Default))
        {
            var inUse = level == (current ?? Default);
            lines.Add(inUse ? $"- **▸ {level}** (in use)" : $"- {level}");
        }
        lines.Add("");
        lines.Add("`/think <level>` sets it for this session · `--save` makes it the default · F10 cycles");
        return new Message(string.Join("\n", lines));
    }
}

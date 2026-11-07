using System.Text;

namespace CxAgent.Core.Colleagues;

/// <summary>
/// How much experience an incarnation starts with, and which model may carry a colleague at all.
///
/// <para>A QUARTER OF THE WINDOW: enough that a colleague arrives knowing its codebases, little enough that the work it
/// came for still has three quarters of the room. Capped, because past 100k the starting prompt costs more on every
/// turn than recalling the rest when it is needed.</para>
/// </summary>
public static class ExperienceBudget
{
    /// <summary>The smallest window a colleague runs on. Below it the experience would crowd out the work, and what
    /// arrives is a helper without a memory, not a colleague.</summary>
    public const int MinimumWindow = 128_000;

    /// <summary>The most experience loaded at the start, whatever the window.</summary>
    public const int Cap = 100_000;

    /// <summary>The core experience's size limit, in tokens.</summary>
    public const int CoreCap = 8_000;

    /// <summary>One codebase's experience size limit, in tokens.</summary>
    public const int CodebaseCap = 24_000;

    /// <summary>
    /// An estimate of <paramref name="text"/>'s size in tokens. AN ESTIMATE IS ENOUGH: this sizes a budget and a cap,
    /// not a bill, and four characters a token holds for the prose and code an experience is made of.
    /// </summary>
    public static int Tokens(string text) => (text.Length + 3) / 4;

    /// <summary>
    /// Why a model with <paramref name="window"/> cannot carry a colleague, or null when it can. An unknown window is
    /// refused rather than guessed: a guess too large would overflow the model on its first turn.
    /// </summary>
    public static string? WindowRefusal(int? window, string instanceLabel) => window switch
    {
        null => $"{instanceLabel}'s context window is not known. Set contextWindow for it in config.json; a colleague "
                + "needs at least 128k.",
        < MinimumWindow => $"{instanceLabel} has a {window / 1000}k window; a colleague needs at least 128k.",
        _ => null,
    };

    /// <summary>The experience budget for a window: a quarter of it, at most <see cref="Cap"/>.</summary>
    public static int For(int window) => Math.Min(window / 4, Cap);

    /// <summary>
    /// The experience an incarnation starts with: the core and the current codebase always, then its other codebases,
    /// the most recently worked first, while the budget lasts. A codebase that does not fit whole is left out, never
    /// cut — half an account reads as the whole of one, and the rest is a recall away.
    /// </summary>
    public static string Preload(Experience experience, string currentKey, IReadOnlyList<Workplace> byRecency, int budget)
    {
        var sb = new StringBuilder();
        var used = 0;

        void Add(string heading, string text, bool always)
        {
            if (text.Length == 0) return;
            var block = $"## {heading}\n\n{text.Trim()}\n\n";
            var cost = Tokens(block);
            if (!always && used + cost > budget) return;
            sb.Append(block);
            used += cost;
        }

        Add("Core", experience.Core, always: true);
        if (experience.Codebases.TryGetValue(currentKey, out var current)) Add(currentKey, current, always: true);
        foreach (var key in byRecency.OrderByDescending(w => w.Last).Select(w => w.Codebase).Distinct(StringComparer.Ordinal))
        {
            if (key == currentKey || !experience.Codebases.TryGetValue(key, out var text)) continue;
            Add(key, text, always: false);
        }
        return sb.ToString().TrimEnd() + (sb.Length > 0 ? "\n" : "");
    }
}

using System.Globalization;
using System.Linq;
using NCrontab;

namespace CxAgent.Plugins.Triggers;

/// <summary>Which of the three fields a schedule was written with.</summary>
public enum WhenKind
{
    /// <summary>A delay from now.</summary>
    After,

    /// <summary>A moment.</summary>
    At,

    /// <summary>A cron expression — the only repeating kind.</summary>
    Every,
}

/// <summary>
/// When a trigger fires, in one of three grammars.
///
/// <para>THREE NAMED FIELDS, EXACTLY ONE SET, AND THE PLUGIN NEVER GUESSES WHICH. One string that
/// might be a cron line, a date or a duration makes the plugin guess, and a wrong guess schedules
/// something nobody asked for — noticed when it fires, which is exactly when it is least welcome.
/// Three fields cost one line of validation.</para>
/// </summary>
public sealed record When(WhenKind Kind, TimeSpan? After, DateTimeOffset? At, string? Cron)
{
    /// <summary>Only a cron expression repeats; the other two fire once and are done.</summary>
    public bool Repeats => Kind == WhenKind.Every;

    private CrontabSchedule? _schedule;

    /// <summary>
    /// A duration: one integer and one unit suffix.
    ///
    /// <para>NO COMPOUND FORMS. "1h30m" is refused rather than parsed, because accepting it means
    /// owning a parser for every ordering somebody might write — and the refusal is immediate and on
    /// screen, where a wrong parse would surface hours later as a wake at the wrong time.</para>
    /// </summary>
    public static bool TryParseDuration(string text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return false;

        var unit = text[^1];
        if (!int.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            || n <= 0)
            return false;

        value = unit switch
        {
            's' => TimeSpan.FromSeconds(n),
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => TimeSpan.Zero,
        };
        return value > TimeSpan.Zero;
    }

    /// <summary>
    /// Reads a schedule, or says exactly why it will not.
    ///
    /// <para>EVERY REFUSAL NAMES WHAT WAS EXPECTED, because the caller is a model composing a call
    /// and a bare "invalid" leaves it guessing which of three grammars it got wrong.</para>
    /// </summary>
    public static bool TryParse(string? after, string? at, string? every,
        out When? when, out string? refusal)
    {
        when = null;

        var set = new[] { after, at, every }.Count(s => !string.IsNullOrWhiteSpace(s));
        if (set != 1)
        {
            refusal = "set exactly one of 'after', 'at' or 'every' — "
                    + (set == 0 ? "none was set." : $"{set} were set.");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(after))
        {
            if (!TryParseDuration(after, out var delay))
            {
                refusal = $"'{after}' is not a duration. Use one integer and one unit: "
                        + "30s, 90m, 2h, 1d. Compound forms like 1h30m are not accepted.";
                return false;
            }
            when = new When(WhenKind.After, delay, null, null);
            refusal = null;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(at))
        {
            // ASSUME LOCAL, because a person writing "09:00" means their own morning. An explicit
            // offset in the string still wins, so a caller who means UTC can say so.
            if (!DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var moment))
            {
                refusal = $"'{at}' is not a time. Use ISO 8601: 2026-09-10 09:00, "
                        + "or 2026-09-10T09:00:00+02:00.";
                return false;
            }

            if (moment <= DateTimeOffset.Now)
            {
                refusal = $"{at} is in the past. Nothing would ever fire.";
                return false;
            }

            when = new When(WhenKind.At, null, moment, null);
            refusal = null;
            return true;
        }

        // PARSED WHEN IT IS WRITTEN, NEVER WHEN IT FIRES. A scheduler that stored an unparseable
        // line and discovered it at 09:00 has turned a typo into silence.
        var parsed = CrontabSchedule.TryParse(every);
        if (parsed is null)
        {
            refusal = $"'{every}' is not a five-field cron expression. "
                    + "Minute hour day-of-month month day-of-week, e.g. 0 9 * * 1-5.";
            return false;
        }

        when = new When(WhenKind.Every, null, null, every) { _schedule = parsed };
        refusal = null;
        return true;
    }

    /// <summary>
    /// The next moment this fires after <paramref name="now"/>, or null when a one-shot is spent.
    /// </summary>
    public DateTimeOffset? NextAfter(DateTimeOffset now) => Kind switch
    {
        WhenKind.After => now + After!.Value,
        WhenKind.At => At!.Value > now ? At : null,
        _ => Next(now),
    };

    private DateTimeOffset? Next(DateTimeOffset now)
    {
        _schedule ??= CrontabSchedule.TryParse(Cron);
        if (_schedule is null) return null;

        // NCrontab works in DateTime; the local wall clock is what a cron line means.
        var next = _schedule.GetNextOccurrence(now.LocalDateTime);
        return new DateTimeOffset(next, TimeZoneInfo.Local.GetUtcOffset(next));
    }

    /// <summary>How this reads in a listing.</summary>
    public string Describe() => Kind switch
    {
        WhenKind.After => $"in {Format(After!.Value)}",
        WhenKind.At => $"at {At!.Value:yyyy-MM-dd HH:mm}",
        _ => $"every {Cron}",
    };

    private static string Format(TimeSpan span) =>
        span.TotalDays >= 1 && span.TotalDays % 1 == 0 ? $"{(int)span.TotalDays}d"
        : span.TotalHours >= 1 && span.TotalHours % 1 == 0 ? $"{(int)span.TotalHours}h"
        : span.TotalMinutes >= 1 && span.TotalMinutes % 1 == 0 ? $"{(int)span.TotalMinutes}m"
        : $"{(int)span.TotalSeconds}s";
}

using System.Globalization;

namespace CxAgent.Core.Helpers;

/// <summary>
/// Numbers as the UI shows them, formatted the same way on every machine.
///
/// <para>THE SAME DEFECT <c>StatsDashboard.Percent</c> WAS WRITTEN FOR, in every other numeric
/// format the panels use. <c>:N0</c>, <c>:0.0</c> and friends read the CURRENT CULTURE for their
/// group and decimal separators, so a token count rendered "4,441" on an English machine and
/// "4 441" under fr-FR, while "153.1k" became "153,1k". Nothing in the code said which one a user
/// would get; it depended on the machine the app happened to start on.</para>
///
/// <para>INVARIANT RATHER THAN LOCALISED, which is a deliberate choice and not merely the cheaper
/// one. These are figures a user compares against something outside their own screen: the README's
/// screenshots, a number quoted in an issue report, a context window written in a provider's docs,
/// another developer's terminal in a screen share. A separator that changes by machine makes every
/// one of those comparisons quietly wrong, and the reader has no way to tell that it happened.
/// Localised separators would be the right call for a figure the user only ever reads alone; these
/// are not that.</para>
///
/// <para>A HELPER RATHER THAN A FORMAT PROVIDER AT EACH CALL SITE. The percent bug reached a
/// release because eight sites each had to remember; passing <c>CultureInfo.InvariantCulture</c>
/// thirty times over would only raise that number. One name per shape means a missed site is a site
/// that never used the helper, which is visible in a search — not an argument that was left off.</para>
/// </summary>
public static class DisplayNumber
{
    /// <summary>A grouped whole number — <c>4441</c> becomes <c>4,441</c>. The <c>:N0</c> replacement.</summary>
    public static string Grouped(long n) =>
        n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Compact magnitudes — a status bar or dashboard is read for scale, never for digits.
    ///
    /// <para>THE THOUSANDS BRANCH IS WHERE THE CULTURE LEAKED. <c>:0.0</c> takes the current
    /// culture's decimal separator, so the same session read "153.1k" or "153,1k" depending on the
    /// machine — and the comma form reads as a GROUP separator to anyone expecting the other, which
    /// turns a number into one a thousand times its size at a glance.</para>
    /// </summary>
    public static string Compact(long n) =>
        n >= 1_000_000_000 ? Fixed(n / 1_000_000_000.0, 1) + "B"
        : n >= 1_000_000 ? Fixed(n / 1_000_000.0, 1) + "M"
        : n >= 1_000 ? Fixed(n / 1_000.0, 1) + "k"
        : n.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A number at a fixed number of decimal places, with a <c>.</c> for the point everywhere —
    /// durations ("2.0s"), sizes ("1.4 KB") and anything else that shows tenths.
    /// </summary>
    public static string Fixed(double value, int decimals) =>
        value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>
    /// One decimal place, dropped when it is zero — the <c>:0.#</c> shape, which several context-window
    /// labels use so that "1M" does not read as "1.0M" while "1.5M" keeps its half.
    /// </summary>
    public static string Trimmed(double value) =>
        value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <inheritdoc cref="Fixed(double, int)"/>
    public static string Fixed(decimal value, int decimals) =>
        value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>
    /// A duration as <c>00h00m00s</c> — hours, minutes, seconds, each two digits.
    ///
    /// <para>ONE SHAPE FOR EVERY DURATION IN THE APP, not a threshold that switches formats. A run
    /// under a minute and one over it are the same kind of fact, and a reader comparing two rows
    /// should not have to notice which format either one happens to be in before comparing them.</para>
    ///
    /// <para><c>hh\:mm\:ss</c>-STYLE CUSTOM FORMATS ARE SAFE HERE, unlike <see cref="Fixed(double, int)"/>'s
    /// bare decimals: a custom TimeSpan format string with escaped literal separators reads no
    /// culture-sensitive symbol, so <c>InvariantCulture</c> is passed for consistency rather than to
    /// fix a leak the way it does above.</para>
    /// </summary>
    public static string Duration(TimeSpan d) =>
        d.ToString(@"hh\hmm\mss\s", CultureInfo.InvariantCulture);
}

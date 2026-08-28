using System.Globalization;
using System.Text.RegularExpressions;
using Jace;

namespace CxAgent.Plugins.Calculator;

/// <summary>What an expression came to, or why it did not.</summary>
public abstract record EvalResult
{
    private EvalResult() { }

    /// <param name="Text">The value, formatted — see <see cref="Evaluator.Format"/>.</param>
    public sealed record Value(string Text) : EvalResult;

    /// <param name="Reason">Why there is no answer, in words a model can act on.</param>
    public sealed record Refused(string Reason) : EvalResult;
}

/// <summary>
/// One expression in, one number out.
///
/// <para>APART FROM THE PLUGIN so it can be tested as what it is: a pure function of a string. Every
/// decision worth checking — the formatting, the infinity check, the refusals — would otherwise need
/// a plugin host to exercise a format string.</para>
/// </summary>
public static class Evaluator
{
    /// <summary>
    /// Every function Jace 1.0.0 actually has — verified by calling each, not read from its docs.
    /// `pow`, `exp` and `sign` are NOT among them: `exp(1)` and `sign(-3)` are parse errors and
    /// `pow(2,10)` throws "Stack empty." from inside the engine. `^` covers exponentiation.
    /// </summary>
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "sqrt", "abs", "ceiling", "floor", "round", "truncate",
        "max", "min", "sin", "cos", "tan", "asin", "log10", "loge", "logn",
    };

    /// <summary>The same set as prose, for a refusal to list.</summary>
    private const string Functions =
        "sqrt abs ceiling floor round truncate max min sin cos tan asin log10 loge logn";

    // INVARIANT, NOT THE MACHINE'S CULTURE. Jace parses with CurrentCulture by default, so on any
    // comma-decimal locale `0.1+0.2` is a parse error — the plugin would work in CI and fail for a
    // German user, which is exactly the silent wrongness this exists to prevent. The formatter below
    // is invariant for the same reason, in the other direction.
    //
    // ONE ENGINE, REUSED: Jace caches compiled expressions, and a fresh engine per call throws that
    // away.
    private static readonly CalculationEngine Engine = new(CultureInfo.InvariantCulture);

    public static EvalResult Evaluate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return new EvalResult.Refused("nothing to evaluate — give an expression, like (2 + 3) * 4.");

        // REFUSED BEFORE IT RUNS, because the problem is not the value it returns but that the value
        // changes. A model may cache a result or reason about it in a later turn, and a calculator
        // that answers differently for the same input breaks the one promise it makes.
        if (expression.Contains("random", StringComparison.OrdinalIgnoreCase))
            return new EvalResult.Refused(
                "random() is not supported: this tool answers the same way every time, and a "
              + "result that changes between calls cannot be reasoned about.");

        // A NAME JACE DOES NOT KNOW, CAUGHT BEFORE IT RUNS. Its own message for `log(100)` is
        // "The syntax of the provided formula is not valid." — which names neither the function nor
        // the problem, so a model reaching for `log` would be told nothing it could act on. Any
        // identifier followed by '(' is a function call; one that is not in the known set is the
        // likely culprit and is worth naming.
        var unknown = Regex.Matches(expression, @"([A-Za-z_][A-Za-z0-9_]*)\s*\(")
            .Select(m => m.Groups[1].Value)
            .FirstOrDefault(name => !Known.Contains(name));

        if (unknown is not null)
            return new EvalResult.Refused(
                $"'{unknown}' is not a function this calculator has. Available: {Functions}.");

        double value;
        try
        {
            value = Engine.Calculate(expression);
        }
        catch (Exception ex)
        {
            // JACE'S OWN REASON. It is terse — "The syntax of the provided formula is not valid." —
            // but it is what there is, and the common cause is handled above where something useful
            // can be said.
            return new EvalResult.Refused(ex.Message);
        }

        // INFINITY AND NaN ARE NOT ANSWERS. Jace returns them as VALUES — 1/0 is ∞, 0/0 is NaN — and
        // a value is something a model carries into its next step. This cannot be caught on the way
        // in: nothing in the text distinguishes x/0 from x/y where y happens to be zero.
        if (double.IsNaN(value) || double.IsInfinity(value))
            return new EvalResult.Refused(
                "the result is not a number — check for division by zero or an overflow.");

        return new EvalResult.Value(Format(value));
    }

    /// <summary>
    /// Fifteen significant figures.
    ///
    /// <para>NOT ROUNDING, AND NOT THE RAW DOUBLE. A double carries about 15-17 significant digits of
    /// real information and the digits past that are representation artefact: 9.95*100 is
    /// 994.9999999999999 raw, and 995 is what the arithmetic determined. G15 drops the artefact.</para>
    ///
    /// <para>AND KEEPS REAL PRECISION LOSS. 100.5-100.4 stays 0.0999999999999943, because that is
    /// catastrophic cancellation rather than noise — a formatter that hid it would be lying about
    /// what was computed.</para>
    ///
    /// <para>INVARIANT CULTURE, so a decimal point is a point wherever this runs. A model reading
    /// "5,002" for five-and-a-bit would be wrong by three orders of magnitude.</para>
    /// </summary>
    private static string Format(double value) =>
        value.ToString("G15", CultureInfo.InvariantCulture);
}

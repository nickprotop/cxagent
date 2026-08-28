using CxAgent.Plugins.Calculator;
using Xunit;

namespace CxAgent.Plugins.Calculator.Tests;

/// <summary>
/// WHY THIS PLUGIN EXISTS, in test form. A model doing arithmetic across several turns is
/// confidently wrong in the middle of long calculations; these pin the cases where an evaluator
/// could be wrong in the same way — silently.
/// </summary>
public class EvaluatorTests
{
    private static string Value(string expression) =>
        Assert.IsType<EvalResult.Value>(Evaluator.Evaluate(expression)).Text;

    private static string Refusal(string expression) =>
        Assert.IsType<EvalResult.Refused>(Evaluator.Evaluate(expression)).Reason;

    /// <summary>The calculation this plugin exists for: one expression, one exact answer.</summary>
    [Fact]
    public void ItEvaluatesAWholeExpression()
        => Assert.Equal("5.00229166666667", Value("(1847 * 0.0325) / 12"));

    /// <summary>
    /// ^ IS EXPONENTIATION AND RIGHT-ASSOCIATIVE. This is the case that chose the library: NCalc
    /// answers 8 for 2^10 because ^ is XOR there — a silently wrong number, which is the exact
    /// failure this plugin exists to prevent.
    /// </summary>
    [Theory]
    [InlineData("2^10", "1024")]
    [InlineData("2^3^2", "512")]
    [InlineData("(1+1)^3", "8")]
    [InlineData("2^-1", "0.5")]
    public void ExponentiationIsWhatAMathematicianMeans(string expression, string expected)
        => Assert.Equal(expected, Value(expression));

    /// <summary>
    /// G15 DROPS THE ARTEFACT, NOT THE ANSWER. A double carries ~15-17 significant digits of real
    /// information; the rest is representation. 9.95*100 is 994.9999999999999 raw, and 995 is what
    /// the arithmetic actually determined.
    /// </summary>
    [Theory]
    [InlineData("9.95*100", "995")]
    [InlineData("0.1+0.2", "0.3")]
    [InlineData("2/3*3", "2")]
    public void RepresentationNoiseIsNotShown(string expression, string expected)
        => Assert.Equal(expected, Value(expression));

    /// <summary>
    /// AND REAL PRECISION LOSS STILL IS. 100.5-100.4 is catastrophic cancellation, not noise — a
    /// formatter that hid this would be lying about what was computed.
    /// </summary>
    [Fact]
    public void GenuinePrecisionLossIsStillVisible()
        => Assert.Equal("0.0999999999999943", Value("100.5-100.4"));

    /// <summary>
    /// INFINITY IS NOT AN ANSWER. Jace returns it as a VALUE for 1/0, not an exception — and a value
    /// is something a model carries into its next step. It cannot be caught on the way in, because
    /// nothing in the text distinguishes x/0 from x/y where y happens to be zero.
    /// </summary>
    [Theory]
    [InlineData("1/0")]
    [InlineData("-1/0")]
    [InlineData("0/0")]
    [InlineData("9999999^9999999")]
    public void AResultThatIsNotANumberIsRefused(string expression)
        => Assert.Contains("not a number", Refusal(expression));

    /// <summary>
    /// RANDOM IS REFUSED. Jace provides it; a calculator that answers differently for the same input
    /// is a trap for a model that may cache or reason about a result which will not reproduce.
    /// </summary>
    [Theory]
    [InlineData("random()")]
    [InlineData("1 + random()")]
    [InlineData("RANDOM()")]
    public void RandomIsRefusedBecauseItIsNotReproducible(string expression)
        => Assert.Contains("random", Refusal(expression), StringComparison.OrdinalIgnoreCase);

    /// <summary>Malformed input is a refusal carrying the reason, not a crash.</summary>
    [Theory]
    [InlineData("2 +")]
    [InlineData("((1+2)")]
    public void MalformedInputIsRefused(string expression)
        => Assert.NotEmpty(Refusal(expression));

    /// <summary>
    /// AN UNKNOWN FUNCTION NAMES WHAT EXISTS. `log` is the one a model reaches for and Jace does not
    /// have it — log10, loge and logn instead, which is less ambiguous but only if it is said.
    /// </summary>
    [Fact]
    public void AnUnknownFunctionListsTheOnesThatExist()
    {
        var reason = Refusal("log(100)");

        Assert.Contains("log10", reason);
        Assert.Contains("logn", reason);
    }

    /// <summary>Nothing to evaluate is its own message, not a parse error a user must decode.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInputSaysThereIsNothingToEvaluate(string expression)
        => Assert.Contains("nothing to evaluate", Refusal(expression));

    /// <summary>
    /// A METHOD CALL DOES NOT PARSE, which is why this needs no sandbox. Jace evaluates arithmetic
    /// and nothing else — an evaluator that compiled C# would have needed one.
    /// </summary>
    [Fact]
    public void AMethodCallIsNotEvaluated()
        => Assert.NotEmpty(Refusal("System.IO.File.Delete(\"x\")"));

    /// <summary>The function set the manifest advertises, so the two cannot drift.</summary>
    [Theory]
    [InlineData("sqrt(144)", "12")]
    [InlineData("abs(-5)", "5")]
    [InlineData("max(2,3)", "3")]
    [InlineData("round(3.14159)", "3")]
    [InlineData("log10(100)", "2")]
    public void TheAdvertisedFunctionsWork(string expression, string expected)
        => Assert.Equal(expected, Value(expression));
}

using CxAgent.Core.Commands;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What the model is told about a terminal the USER drove. Not a tool result: a tool result is
/// something the model's own call produced, and this was driven by a person who may have typed
/// things of their own.
/// </summary>
public class ShellTranscriptTests
{
    private static string Render(string cmd, IReadOnlyList<string> lines, int? code) =>
        ShellTranscript.Render(new ShellOutcome(cmd, lines, code));

    [Fact]
    public void ItNamesTheCommand_TheExitCode_AndWhatWasSeen()
    {
        var text = Render("apt install foo", ["Reading package lists", "done"], 0);

        Assert.Contains("apt install foo", text);
        Assert.Contains("Exited 0", text);
        Assert.Contains("Reading package lists", text);
    }

    [Fact]
    public void AnUnknownExitCode_IsNotReportedAsZero()
    {
        // Zero is a real answer and means success. A child killed mid-run has no status, and
        // saying "Exited 0" would report a killed install as a successful one.
        var text = Render("sleep 100", ["partial"], null);

        Assert.DoesNotContain("Exited 0", text);
        Assert.Contains("unknown", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ALongTranscript_KeepsBothEnds_AndAdmitsTheGap()
    {
        // THE ENDS CARRY THE SIGNAL: the head has the command and any early failure, the tail has
        // the outcome. The middle carries progress.
        var lines = new List<string> { "FIRST-LINE" };
        for (int i = 0; i < 5000; i++) lines.Add($"middle noise line {i} ................");
        lines.Add("LAST-LINE");

        var text = Render("build", lines, 0);

        Assert.Contains("FIRST-LINE", text);
        Assert.Contains("LAST-LINE", text);
        Assert.Contains("lines not shown", text);

        // THE BODY IS WHAT IS CAPPED. The header is two bounded lines; letting the user's command
        // length eat into the output budget would be the wrong knob. A slack tolerance here would
        // hide a real overshoot, so this measures the body itself.
        var body = text[(text.IndexOf("saw:", StringComparison.Ordinal) + 6)..];
        Assert.True(body.Length <= 8_000, $"budget blown: {body.Length}");
    }

    [Fact]
    public void AShortTranscript_IsNotTruncated()
    {
        Assert.DoesNotContain("not shown", Render("echo hi", ["hi"], 0));
    }

    [Fact]
    public void NoOutput_IsSaidPlainly()
    {
        // An empty block under "What they saw:" reads as a rendering bug rather than as silence.
        var text = Render("true", [], 0);

        Assert.Contains("Exited 0", text);
        Assert.DoesNotContain("lines not shown", text);
    }

    [Fact]
    public void ElisionIsByWholeLines()
    {
        // BY LINES, NOT CHARACTERS. The source is already a list of lines, and a cut mid-character
        // produces a fragment that reads as real output.
        var lines = new List<string>();
        for (int i = 0; i < 3000; i++) lines.Add($"line-{i:D5}-{new string('x', 60)}");

        var text = Render("build", lines, 0);

        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            // Every surviving output line is whole: it either is the marker/header, or it still
            // carries the full padding it was written with.
            if (t.StartsWith("line-"))
                Assert.EndsWith(new string('x', 60), t);
        }
    }
}

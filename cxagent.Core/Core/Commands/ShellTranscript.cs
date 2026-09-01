using System.Text;

namespace CxAgent.Core.Commands;

/// <summary>What a terminal window did, once its child has gone.</summary>
/// <param name="Command">What the user asked for — empty for a bare interactive shell.</param>
/// <param name="Lines">Everything the terminal showed, oldest first.</param>
/// <param name="ExitCode">
/// The child's status, or null when it could not be determined — a command killed on close has no
/// status, and zero is a real answer meaning success rather than a stand-in for "unknown".
/// </param>
public readonly record struct ShellOutcome(string Command, IReadOnlyList<string> Lines, int? ExitCode);

/// <summary>
/// Renders a terminal's transcript for the model.
///
/// <para>NOT A TOOL RESULT, and that is why it is prose with a marker rather than JSON. A tool
/// result is something the model's own call produced; this was driven by a person who may have
/// typed things of their own, and it arrives ahead of their next message.</para>
///
/// <para>COOKED, NOT RAW. The terminal's VT100 machine has already interpreted the byte stream, so
/// there are no escape sequences to strip — the thing that would have produced them has run.</para>
/// </summary>
public static class ShellTranscript
{
    /// <summary>
    /// The budget for the transcript body.
    ///
    /// <para>SMALLER THAN A TOOL RESULT'S 65,536, which is sized for reading a source file whole. A
    /// transcript is not a unit: an <c>apt install</c> at that size would spend a fifth of a context
    /// on something the model needs one line of.</para>
    ///
    /// <para>THE BODY ALONE, not the whole message. The two header lines are bounded — a command
    /// line and an exit status — and counting them would make how much OUTPUT survives depend on
    /// how long the user's command was, which is the one input that has nothing to do with it.</para>
    /// </summary>
    private const int Cap = 8_000;

    public static string Render(ShellOutcome outcome)
    {
        var what = string.IsNullOrWhiteSpace(outcome.Command) ? "an interactive shell" : outcome.Command;

        var sb = new StringBuilder();
        sb.Append("[cxagent] the user ran a command in a terminal: ").AppendLine(what);

        // NEVER "Exited 0" FOR AN UNKNOWN STATUS. A command killed when the window closed has no
        // status, and reporting it as zero would tell the model an interrupted install succeeded.
        sb.Append(outcome.ExitCode is { } code ? $"Exited {code}." : "Exited (unknown).");

        if (outcome.Lines.Count == 0)
        {
            // SAID, NOT SHOWN AS EMPTINESS. A "What they saw:" heading with nothing under it reads
            // as a rendering fault rather than as a command that printed nothing.
            sb.AppendLine(" It printed nothing.");
            return sb.ToString();
        }

        sb.AppendLine(" What they saw:").AppendLine();
        sb.Append(Body(outcome.Lines));
        return sb.ToString();
    }

    /// <summary>
    /// The lines, indented, with the middle dropped when there are too many.
    ///
    /// <para>HEAD AND TAIL, following <c>ToolBindings.Truncate</c>'s shape for its reason: the ends
    /// of a long output carry the signal — the head has the command and any early failure, the tail
    /// has the outcome — and the middle carries progress.</para>
    ///
    /// <para>BY WHOLE LINES rather than by characters, which is where this parts company with that
    /// helper. The source is already a list of lines, a cut mid-character produces a fragment that
    /// reads as real output, and "847 lines not shown" tells a model the SCALE of what it is missing
    /// in a way a byte count does not.</para>
    ///
    /// <para>INDENTED BY TWO SPACES so the block is visibly quoted output rather than instructions
    /// addressed to the model.</para>
    /// </summary>
    private static string Body(IReadOnlyList<string> lines)
    {
        var whole = new StringBuilder();
        foreach (var line in lines) whole.Append("  ").AppendLine(line);
        if (whole.Length <= Cap) return whole.ToString();

        // Grown from both ends until the budget is spent, so the marker's own length is accounted
        // for rather than estimated — the count inside it cannot push the result back over.
        int head = 0, tail = 0, used = 0;
        while (head + tail < lines.Count)
        {
            var marker = $"  [... {lines.Count - head - tail:N0} lines not shown ...]\n";
            var next = head <= tail ? lines[head] : lines[lines.Count - 1 - tail];
            if (used + next.Length + 3 + marker.Length > Cap) break;

            used += next.Length + 3;
            if (head <= tail) head++; else tail++;
        }

        var sb = new StringBuilder();
        for (int i = 0; i < head; i++) sb.Append("  ").AppendLine(lines[i]);
        sb.Append("  [... ").Append($"{lines.Count - head - tail:N0}").AppendLine(" lines not shown ...]");
        for (int i = lines.Count - tail; i < lines.Count; i++) sb.Append("  ").AppendLine(lines[i]);
        return sb.ToString();
    }
}

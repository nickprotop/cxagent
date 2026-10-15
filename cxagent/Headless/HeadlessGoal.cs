using System.Text;

namespace CxAgent.Headless;

public sealed record GoalInput(string? Goal, bool StdinRedirected, TextReader Stdin);

public sealed record ComposedGoal(string? Text, string? Error, bool Truncated);

/// <summary>The goal as the model receives it: -p's text, stdin, or both.</summary>
public static class HeadlessGoal
{
    /// <summary>The same bound a tool's output gets: piped text is text on its way to the model.</summary>
    public const int PipeCap = Core.Jobs.ToolBindings.MaxToolResultChars;

    public static ComposedGoal Compose(GoalInput input)
    {
        string? piped = null;
        var truncated = false;

        if (input.StdinRedirected)
        {
            // AT MOST CAP + 1 CHARACTERS, so `< /dev/zero` ends instead of filling memory. The extra
            // one is how a cut is told apart from an input exactly the cap long.
            var buffer = new char[PipeCap + 1];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = input.Stdin.Read(buffer, read, buffer.Length - read)) > 0)
                read += n;

            piped = new string(buffer, 0, Math.Min(read, PipeCap));
            truncated = read > PipeCap;

            // NUL NEVER APPEARS IN TEXT. Sending a binary to the model costs a whole context of
            // noise and answers nothing.
            if (piped.Contains('\0'))
                return new(null, "piped input is not text.", false);
        }

        var goal = string.IsNullOrWhiteSpace(input.Goal) ? null : input.Goal;
        var pipedText = string.IsNullOrWhiteSpace(piped) ? null : piped.TrimEnd();

        if (goal is null && pipedText is null)
            return new(null, "nothing to run: give -p a goal, or pipe one in.", false);

        var note = truncated ? $"\n\n[input cut at {PipeCap:N0} characters]" : "";

        if (goal is null) return new(pipedText + note, null, truncated);
        if (pipedText is null) return new(goal, null, false);

        var text = new StringBuilder()
            .Append(goal).Append("\n\nInput piped to this run:\n```\n")
            .Append(pipedText).Append("\n```").Append(note);
        return new(text.ToString(), null, truncated);
    }
}

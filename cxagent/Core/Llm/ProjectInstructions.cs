namespace CxAgent.Core.Llm;

/// <summary>One project's instruction file: where it was found, and what it said.</summary>
public sealed record ProjectInstructionFile(string Path, string Text);

/// <summary>
/// Per-project instructions — <c>AGENTS.md</c> or <c>CLAUDE.md</c>, found by walking up from the
/// working directory.
///
/// <para>WHY A SEPARATE FILE AT ALL. Some instructions are true of a REPO, not of agents in general,
/// and they cannot live in the universal system prompt. The case that forced this: opencode's prompt
/// says "DO NOT ADD ***ANY*** COMMENTS unless asked", while this codebase wants the opposite — heavy
/// explanatory comments carrying the reasoning behind a decision. Both are right for their own tree.
/// Writing either into <see cref="SystemPrompt"/> would be wrong for whoever points the agent
/// somewhere else, and a prompt that is wrong about the project is worse than one that is silent.</para>
///
/// <para>Shape from opencode's <c>session/instruction.ts</c>: walk up, FIRST MATCH WINS. Their
/// comment on that line reads "so we don't stack AGENTS.md/CLAUDE.md from every ancestor" — stacking
/// is how a context quietly fills with advice from three directories up that has nothing to do with
/// the work in hand.</para>
/// </summary>
public static class ProjectInstructions
{
    /// <summary>
    /// Names tried in order, nearest directory first.
    ///
    /// <para>AGENTS.md leads because it is the vendor-neutral name. CLAUDE.md is read as well so a
    /// repo already carrying one — this one does not, but many do — gets its instructions honoured
    /// without having to duplicate the file under a second name.</para>
    /// </summary>
    private static readonly string[] FileNames = ["AGENTS.md", "CLAUDE.md"];

    /// <summary>
    /// How much of the file is used.
    ///
    /// <para>This rides in the system message, which is the prompt-cache prefix re-sent on EVERY
    /// turn — so an enormous AGENTS.md is not a one-off cost but a permanent tax on the window. The
    /// cut is marked in the text rather than silent: instructions that stop mid-sentence with no
    /// explanation read as a bug in the agent.</para>
    /// </summary>
    private const int MaxChars = 8_000;

    /// <summary>
    /// The nearest instruction file at or above <paramref name="startDirectory"/>, or null when
    /// there is none.
    /// </summary>
    /// <remarks>
    /// Never throws. An unreadable directory, a permission error or a file that vanishes mid-walk
    /// means the agent runs without project instructions — exactly as it did before this existed.
    /// </remarks>
    public static ProjectInstructionFile? Find(string startDirectory)
    {
        try
        {
            var dir = new DirectoryInfo(startDirectory);
            while (dir is not null)
            {
                foreach (var name in FileNames)
                {
                    var path = Path.Combine(dir.FullName, name);
                    if (!File.Exists(path)) continue;

                    var text = File.ReadAllText(path).Trim();

                    // AN EMPTY FILE IS NOT INSTRUCTIONS. A placeholder would otherwise produce a
                    // header announcing project instructions followed by nothing.
                    if (text.Length == 0) continue;

                    if (text.Length > MaxChars)
                        text = text[..MaxChars]
                             + $"\n\n[truncated — {name} is longer than {MaxChars:N0} characters]";

                    return new ProjectInstructionFile(path, text);
                }

                dir = dir.Parent;
            }
        }
        catch (Exception)
        {
            // Best effort, like every other read in this app that is not the work itself.
        }

        return null;
    }

    /// <summary>
    /// Renders the file as the block that follows the system prompt, or an empty string when there
    /// is nothing to add.
    ///
    /// <para>NAMED AND FENCED. The model is told where the text came from and that it takes
    /// precedence — an unattributed paragraph appended to a system prompt reads as though the app
    /// itself said it, and there would be no way for the model to weigh a project rule against a
    /// general one.</para>
    /// </summary>
    public static string Render(ProjectInstructionFile? file) =>
        file is null
            ? ""
            : $"\n# Project instructions\n\n"
            + $"These come from {file.Path} and describe THIS project. Where they disagree with "
            + $"anything above, follow these.\n\n"
            + file.Text + "\n";
}

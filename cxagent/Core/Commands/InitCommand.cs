namespace CxAgent.Core.Commands;

/// <summary>
/// <c>/init</c> — write the project instruction file the agent reads on every turn.
///
/// <para>THE BOOTSTRAP STEP: how a repository teaches cxagent its conventions. Everything else the
/// app knows about a project it works out by looking; this is the one place a human writes down what
/// looking cannot tell you — the test command that actually works, the convention that looks
/// arbitrary until explained, the thing that was tried and abandoned.</para>
///
/// <para>A TURN, NOT A COMMAND. Unlike <c>/mode</c> or <c>/skills</c> this costs tokens and takes
/// time: the agent explores the project and writes what it found. It shows as an ordinary turn, with
/// its tool calls visible and its file write gated like any other.</para>
/// </summary>
public static class InitCommand
{
    /// <summary>
    /// The file <c>/init</c> should write, and why that one.
    /// </summary>
    /// <param name="Path">Absolute path of the file to write.</param>
    /// <param name="Exists">Is it already there? Decides merge-versus-create.</param>
    /// <param name="Note">A line for the transcript when the choice is worth explaining, else null.</param>
    public readonly record struct Target(string Path, bool Exists, string? Note);

    /// <summary>
    /// The resolver's OWN list, not a copy of it.
    ///
    /// <para><c>/init</c> must write the file that governs, so the two cannot be allowed to drift: a
    /// private copy here would agree today, and the day a name is added to the resolver this would
    /// keep writing a file the agent never reads — silently, with every test still green.</para>
    /// </summary>
    private static string[] Candidates => Core.Llm.ProjectInstructions.ProjectFileNames;

    /// <summary>
    /// Decides which file to write.
    ///
    /// <para>IT EDITS THE FILE THAT ALREADY GOVERNS — whichever one the resolver would pick, so what
    /// <c>/init</c> writes is what the agent then reads. Writing a <c>CXAGENT.md</c> next to an
    /// existing <c>AGENTS.md</c> would produce two near-identical documents, one of which rots, and
    /// the repository has already committed to the vendor-neutral name.</para>
    ///
    /// <para><c>CLAUDE.md</c> IS READ, NEVER WRITTEN. It is third in the resolver so a repository
    /// carrying only that one still works; seeding from it would mean copying another product's
    /// instructions into a file we maintain. Honouring it when it is all there is is a courtesy.
    /// Treating it as ours to edit is not.</para>
    /// </summary>
    public static Target Resolve(string workingDir, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;

        foreach (var name in Candidates)
        {
            var path = Path.Combine(workingDir, name);
            if (!exists(path)) continue;

            if (name == "CLAUDE.md")
                // NOT the file to write. A fresh CXAGENT.md is created beside it, and the note says
                // why — otherwise "I already have instructions, why is there a new file" has no
                // answer anywhere on screen.
                return new(Path.Combine(workingDir, "CXAGENT.md"), false,
                    "CLAUDE.md is read but never written — writing CXAGENT.md instead.");

            // A SECOND FILE EXISTING IS NOT WORTH A LINE. CXAGENT.md winning over AGENTS.md is the
            // resolver's documented behaviour, and saying so on every /init is noise.
            return new(path, true, null);
        }

        return new(Path.Combine(workingDir, "CXAGENT.md"), false, null);
    }

    /// <summary>
    /// The prompt <c>/init</c> sends.
    ///
    /// <para>WHAT IS WORTH WRITING IS WHAT IS NOT DISCOVERABLE. "This is a .NET project" is visible
    /// from a directory listing and helps nobody; the file earns its place with the command that
    /// actually works and the convention that looks arbitrary until explained. Said explicitly
    /// because a model asked to "document the project" will otherwise produce a summary of the
    /// directory tree, which is the one thing the reader can already see.</para>
    ///
    /// <para>MERGED, NEVER APPENDED, when the file exists. It is the user's work and their words:
    /// preserve what is there, add only what is missing, and never restate in different words
    /// something already said — a document contradicting itself in two registers is worse than one
    /// that is merely incomplete.</para>
    /// </summary>
    public static string Prompt(Target target)
    {
        var name = Path.GetFileName(target.Path);

        var task = target.Exists
            ? $"""
               Update `{name}` in the working directory. It already exists and it is the user's own
               work: read it first, preserve what is there, and add ONLY what is genuinely missing.
               Never restate something the file already says in different words, and never reorder or
               rewrite a section that is already correct. If you cannot merge safely, stop and say
               why rather than overwriting.
               """
            : $"""
               Create `{name}` in the working directory.
               """;

        return $"""
            {task}

            This file is read by the agent at the start of every session in this project, so it is
            for whoever works here next — a human or an agent — and it is worth tokens on every turn.

            Explore the project first: build files, README, test layout, CI configuration, and how
            the code is actually organised. Then write what you found.

            WRITE WHAT IS NOT DISCOVERABLE. Anything visible from a directory listing or the top of
            the README is not worth a line — "this is a .NET project", "the source is in src/" — and
            padding the file with it makes the parts that matter harder to find. What earns its place:

            - The build, test and lint commands that actually work here, including how to run a
              single test.
            - Architecture that takes reading several files to see: what talks to what, where the
              boundaries are, which layer owns what.
            - Conventions that look arbitrary until explained, and the reason behind them.
            - Anything that was tried and abandoned, and why — that is the knowledge nobody
              rediscovers cheaply.

            Keep it short enough that someone reads all of it. Do not invent sections for their own
            sake: no "Support", no "Contributing", no "Tips", unless the project genuinely has
            something to say there. If a fact is not in the repository, do not write it down.
            """;
    }
}

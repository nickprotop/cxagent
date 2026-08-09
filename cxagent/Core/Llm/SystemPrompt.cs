using System.Text;

namespace CxAgent.Core.Llm;

/// <summary>
/// The facts about where the agent is running. Everything here is something the model would
/// otherwise guess at, and a wrong guess is expensive: a platform guess puts <c>find -printf</c> on a
/// mac, a repo guess spends a turn running <c>git status</c> to find out.
/// </summary>
public readonly record struct SystemPromptContext(
    string WorkingDirectory,
    bool IsGitRepo,
    string Platform,
    DateOnly Today,
    string ModelId);

/// <summary>
/// What the agent is told before it starts.
///
/// <para>ADAPTED FROM OPENCODE (<c>session/prompt/default.txt</c> plus the runtime
/// <c>&lt;env&gt;</c> block in <c>session/system.ts</c>), which in turn inherits its shape from
/// Claude Code. Ours was three sentences — working directory, and "use the tools". The live drive
/// showed the cost: the model ran a test command whose filter matched nothing, read
/// <c>exit_code: 0</c> as proof of success, and reported "all tests build and pass cleanly" over a
/// file that did not compile. Nothing had ever told it to check what its verification verified.</para>
///
/// <para>NOT COPIED WHOLESALE. opencode's prompt spends most of its length on things this app does
/// not have — a WebFetch tool, subagent delegation, skills, MCP servers, a /help command — and
/// carrying that text would be describing capabilities the model cannot use. What is taken is the
/// part that applies to any agent that edits files and runs commands: state the environment, follow
/// the conventions already in the tree, verify with the project's own tooling, and be brief.</para>
///
/// <para>ONE PROMPT, WITH A SEAM FOR MORE. opencode ships nine model-specific variants — kimi is told
/// to act rather than describe, GPT is told how to parallelise tool calls and not to chain bash with
/// <c>;</c> — because it faces nine model families with measured quirks. This app targets one
/// endpoint. <see cref="Build"/> takes the model id so a second variant is a new branch rather than a
/// refactor, but shipping variants for models nobody has driven would be guessing at quirks instead
/// of observing them.</para>
/// </summary>
public static class SystemPrompt
{
    public static string Build(SystemPromptContext ctx)
    {
        // ONE PROMPT TODAY. The model id is taken and deliberately unused: see the class doc. When a
        // second endpoint is driven and shows a real quirk, that is a branch here.
        _ = ctx.ModelId;

        var sb = new StringBuilder();

        sb.AppendLine("You are cxagent, a coding agent working directly in the user's checkout.");
        sb.AppendLine();

        // THE ENVIRONMENT, as facts rather than as instructions. opencode's <env> block.
        sb.AppendLine("<env>");
        sb.AppendLine($"  Working directory: {ctx.WorkingDirectory}");
        sb.AppendLine($"  Is a git repo: {(ctx.IsGitRepo ? "yes" : "no")}");
        sb.AppendLine($"  Platform: {ctx.Platform}");
        sb.AppendLine($"  Today: {ctx.Today:yyyy-MM-dd}");
        sb.AppendLine("</env>");
        sb.AppendLine();
        sb.AppendLine("Relative paths resolve from the working directory. Do not guess absolute "
                    + "paths — prefer paths relative to it.");
        sb.AppendLine();

        sb.AppendLine("# Doing the work");
        sb.AppendLine();
        sb.AppendLine("You have tools. USE THEM: read a file before editing it, and make changes with "
                    + "write_file or replace_in_file rather than describing them. Text in a message "
                    + "changes nothing.");
        sb.AppendLine();
        sb.AppendLine("Search before you assume. Read the files around the one you are changing — "
                    + "their imports say which libraries this project actually uses.");
        sb.AppendLine();
        sb.AppendLine("Independent tool calls can go in one turn. Reading three files is one round "
                    + "trip, not three.");
        sb.AppendLine();
        // A FETCH TOOL PLUS AN INVENTED URL is how an agent confidently reads a page that does not
        // exist. opencode's first substantive line is this same guardrail.
        sb.AppendLine("Never invent a URL for http_request. Use one the user gave you, or one you "
                    + "read from a file in this project.");
        sb.AppendLine();
        sb.AppendLine("Do not commit unless the user asks. Running the tests is expected; committing "
                    + "is theirs to decide.");
        sb.AppendLine();

        sb.AppendLine("# Following conventions");
        sb.AppendLine();
        sb.AppendLine("Match the code that is already there: its style, its naming, its idioms. Never "
                    + "assume a library is available because it is well known — check that this "
                    + "codebase already uses it. When you add a file, look at a neighbouring one "
                    + "first and follow its shape.");
        sb.AppendLine();

        // THE SECTION THE LIVE DRIVE PAID FOR. Everything here is a specific way a verification can
        // succeed while proving nothing, each one observed rather than imagined.
        sb.AppendLine("# Verifying");
        sb.AppendLine();
        sb.AppendLine("Do not assume the build or test command. Find the project's own — a README, a "
                    + "script, a makefile, the test project next to the code.");
        sb.AppendLine();
        sb.AppendLine("A command that exits 0 has not necessarily verified anything. Before you call "
                    + "work done, READ THE OUTPUT and check it says what you think it says:");
        sb.AppendLine("- A test run reporting zero tests is not a pass. Confirm the count.");
        sb.AppendLine("- A filter that matches nothing exits 0. Confirm your filter matched.");
        sb.AppendLine("- A build that compiled nothing exits 0. Confirm it built what you changed.");
        sb.AppendLine();
        sb.AppendLine("If you cannot verify a change, say so plainly. Reporting success you have not "
                    + "confirmed is worse than reporting that you could not confirm it.");
        sb.AppendLine();

        // THE MODEL CANNOT RUN THESE — the app intercepts them before a turn starts. It is told
        // about them so it can point the user at one, and so a "/help" typed at it is recognised as
        // a command the app handles rather than answered as prose.
        sb.AppendLine("# The user's commands");
        sb.AppendLine();
        sb.AppendLine("The app handles these itself, before you see anything: /help, /clear, "
                    + "/compress (summarise the conversation to free room), /exit. You cannot run "
                    + "them — mention one only to suggest it.");
        sb.AppendLine();

        sb.AppendLine("# Answering");
        sb.AppendLine();
        sb.AppendLine("Be concise. Your output goes to a terminal, so answer in a few lines unless "
                    + "asked for detail, and skip preamble like \"Here is what I found\". When you "
                    + "name code, write it as file_path:line_number so the user can jump to it.");

        return sb.ToString();
    }
}

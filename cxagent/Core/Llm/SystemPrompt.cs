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
    string ModelId)
{
    /// <summary>
    /// Each connected MCP server's own usage prose from its <c>initialize</c> response, by server
    /// name. Empty — the common case — contributes nothing at all.
    ///
    /// <para>AN INIT-ONLY PROPERTY, not a sixth positional field. Every construction site would
    /// otherwise have to name it, including the ones that have no idea what MCP is; this way a caller
    /// that has servers says so and nobody else changes.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> McpInstructions { get; init; } =
        new Dictionary<string, string>();
}

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
        // THE USER IS ASKED TO APPROVE run_shell, and the prompt shows the command truncated with no
        // reason attached. Say what a non-obvious one does BEFORE calling it, or they are approving
        // a string they cannot read.
        sb.AppendLine("Before running a non-obvious shell command, say in one line what it does and "
                    + "why — especially if it changes anything. The user is asked to approve it and "
                    + "sees only the command.");
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
        // /mcp is listed unconditionally, like the rest: it is a real command for every user
        // whether or not they have servers, and it is the answer to "a tool I expected is missing" —
        // which the model is the first to notice. Naming it only when servers exist would hide it in
        // exactly the case where the user has configured one that failed to start.
        sb.AppendLine("The app handles these itself, before you see anything: /help, /clear, "
                    + "/compress (summarise the conversation to free room), "
                    + "/mcp (list MCP servers and why any failed), /exit. You cannot run "
                    + "them — mention one only to suggest it.");
        sb.AppendLine();

        sb.AppendLine("# Answering");
        sb.AppendLine();
        sb.AppendLine("Be concise. Your output goes to a terminal, so answer in a few lines unless "
                    + "asked for detail, and skip preamble like \"Here is what I found\". When you "
                    + "name code, write it as file_path:line_number so the user can jump to it.");
        sb.AppendLine();
        // WE DO RENDER IT — MainWindow installs a MarkdownStyle for the transcript. Saying so stops
        // the model either avoiding formatting or emitting things a monospace pane cannot show.
        sb.AppendLine("The transcript renders GitHub-flavoured markdown in a monospace pane, so "
                    + "headings, lists and fenced code all display.");
        sb.AppendLine();
        // A SHELL CALL IS NOT A CHANNEL. `echo "let me look at that"` costs a permission prompt to
        // say something that belongs in the reply, and the user has to approve it to hear it.
        sb.AppendLine("Talk to the user in your reply, never through a tool. Do not echo messages "
                    + "from run_shell or leave notes in code comments to be read.");
        sb.AppendLine();
        sb.AppendLine("If you will not do something, say so in a sentence and offer what you can do. "
                    + "Do not explain at length why it was refused.");
        sb.AppendLine();
        sb.AppendLine("Never write code that logs or hard-codes a secret, and never put one in a "
                    + "file you create.");

        // MCP SERVERS LAST, and only when there are any.
        //
        // Nothing is appended when no server sent instructions — which is both the common case and a
        // cache concern: the system message is the prompt-cache prefix, and a heading that appeared
        // even when empty would change it for every user with no MCP configured, charging them a miss
        // for a feature they are not using.
        //
        // Attributed to the server that said it, the same rule project instructions follow: an
        // unattributed paragraph appended to a system prompt reads as though the app itself said it,
        // leaving the model no way to weigh a server's advice against a general instruction.
        var servers = ctx.McpInstructions
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)   // stable order, or the prefix churns
            .ToList();

        if (servers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# MCP servers");
            sb.AppendLine();
            sb.AppendLine("These tools come from external servers. Each server's own guidance follows; "
                        + "it describes how to use that server, which its individual tool descriptions "
                        + "cannot.");

            foreach (var (name, text) in servers)
            {
                sb.AppendLine();
                sb.AppendLine($"From the '{name}' server:");
                sb.AppendLine();
                sb.AppendLine(text.Trim());
            }
        }

        return sb.ToString();
    }
}

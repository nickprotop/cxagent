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

    /// <summary>
    /// The skills available to this agent — NAME AND DESCRIPTION ONLY. The body is fetched by the
    /// load tool when the model decides a task matches, which is the whole economics of the feature:
    /// twenty skills of 3k each would be 60k of permanent prefix, while their catalog is a few
    /// hundred characters.
    ///
    /// <para>A LIST OF RECORDS, not a name→description dictionary like <see cref="McpInstructions"/>.
    /// A skill also carries a directory and a body that the loader needs, and forking discovery into
    /// a prompt-shaped copy and a loader-shaped copy is two representations to keep in step. The
    /// prompt renders the two fields it wants and ignores the rest.</para>
    ///
    /// <para>Already sorted by name when it arrives — see the catalog. Order is not cosmetic here:
    /// this text rides in the cached prefix, so a list that reshuffled between turns would churn the
    /// cache for nothing.</para>
    /// </summary>
    public IReadOnlyList<Skills.SkillInfo> Skills { get; init; } = [];

    /// <summary>
    /// True when this prompt is for a SUB-AGENT rather than the session's own agent.
    ///
    /// <para>A child would otherwise receive the session prompt unchanged, which is wrong in one
    /// specific way and silent about the thing that matters most. Wrong: the commands section names
    /// /help, /clear, /compress, /mcp and /exit to an agent with NO USER AND NO COMPOSER — it is being
    /// told to suggest commands to someone who will never see them. Silent: nothing tells it that its
    /// final message is the entire answer, so a child that believes it is in a conversation closes
    /// with "let me know if you'd like me to check the other files" and the parent receives a question
    /// nobody can answer.</para>
    ///
    /// <para>AN INIT-ONLY PROPERTY, like <see cref="McpInstructions"/>, and FIXED AT CONSTRUCTION. It
    /// cannot vary within an agent's life, so each agent still produces a byte-identical prefix for
    /// its whole session — two prefixes per session rather than one, which is correct because they are
    /// two different agents.</para>
    /// </summary>
    public bool IsSubAgent { get; init; }

    /// <summary>
    /// True when this agent MAY SPAWN sub-agents — false in single mode, and false for a child.
    ///
    /// <para>Gates the three obligation lines a parent has towards work it did not do itself (D26).
    /// They are correct only for an agent that can actually delegate: in single mode the agent has no
    /// spawn tool, so "a sub-agent's report is a claim, not a verification" describes a capability it
    /// does not have — exactly the argument that keeps those lines off a CHILD, which cannot spawn
    /// either. A prompt that discusses machinery the reader has no access to is noise, and noise in a
    /// system prompt is what trains a model to skim it.</para>
    ///
    /// <para>SEPARATE FROM <see cref="IsSubAgent"/> rather than derived from it. A child is never a
    /// spawner, so one implies the other in ONE direction only — a single-mode parent is not a
    /// sub-agent and still cannot spawn. Collapsing them into one flag would make that case
    /// unrepresentable.</para>
    /// </summary>
    public bool CanSpawn { get; init; }

    /// <summary>
    /// True when this agent can put a question to a person and wait for the answer.
    ///
    /// <para>False for a sub-agent, which has no user, and for any headless host. Gated for the same
    /// reason the spawn lines are: a prompt that describes machinery the reader cannot reach is
    /// noise, and noise in a system prompt is what teaches a model to skim it.</para>
    /// </summary>
    public bool CanAskUser { get; init; }
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
        // NAMES NO TOOL, DELIBERATELY. This said "make changes with write_file or replace_in_file"
        // and was UNCONDITIONAL — so a read-only agent was ordered by its own prompt to use tools it
        // did not have. The advice is true whatever the agent was offered; only the names could go
        // stale, so they are gone rather than gated.
        sb.AppendLine("You have tools. USE THEM: read a file before editing it, and make changes "
                    + "with the tools you have rather than describing them. Text in a message "
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
        sb.AppendLine("Never invent a URL for a request. Use one the user gave you, or one you "
                    + "read from a file in this project.");
        sb.AppendLine();
        sb.AppendLine("Do not commit unless the user asks. Running the tests is expected; committing "
                    + "is theirs to decide.");
        sb.AppendLine();
        // D26, ONE OF THREE. Not spawn guidance — when to spawn lives in the tool description, read
        // at the moment of choosing. These three lines are a parent's obligations towards work it did
        // not do itself, and each is a line that is correct for a lone agent and WRONG once it has a
        // child. Appended unconditionally, for the same reason /mcp is listed for users with no
        // servers: gating them on "has spawned" would remove them from the first spawn, the one turn
        // that needs them, and would change the prefix mid-session.
        if (ctx.CanSpawn)
        {
            // ADAPTED FROM OPENCODE'S anthropic.txt, which carries FOUR separate nudges toward
            // delegating — "prefer to use the Task tool in order to reduce context usage",
            // "proactively use", "VERY IMPORTANT… it is CRITICAL that you use the Task tool instead
            // of running search commands directly", plus two worked examples. Four, in a system
            // prompt, for a capability its tool description already explains.
            //
            // WHY SO MUCH INSISTENCE: a model left to its own judgement UNDER-delegates. Measured
            // here, in fan-out mode, asked "across this whole repository, which controls implement
            // their own keyboard handling — I don't know where they are": TWELVE read_file calls and
            // no spawn, on the exact shape of task the tool description says to delegate. Twelve file
            // dumps permanently in the context, to answer one question. The tool description alone
            // did not move it, which is the finding this line exists to act on.
            //
            // ONE LINE RATHER THAN FOUR, and phrased as a rule about WHERE THE READING LANDS rather
            // than as a preference for a tool. A model can check "am I about to read files I will not
            // need afterwards" against what it is doing; "prefer the Task tool" it can only obey or
            // ignore.
            sb.AppendLine("When answering means searching or reading widely — you do not know which "
                        + "files hold the answer, or there are several — send a sub-agent rather "
                        + "than reading them into this conversation. What you need is its "
                        + "conclusion; the files it opened to find it are not worth the room they "
                        + "take. Read directly only when you already know the file.");
            sb.AppendLine();
            // TYPE-MATCHING AS A REASON TO DELEGATE — opencode's one line on the subject
            // (anthropic.txt:80), and note what it does NOT do: it names no type. The catalog lives
            // in the tool description, read at the moment of choosing (D25); this is the judgement
            // rule, which is a different thing and belongs where judgement is formed.
            //
            // SHIPPED BEFORE THE TYPES THEMSELVES, deliberately. Today's prompting work produced one
            // usable answer out of three attempts, and the difference was always whether a change
            // could be attributed: the worked examples moved a pure search from 0 spawns to 1
            // because nothing else changed in that run. Landing this line together with a generated
            // catalog would change two things at once and explain neither.
            //
            // HARMLESS UNTIL TYPES EXIST. With only the implicit `general` there is nothing to
            // match, so this reads as a restatement of the rule above rather than as a false
            // promise — no session is told about a capability it does not have.
            // WHERE WHAT YOU ALREADY KNOW LANDS — the third rule, and the one with the most
            // evidence behind it. The tool description has said "Put the TASK in prompt, and what you
            // already KNOW in context" since spawning existed, and both the explore and planner type
            // descriptions now repeat it. Measured across three consecutive drives: the parent typed
            // "Here's what I know about the codebase:" followed by entry points, line numbers and a
            // data-flow summary — INTO THE PROMPT, with context left empty, every time.
            //
            // THE COST IS MEASURED, not theoretical. A planner given its facts that way spent ten
            // turns re-reading files the parent had already described to it, because — as the tool
            // description itself says — "the prompt does not survive a long one". Three statements
            // of the rule in the places a model reads while CHOOSING did not move it; this is the
            // place it reads while forming judgement, which is where the other two spawn rules live
            // and where they worked.
            sb.AppendLine("When you send a sub-agent something you already know — where a thing "
                        + "lives, a convention this repo follows, what an earlier agent found — put "
                        + "it in `context`, not in the prompt. A prompt is read once and fades as an "
                        + "agent's own work fills its window; context stays for the whole run. An "
                        + "agent that loses the facts you gave it goes and rediscovers them, and "
                        + "spends its turns doing that instead of the task.");
            sb.AppendLine();

            // AND HOW SPECIFIC THOSE FACTS HAVE TO BE. The rule above fixed WHERE they go and it
            // worked — measured on a later drive, all three spawns split context from prompt without
            // correction. This is the failure that survived it: the parent had READ GpuCapabilities.cs
            // and still wrote only the concept ("capability-gate each metric") into context, with no
            // property names. The builder invented Utilization, Memory, Temperature and Power — none
            // of which exist on that type — and wrote against them for fifty-five turns before its
            // first build said otherwise.
            //
            // A NAME IS CHEAP AND A GUESS IS NOT. Quoting four identifiers costs a line; omitting
            // them cost most of a build run. The planner briefing already states this for plan files
            // ("quote the identifiers a step depends on rather than describing them") — the parent
            // had no equivalent, which is why the gap kept reappearing one spawn earlier.
            sb.AppendLine("When you have read a file the sub-agent will work against, give it the "
                        + "NAMES, not just the pattern: the actual properties, signatures, enum "
                        + "members and paths you saw. A convention described without its identifiers "
                        + "is something they have to guess at, and a plausible guess compiles into a "
                        + "great deal of work before anything contradicts it.");
            sb.AppendLine();
            sb.AppendLine("If one of the agent types you are offered fits the task at hand, use it "
                        + "rather than doing the work yourself — a type exists because someone "
                        + "decided that work is better done by an agent briefed for it.");
            sb.AppendLine();
            // WORKED EXAMPLES, opencode's shape (<example> blocks with a bracketed action). Two
            // interventions had already failed to move this — a sharpened tool description and the
            // rule above — and an example is the one lever opencode has that we did not: it shows
            // the SHAPE of the decision at the moment of choosing, where a rule only asserts one.
            //
            // THE THIRD IS A COUNTER-EXAMPLE, deliberately. Two positives alone teach "spawn when
            // asked about code", and a model that over-corrects into delegating a one-file read is
            // no better than one that never delegates — it pays a full run to learn what a single
            // read_file would have told it. The pair is what draws the line.
            sb.AppendLine("<example>");
            sb.AppendLine("user: Where is retry logic handled?");
            sb.AppendLine("assistant: [sends a sub-agent to find it, rather than grepping and "
                        + "reading the matches here]");
            sb.AppendLine("</example>");
            sb.AppendLine("<example>");
            sb.AppendLine("user: How does this project structure its tests?");
            sb.AppendLine("assistant: [sends a sub-agent]");
            sb.AppendLine("</example>");
            sb.AppendLine("<example>");
            sb.AppendLine("user: What does Parser.cs:88 do?");
            sb.AppendLine("assistant: [reads the file directly — the location is already known]");
            sb.AppendLine("</example>");
            // THE MIXED TASK, and the reason for a fourth. The three above are pure lookups: they
            // move a "where is X?" question and leave a task with two halves untouched, because
            // there is nothing for a model to pattern-match a mixed task against.
            //
            // MEASURED: asked to find every hand-rolled markup escape in a repo AND replace the safe
            // ones, this model did all of it inline — 50 messages, 168,249 chars, no spawn — when the
            // finding half was a textbook delegation and only the deciding half needed the
            // conversation. The work was correct; it simply cost nine times what it needed to.
            //
            // FOUR IS THE CEILING. opencode ships two Task-tool examples; ours carries a
            // counter-example and this split, which is one distinction more than theirs has to
            // teach. Past that, examples stop being a pattern and become a list nobody reads.
            sb.AppendLine("<example>");
            sb.AppendLine("user: Find every place that hand-rolls this, and fix the ones that are "
                        + "safe to change.");
            sb.AppendLine("assistant: [sends a sub-agent to find them all, then decides and edits "
                        + "here — the finding is a search, the deciding needs this conversation]");
            sb.AppendLine("</example>");
            sb.AppendLine();
            sb.AppendLine("You are accountable for a sub-agent's work as if it were your own. If its "
                        + "report is thin or does not answer what you asked, say so or check it "
                        + "yourself — do not pass it on as fact.");
            sb.AppendLine();
        }
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
        // D26, THE MOST VALUABLE OF THE THREE. Without it this ENTIRE SECTION is dead on the
        // delegated path: every rule above is written about output THE MODEL READ, and a child's
        // summary is neither the output nor the model's own reading of it — so a parent can satisfy
        // every line here while verifying nothing. That is the live-drive failure this section was
        // written to fix ("all tests build and pass cleanly" over a file that did not compile),
        // arriving through a layer the text does not reach.
        if (ctx.CanSpawn)
        {
            sb.AppendLine("A sub-agent's report is a claim, not a verification. If it says the build "
                        + "passes, the build is not verified until you have seen the output "
                        + "yourself.");
            sb.AppendLine();

            // THE MECHANISM, STATED. Both references say it outright rather than trusting a model to
            // infer that several tool calls in one message may be several agents — Claude Code's
            // Agent tool ("send them in a single message with multiple tool uses so they run
            // concurrently") and opencode's task.txt ("Launch multiple agents concurrently whenever
            // possible"). Here rather than only in the tool description because the description
            // answers "should I delegate THIS?" at the moment of choosing, while this answers "what
            // shape does a delegating turn take?", which is a fact about the session.
            sb.AppendLine("Several agents can run at once: put their calls in one message and they "
                        + "work concurrently. Only for genuinely independent work — anything that "
                        + "depends on another agent's answer is a later turn, not a parallel one.");
            sb.AppendLine();
        }

        // THE MODEL CANNOT RUN THESE — the app intercepts them before a turn starts. It is told
        // about them so it can point the user at one, and so a "/help" typed at it is recognised as
        // a command the app handles rather than answered as prose.
        // DROPPED FOR A CHILD — the one block that is actively WRONG rather than merely unhelpful.
        // It names /help, /clear, /compress, /mcp and /exit to an agent with no user and no composer,
        // instructing it to suggest commands to someone who will never see them.
        if (!ctx.IsSubAgent)
        {
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
        }

        // REPLACED FOR A CHILD, not appended to. Everything below addresses a HUMAN READING A
        // TERMINAL — be concise for a terminal, render markdown, talk to the user in your reply — and
        // a child's reader is a model. Adding a second block instead of replacing would leave the
        // child with two sets of answering instructions that disagree.
        if (ctx.IsSubAgent)
        {
            sb.AppendLine("# Answering");
            sb.AppendLine();
            sb.AppendLine("You are a sub-agent. Another agent gave you this task and is waiting for "
                        + "one message back.");
            sb.AppendLine();
            sb.AppendLine("Your final message is the whole of what it receives — nothing else you do "
                        + "is visible to it. Put the answer there, with the specifics: file paths as "
                        + "file_path:line_number, names, and what you actually observed. A summary "
                        + "that omits where you looked cannot be checked or used.");
            sb.AppendLine();
            sb.AppendLine("There is no follow-up. Nobody will answer a question, approve a plan, or "
                        + "ask you to continue — so do not ask one, do not offer next steps, and do "
                        + "not close by describing what you could do instead.");
            sb.AppendLine();
            sb.AppendLine("If you could not do what was asked, say that plainly and say how far you "
                        + "got. A partial answer marked partial is useful; a confident answer that is "
                        + "not backed by what you saw is not.");
            sb.AppendLine();
            sb.AppendLine("Be brief in the way a report is brief, not the way a chat message is. No "
                        + "preamble.");
            sb.AppendLine();

            AppendMcpInstructions(sb, ctx);
            AppendSkills(sb, ctx);
            return sb.ToString();
        }

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
                    + "through a shell command or leave notes in code comments to be read.");
        sb.AppendLine();
        // D26, THREE OF THREE. The tool description says this too, and the duplication is the point:
        // that is read when CHOOSING to spawn, this applies when ANSWERING, often many turns and
        // several tool calls later. D22 is what makes it true — nothing of the child renders but its
        // row — so the parent is the only channel there is.
        if (ctx.CanSpawn)
        {
            sb.AppendLine("The user cannot see a sub-agent's work. Anything from one that they need "
                        + "is only in your reply.");
            sb.AppendLine();
        }
        sb.AppendLine("If you will not do something, say so in a sentence and offer what you can do. "
                    + "Do not explain at length why it was refused.");
        sb.AppendLine();

        // ENDING A TURN WITH A QUESTION IS THE FAILURE THIS PREVENTS. Seen on the first drive of the
        // tool: three config files, an ambiguous request, and the model wrote "Which one should I
        // change?" as its final message — which reads as an answer, is not one, and costs the user a
        // turn to repair. The tool was offered; nothing told the model it existed, because a tool
        // description is only read once the model is already considering that tool.
        //
        // IT USED TO END BY ARGUING AGAINST ITSELF — "ask only when you cannot settle it yourself".
        // Together with a tool description that spent a paragraph on when NOT to ask, the model
        // stopped calling the tool at all: on a later drive it wanted to consult the user and asked
        // in PROSE, which is precisely the failure this paragraph exists to prevent. The restraint
        // now lives once, in the tool description, and this says what to do instead.
        if (ctx.CanAskUser)
        {
            sb.AppendLine("When you need something from the user — which of several files they "
                        + "meant, a preference, a decision on an implementation choice — call "
                        + "ask_user and wait. Do NOT end your turn with a question in the text: "
                        + "that reads as an answer, and nobody is listening for a reply to it. Ask "
                        + "several related things in ONE call rather than one at a time, and give "
                        + "each option a description so the choice can be made without guessing "
                        + "what you meant.");
            sb.AppendLine();
        }
        sb.AppendLine("Never write code that logs or hard-codes a secret, and never put one in a "
                    + "file you create.");

        AppendMcpInstructions(sb, ctx);
        AppendSkills(sb, ctx);

        return sb.ToString();
    }

    /// <summary>
    /// MCP SERVERS LAST, and only when there are any. Shared by both prompts — a child gets its
    /// parent's toolset (D21), so a server's guidance is exactly as relevant to it.
    ///
    /// <para>Nothing is appended when no server sent instructions, which is both the common case and a
    /// cache concern: the system message is the prompt-cache prefix, and a heading that appeared even
    /// when empty would change it for every user with no MCP configured, charging them a miss for a
    /// feature they are not using.</para>
    ///
    /// <para>Attributed to the server that said it, the same rule project instructions follow: an
    /// unattributed paragraph appended to a system prompt reads as though the app itself said it,
    /// leaving the model no way to weigh a server's advice against a general instruction.</para>
    /// </summary>
    private static void AppendMcpInstructions(StringBuilder sb, SystemPromptContext ctx)
    {
        var servers = ctx.McpInstructions
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)   // stable order, or the prefix churns
            .ToList();

        if (servers.Count == 0) return;

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

    /// <summary>
    /// The skills this agent can load, as names and descriptions.
    ///
    /// <para>OMITTED ENTIRELY WHEN THERE ARE NONE — not an empty heading, not "no skills available".
    /// Most sessions have no skills, and those users should pay nothing for the feature: an empty
    /// section is permanent prefix bytes charged to everyone for a capability nobody is using. The
    /// same early return <see cref="AppendMcpInstructions"/> makes, for the same reason.</para>
    ///
    /// <para>THE DESCRIPTION IS THE ENTIRE INTERFACE. It is all the model sees before deciding, so it
    /// is rendered verbatim from the file rather than summarised — a skill whose description says
    /// WHEN to use it will be found, and one that reads like a title will not.</para>
    ///
    /// <para>TAGGED RATHER THAN BULLETED, matching both reference implementations. The catalog sits
    /// among prose the model must not confuse it with; delimiters make "these are the available
    /// skills" unambiguous where a markdown list would blend into the surrounding instructions.</para>
    /// </summary>
    private static void AppendSkills(StringBuilder sb, SystemPromptContext ctx)
    {
        // NO RE-SORT. The catalog is already ordered by name, and sorting again here would put the
        // guarantee in two places — the second one silently wrong the day the first changes.
        var skills = ctx.Skills;
        if (skills.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine("# Skills");
        sb.AppendLine();
        sb.AppendLine("Specialised instructions you can load when a task matches one. Call "
                    + "skill with the name to read it; the description tells you when it "
                    + "applies. Load one BEFORE starting work it covers, not after.");
        sb.AppendLine();
        // SAID HERE AND IN THE TOOL DESCRIPTION, because the first live drive showed that naming the
        // tool is not enough: the model located the SKILL.md with list_files and read it with
        // read_file — the tool it reaches for whenever it holds a path. The instructions still
        // arrived, so the WORK was right and every downstream surface was wrong: no marker meant the
        // worker row, the session panel and the compaction notice all reported no skill in force.
        sb.AppendLine("Use the skill tool for this, never a file tool. Reading a SKILL.md directly gets "
                    + "you the text but does not register the skill, so nothing else in the session "
                    + "knows it is in force — and you will not be told when it is compacted away. "
                    + "A skill may ship other files; those are listed by full path when you load it, "
                    + "and you read them with the file tool as usual.");
        sb.AppendLine();
        sb.AppendLine("<available_skills>");

        foreach (var skill in skills)
        {
            sb.AppendLine("  <skill>");
            sb.AppendLine($"    <name>{skill.Name}</name>");
            sb.AppendLine($"    <description>{skill.Description}</description>");
            sb.AppendLine("  </skill>");
        }

        sb.AppendLine("</available_skills>");
    }
}

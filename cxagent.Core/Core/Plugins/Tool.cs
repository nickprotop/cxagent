namespace CxAgent.Core.Plugins;

/// <summary>
/// The wire names, as constants, for writing a <see cref="ToolSelection"/> in C#.
///
/// <para>WHY THIS EXISTS: a selection is a list of strings, and an unknown name is ignored silently
/// — deliberately, because names arrive late and a typo in a removal must be harmless. The cost is
/// that a typo in an INCLUSION is also silent: <c>["read_files"]</c> yields an agent with no file
/// read and no error. These constants move that failure to compile time for the one caller who can
/// have it, the embedder writing C#.</para>
///
/// <para>THE ENUM MEMBER IS NOT THE WIRE NAME. <c>WorkerTool.ListFiles</c> is offered as
/// <c>glob</c> and <c>WorkerTool.SearchFiles</c> as <c>grep</c>, which is exactly the mistake this
/// removes: a caller reaching for the enum spelling selects nothing.</para>
///
/// <para>CONFIG CANNOT USE THESE and does not need to — a JSON file writes the same strings, and
/// its terms are validated at load. This is the code-level convenience only.</para>
///
/// <para>MCP TOOLS ARE ABSENT ON PURPOSE. They are not selectable: <c>enabled</c> per server is
/// their control, and their names are composed at runtime from a server name we cannot know here.</para>
/// </summary>
public static class Tool
{
    /// <summary>Start from what the level above left. Without it (or <see cref="All"/>) a list is an
    /// exact set.</summary>
    public const string Inherited = "inherited";

    /// <summary>
    /// Start over from everything this agent COULD have, discarding what earlier levels removed.
    ///
    /// <para>The one term that widens. Identical to <see cref="Inherited"/> at the manager level,
    /// where nothing has narrowed yet — it earns its keep below that, where a session or a turn
    /// wants the full set back rather than what it was handed.</para>
    ///
    /// <para>Still bounded by what the agent structurally has: <c>all</c> on a sub-agent never
    /// produces <c>ask_user</c>.</para>
    /// </summary>
    public const string All = "all";

    // --- The eight built-ins (WorkerTool), by the name the MODEL sees ------------------

    public const string ReadFile = "read_file";
    public const string WriteFile = "write_file";
    public const string ReplaceInFile = "replace_in_file";

    /// <summary>Find files by path pattern. <c>WorkerTool.ListFiles</c>, renamed for the model.</summary>
    public const string Glob = "glob";

    /// <summary>Search file contents. <c>WorkerTool.SearchFiles</c>, renamed for the model.</summary>
    public const string Grep = "grep";

    public const string RunShell = "run_shell";
    public const string HttpRequest = "http_request";
    public const string WebFetch = "web_fetch";

    // --- The four that are not enum members, and are selectable just the same ----------

    /// <summary>Delegate to a sub-agent. Also needs fan-out mode and a spawner — a selection cannot
    /// grant it on its own.</summary>
    public const string Agent = "agent";

    public const string TodoWrite = "todowrite";

    /// <summary>Ask the user. Never available to a sub-agent, whatever a selection says.</summary>
    public const string AskUser = "ask_user";

    /// <summary>Load a skill. Offered only when the catalog is non-empty.</summary>
    public const string Skill = "skill";

    /// <summary>
    /// Every name this build ships, for telling "withheld" apart from "no such tool".
    ///
    /// <para>MCP AND INJECTED TOOLS ARE ABSENT: their names are not knowable here — one comes from a
    /// server at runtime, the other from an embedder. A caller that needs those asks their own
    /// source, which is what Agent.Withheld does.</para>
    /// </summary>
    public static bool IsKnown(string name) => Names.Contains(name);

    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        ReadFile, WriteFile, ReplaceInFile, Glob, Grep, RunShell, HttpRequest, WebFetch,
        Agent, TodoWrite, AskUser, Skill,
    };

    /// <summary>
    /// Remove a tool: <c>Tool.Not.RunShell</c> is <c>"-run_shell"</c>.
    ///
    /// <para>A removal that matches nothing is harmless, which is the grammar's safety property —
    /// so these are the terms a typo cannot hurt. They are still constants because the reader of a
    /// selection deserves to see a name rather than a hyphenated string.</para>
    /// </summary>
    public static class Not
    {
        public const string ReadFile = "-" + Tool.ReadFile;
        public const string WriteFile = "-" + Tool.WriteFile;
        public const string ReplaceInFile = "-" + Tool.ReplaceInFile;
        public const string Glob = "-" + Tool.Glob;
        public const string Grep = "-" + Tool.Grep;
        public const string RunShell = "-" + Tool.RunShell;
        public const string HttpRequest = "-" + Tool.HttpRequest;
        public const string WebFetch = "-" + Tool.WebFetch;
        public const string Agent = "-" + Tool.Agent;
        public const string TodoWrite = "-" + Tool.TodoWrite;
        public const string AskUser = "-" + Tool.AskUser;
        public const string Skill = "-" + Tool.Skill;
    }

    /// <summary>
    /// Add a tool back after an earlier level removed it: <c>Tool.Also.Grep</c> is <c>"+grep"</c>.
    ///
    /// <para>ONLY MEANINGFUL IN A LATER LEVEL. A bare name includes just as well in a level of its
    /// own; <c>+</c> says "even though something before me removed this", which composition makes
    /// true by arriving later in the term list. It still cannot reach a tool the agent structurally
    /// lacks — no <c>Also.AskUser</c> gives a child a user to ask.</para>
    /// </summary>
    public static class Also
    {
        public const string ReadFile = "+" + Tool.ReadFile;
        public const string WriteFile = "+" + Tool.WriteFile;
        public const string ReplaceInFile = "+" + Tool.ReplaceInFile;
        public const string Glob = "+" + Tool.Glob;
        public const string Grep = "+" + Tool.Grep;
        public const string RunShell = "+" + Tool.RunShell;
        public const string HttpRequest = "+" + Tool.HttpRequest;
        public const string WebFetch = "+" + Tool.WebFetch;
        public const string Agent = "+" + Tool.Agent;
        public const string TodoWrite = "+" + Tool.TodoWrite;
        public const string AskUser = "+" + Tool.AskUser;
        public const string Skill = "+" + Tool.Skill;
    }
}

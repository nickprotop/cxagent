using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Skills;

/// <summary>
/// The <c>skill</c> tool: hands the model a skill's body when it decides a task matches one of
/// the descriptions in its catalog.
///
/// <para>AGENT-OWNED, NOT AN <c>IJobExecutor</c>, AND THAT IS FORCED RATHER THAN PREFERRED. Answering
/// "have I already loaded this?" means reading the agent's own message list, and an executor cannot:
/// <c>IJobContext</c> carries progress, log and telemetry callbacks plus <c>Requester</c> — whose own
/// documentation calls it "A LABEL, NOT AN ID". With several children running concurrently through
/// one shared executor instance, an executor could not even tell which of them was asking. So this sits
/// beside the spawner in the dispatch chain, which is the same shape for the same reason.</para>
///
/// <para>NO STORED STATE AT ALL. What has been loaded is DERIVED from the conversation, so it cannot
/// drift from it: after a compaction removed a body, the scan simply stops finding it and the next
/// call returns the body again. A tracked set would need a compaction hook, and the obvious hook is
/// wrong twice — it misses the truncation fallback, and it over-clears, because compaction removes
/// only the older half while a recently-loaded body survives and must still count as loaded.</para>
/// </summary>
public sealed class SkillLoader
{
    /// <summary>
    /// Marks a message as a skill body, and says WHICH skill.
    ///
    /// <para>THE NAME IS IN THE MARKER BECAUSE THE SCAN CANNOT JOIN ACROSS MESSAGES. A tool result
    /// records only its <c>ToolCallId</c> and content — the tool's NAME lives on the <c>ToolCall</c>
    /// in the preceding assistant message. Pairing the two would be a two-pass join, and it breaks in
    /// exactly the case this scan exists for: compaction can remove one half and leave the other. A
    /// self-describing body needs no join.</para>
    ///
    /// <para>The same recogniser serves compaction, which sees a bare message list with no join
    /// available either. One rule, two consumers.</para>
    /// </summary>
    public const string BodyMarkerPrefix = "[skill: ";

    private readonly Func<SkillCatalogResult> _catalog;

    /// <param name="catalog">Reads the current catalog. A FUNCTION rather than a snapshot, because
    /// discovery runs per turn: a skill added mid-session is loadable from the turn its description
    /// appears in the prompt, and the two would otherwise disagree.</param>
    public SkillLoader(Func<SkillCatalogResult> catalog) => _catalog = catalog;

    /// <summary>
    /// The catalog as it stands right now. Exposed so the caller can decide whether to OFFER this
    /// tool at all: a load tool with an empty catalog advertises a capability whose every call fails.
    /// </summary>
    public SkillCatalogResult Catalog() => _catalog();

    public string ToolName => "skill";

    /// <summary>
    /// Hand-built, like the spawner's and MCP's: there is no executor and no <c>JobSchema</c> behind
    /// this, so <c>ToolBindings.BuildDefinition</c>'s drift guard has nothing to guard.
    ///
    /// <para>IT SAYS "DO NOT READ THE FILE" BECAUSE A MODEL OTHERWISE WILL. On the first live drive
    /// the model saw the catalog, decided it wanted the skill, then found the SKILL.md with
    /// <c>list_files</c> and read it with <c>read_file</c> — the tool it reaches for whenever it holds
    /// a path. The instructions arrived and the work was correct, but nothing downstream of loading
    /// ran: no marker, so the row, the panel and the compaction notice all believed no skill was in
    /// force. A description that only says what this tool DOES leaves the file sitting there as an
    /// equally good answer.</para>
    /// </summary>
    public ToolDefinition Definition => new(
        ToolName,
        "Load a specialised skill when the task at hand matches one of the available skills listed "
        + "in your system context. Returns the skill's full instructions. "
        + "ALWAYS use this tool to read a skill — never open its SKILL.md with a file tool, even if "
        + "you know the path. Only this tool records that the skill is in force. "
        + "Any OTHER file it ships with is listed by full path in the result, and you read those "
        + "with the ordinary file tool, only if the instructions point you at one.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "name": {
                  "type": "string",
                  "description": "The exact skill name from <available_skills>."
                }
              },
              "required": ["name"]
            }
            """).RootElement.Clone());

    /// <summary>
    /// Runs the call, or returns null if it is not this tool's.
    /// </summary>
    /// <param name="messages">
    /// The agent's own conversation — READ, never written. This is what makes "already loaded"
    /// answerable without state, and it is why the tool must be agent-owned.
    /// </param>
    /// <param name="call">The skill call the model issued; null is returned when it is not one.</param>
    public string? TryInvoke(ToolCall call, IReadOnlyList<ChatMessage> messages)
    {
        if (!string.Equals(call.Name, ToolName, StringComparison.Ordinal)) return null;

        var catalog = _catalog();
        var requested = ReadName(call);

        if (string.IsNullOrWhiteSpace(requested))
            return "skill needs a 'name'. " + Available(catalog);

        var skill = catalog.Skills.FirstOrDefault(
            s => string.Equals(s.Name, requested, StringComparison.OrdinalIgnoreCase));

        // AN ERROR STRING, NOT AN EXCEPTION, and it names the valid skills — the same courtesy the
        // spawner extends for an unknown agent type. A refusal the model can act on beats a turn
        // that ends.
        if (skill is null)
            return $"No skill named '{requested}'. " + Available(catalog);

        // ALREADY LOADED: A SHORT ACK, NOT THE BODY AGAIN. A model that forgets what it loaded is
        // likely rather than exotic — the body drifts far up the context — and re-sending a 3k
        // document would put two copies in the window for no gain.
        //
        // BUT IT MUST STILL RETURN SOMETHING. Every tool call needs its result message or the
        // assistant message that made it is left holding an unanswered call, which 400s the session
        // permanently. "Already loaded" is a legitimate answer; silence is a broken conversation.
        if (IsLoaded(messages, skill.Name))
            return $"The '{skill.Name}' skill is already loaded earlier in this conversation. "
                 + "Its instructions are above — follow them.";

        return $"{BodyMarkerPrefix}{skill.Name}]\ndirectory: {skill.Directory}"
             + Files(skill)
             + $"\n\n{skill.Body}";
    }

    /// <summary>
    /// The files that ship with a skill, listed by ABSOLUTE path.
    ///
    /// <para>WHY THEY ARE LISTED AND NOT JUST IMPLIED. Published skills carry reference documents,
    /// and their bodies link them the way markdown does — <c>[references/patterns.md](references/patterns.md)</c>
    /// — which is a path relative to a directory the MODEL CANNOT SEE. It would have to know where
    /// the skill lives, join the two itself, and hope. Both skills in this repository do exactly this
    /// under a heading that says "Load References", so the failure is not hypothetical.</para>
    ///
    /// <para>ABSOLUTE, so the model can pass one straight to the file tool. A relative path here
    /// would be resolved against the WORKING DIRECTORY rather than the skill's, which is the same
    /// confusion one level down.</para>
    ///
    /// <para>NAMED, NOT INLINED. The point of the catalog/body split is that a skill costs what it is
    /// worth: inlining every reference would hand back the 60k prefix the design exists to avoid, and
    /// most references go unread on most tasks. The model reads the one it needs, through the
    /// ordinary permission-gated file tool — no second capability channel, and the gate that already
    /// governs every other read governs these.</para>
    ///
    /// <para>A PROJECT SKILL'S FILES ARE INSIDE THE WORKING BOUNDARY and read without a prompt; a
    /// GLOBAL skill's are in the config directory, outside it, and asking is correct — those files
    /// are not part of the repository the user is working in.</para>
    /// </summary>
    private static string Files(SkillInfo skill)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(skill.Directory)) return "";
            files = Directory
                .EnumerateFiles(skill.Directory, "*", SearchOption.AllDirectories)
                // The body is already in this message; listing it invites a re-read of what the model
                // is holding.
                .Where(f => !string.Equals(Path.GetFileName(f), "SKILL.md", StringComparison.Ordinal))
                // Ordinal, for the same reason the catalog is sorted: filesystem order is not stable,
                // and this text lands in the window.
                .OrderBy(f => f, StringComparer.Ordinal)
                .Take(FileListLimit + 1)
                .ToArray();
        }
        catch (Exception)
        {
            // A skill whose directory cannot be listed still loads. Its body is the substance.
            return "";
        }

        if (files.Length == 0) return "";

        var shown = files.Take(FileListLimit).Select(f => $"\n  {f}");
        var more = files.Length > FileListLimit
            ? $"\n  …and more in this directory"
            : "";

        return "\nfiles (read one with the file tool if the instructions below point at it):"
             + string.Concat(shown) + more;
    }

    /// <summary>
    /// How many files are named before the list is cut short.
    ///
    /// <para>A skill with forty reference documents would otherwise spend more of the window on its
    /// own file listing than on its instructions — and this text is a tool result, so it is re-sent
    /// on every subsequent turn. Twenty names the real cases (both skills here ship three) and stops
    /// a pathological one from becoming the message.</para>
    /// </summary>
    private const int FileListLimit = 20;

    /// <summary>
    /// Is this skill's body still in the window?
    ///
    /// <para>FILTERS ON <c>ToolCallId</c>, NEVER ON <c>Role == "tool"</c>. <see cref="ChatMessage"/>
    /// says so itself: the wire builders overwrite the role — OpenAI sets "tool", Anthropic emits a
    /// "user" turn carrying a tool_result block — and <c>ToolCallId</c> is the only reliable marker
    /// of a tool result.</para>
    /// </summary>
    private static bool IsLoaded(IReadOnlyList<ChatMessage> messages, string name) =>
        LoadedIn(messages).Contains(name, StringComparer.Ordinal);

    /// <summary>
    /// Every skill whose body is still in this window, in load order.
    ///
    /// <para>ONE RECOGNISER, THREE CONSUMERS: the "already loaded" answer above, the worker row that
    /// says which skills are shaping a child's behaviour, and compaction naming what it removed. A
    /// second implementation of "is this a skill body?" is the kind of duplicate that stays correct
    /// until the marker changes and then disagrees silently.</para>
    ///
    /// <para>FILTERS ON <c>ToolCallId</c>, NEVER ON <c>Role == "tool"</c>. <see cref="ChatMessage"/>
    /// says so itself: the wire builders overwrite the role — OpenAI sets "tool", Anthropic emits a
    /// "user" turn carrying a tool_result block — and <c>ToolCallId</c> is the only reliable marker
    /// of a tool result. An assistant message that merely QUOTES the marker is not a loaded body.</para>
    /// </summary>
    public static IReadOnlyList<string> LoadedIn(IReadOnlyList<ChatMessage> messages)
    {
        List<string>? names = null;

        foreach (var message in messages)
        {
            if (message.ToolCallId is null) continue;
            if (!message.Content.StartsWith(BodyMarkerPrefix, StringComparison.Ordinal)) continue;

            var end = message.Content.IndexOf(']', BodyMarkerPrefix.Length);
            if (end <= BodyMarkerPrefix.Length) continue;

            var name = message.Content[BodyMarkerPrefix.Length..end];
            names ??= [];
            if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);
        }

        return (IReadOnlyList<string>?)names ?? [];
    }

    /// <summary>
    /// What the model could have asked for. Listed on every refusal, because a model that guessed a
    /// name wrong has no other way to discover the right one from inside a turn.
    /// </summary>
    private static string Available(SkillCatalogResult catalog) =>
        catalog.Skills.Count == 0
            ? "No skills are available in this session."
            : "Available skills: " + string.Join(", ", catalog.Skills.Select(s => s.Name)) + ".";

    /// <summary>
    /// The requested name, tolerating a model that sends a bare string or omits the argument. A
    /// malformed call is answered, never thrown on.
    /// </summary>
    private static string? ReadName(ToolCall call)
    {
        try
        {
            if (call.Arguments.ValueKind == JsonValueKind.String) return call.Arguments.GetString();
            if (call.Arguments.ValueKind != JsonValueKind.Object) return null;
            if (call.Arguments.TryGetProperty("name", out var name))
                return name.ValueKind == JsonValueKind.String ? name.GetString() : name.ToString();
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

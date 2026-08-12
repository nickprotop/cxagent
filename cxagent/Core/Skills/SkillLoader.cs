using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Skills;

/// <summary>
/// The <c>load_skill</c> tool: hands the model a skill's body when it decides a task matches one of
/// the descriptions in its catalog.
///
/// <para>AGENT-OWNED, NOT AN <c>IJobPlugin</c>, AND THAT IS FORCED RATHER THAN PREFERRED. Answering
/// "have I already loaded this?" means reading the agent's own message list, and a plugin cannot:
/// <c>IJobContext</c> carries progress, log and telemetry callbacks plus <c>Requester</c> — whose own
/// documentation calls it "A LABEL, NOT AN ID". With several children running concurrently through
/// one shared plugin instance, a plugin could not even tell which of them was asking. So this sits
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

    public string ToolName => "load_skill";

    /// <summary>
    /// Hand-built, like the spawner's and MCP's: there is no plugin and no <c>JobSchema</c> behind
    /// this, so <c>WorkerToolset.BuildDefinition</c>'s drift guard has nothing to guard.
    /// </summary>
    public ToolDefinition Definition => new(
        ToolName,
        "Load a specialised skill when the task at hand matches one of the available skills listed "
        + "in your system context. Returns the skill's full instructions.",
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
    public string? TryInvoke(ToolCall call, IReadOnlyList<ChatMessage> messages)
    {
        if (!string.Equals(call.Name, ToolName, StringComparison.Ordinal)) return null;

        var catalog = _catalog();
        var requested = ReadName(call);

        if (string.IsNullOrWhiteSpace(requested))
            return "load_skill needs a 'name'. " + Available(catalog);

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

        return $"{BodyMarkerPrefix}{skill.Name}]\ndirectory: {skill.Directory}\n\n{skill.Body}";
    }

    /// <summary>
    /// Is this skill's body still in the window?
    ///
    /// <para>FILTERS ON <c>ToolCallId</c>, NEVER ON <c>Role == "tool"</c>. <see cref="ChatMessage"/>
    /// says so itself: the wire builders overwrite the role — OpenAI sets "tool", Anthropic emits a
    /// "user" turn carrying a tool_result block — and <c>ToolCallId</c> is the only reliable marker
    /// of a tool result.</para>
    /// </summary>
    private static bool IsLoaded(IReadOnlyList<ChatMessage> messages, string name)
    {
        var marker = BodyMarkerPrefix + name + "]";
        foreach (var message in messages)
            if (message.ToolCallId is not null
                && message.Content.StartsWith(marker, StringComparison.Ordinal))
                return true;

        return false;
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

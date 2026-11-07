using System.Text;
using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Memory;

/// <summary>
/// <c>memory</c> — the model keeps, reads and drops notes that outlive the conversation.
///
/// <para>AGENT-OWNED, like <c>todowrite</c>: it answers from the store the agent was built with, not from an executor,
/// and sits in the dispatch chain beside it.</para>
///
/// <para>NO PERMISSION GATE, by the user's decision: a write touches only cxagent's own notes, each one is a visible
/// tool row, and <c>/memory forget</c> undoes it. A prompt per note would teach reflexive approval.</para>
///
/// <para>READ-ONLY FOR A SUB-AGENT. A child sees a slice of the work and has no user to learn from; what is worth
/// keeping is its parent's call. Withheld from the schema as well as refused, so it is never offered what it may not do.</para>
/// </summary>
/// <param name="store">The notes.</param>
/// <param name="readOnly">True for a sub-agent: list and read only.</param>
/// <param name="written">Told the name of each note this tool wrote or deleted — so the session knows what the model
/// already knows, and tells it only of changes made elsewhere.</param>
public sealed class MemoryTool(MemoryStore store, bool readOnly, Action<string>? written = null)
{
    public const string ToolName = "memory";

    /// <summary>The store this tool reads and writes.</summary>
    public MemoryStore Store => store;

    public ToolDefinition Definition => new(
        ToolName,
        readOnly
            ? "Read the notes kept from earlier sessions — about the user, and about this project. The index of "
              + "names is in your instructions; read a note before relying on what its description suggests. You "
              + "can read notes but not write them: if you learn something worth keeping, say so in your report."
            : "Keep notes that outlive this conversation, and read the ones kept before. The index of names is in "
              + "your instructions.\n\n"
              + "Write a note when you learn something a colleague would remember next time: a correction or "
              + "preference the user stated, how they like to work, or a fact about this project the code and its "
              + "history do not say. Scope 'global' is about the user, in every folder; 'project' is about this "
              + "project only. Update a note rather than adding a near-duplicate, and delete one that turned out "
              + "wrong.\n\n"
              + "Do not keep secrets, anything the code, git history or README already records, or what only "
              + "matters to this conversation. Write the reason with the fact, so a later session can judge it.",
        JsonDocument.Parse($$"""
            {
              "type": "object",
              "properties": {
                "action": { "type": "string", "enum": [{{(readOnly ? "\"list\", \"read\"" : "\"list\", \"read\", \"write\", \"delete\"")}}] },
                "name": { "type": "string", "description": "The note's kebab-case name, like prefers-tabs. Not needed for list." }{{(readOnly ? "" : """
            ,
                "scope": { "type": "string", "enum": ["global", "project"], "description": "write only: global is about the user, project about this project." },
                "description": { "type": "string", "description": "write only: one line saying what the note is, shown in the index." },
                "content": { "type": "string", "description": "write only: the note, in markdown. Replaces what it said before." }
            """)}}
              },
              "required": ["action"]
            }
            """).RootElement.Clone());

    /// <summary>Runs the call, or returns null if it is not this tool's.</summary>
    public string? TryInvoke(ToolCall call)
    {
        if (!string.Equals(call.Name, ToolName, StringComparison.Ordinal)) return null;
        var action = Arg(call, "action")?.Trim().ToLowerInvariant();
        var name = Arg(call, "name")?.Trim() ?? "";
        try
        {
            return action switch
            {
                "list" => List(),
                "read" => Read(name),
                "write" or "delete" when readOnly =>
                    "You can read notes but not write or delete them. If something is worth keeping, say so in your report.",
                "write" => Write(call, name),
                "delete" => Delete(name),
                _ => "memory needs an action: list, read" + (readOnly ? "." : ", write or delete."),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"memory could not reach its notes: {e.Message}";
        }
    }

    private string List()
    {
        var notes = store.List();
        if (notes.Count == 0) return "No notes yet.";
        var sb = new StringBuilder();
        foreach (var n in notes)
            sb.Append(n.Scope == MemoryScope.Global ? "global" : "project").Append(" · ").Append(n.Name)
              .Append(n.Description.Length > 0 ? " — " + n.Description : "").Append('\n');
        return sb.ToString().TrimEnd();
    }

    private string Read(string name)
    {
        if (store.Find(name) is { } note)
            return $"# {note.Name} ({(note.Scope == MemoryScope.Global ? "global" : "project")})\n{note.Description}\n\n{note.Body}";
        // THE NAMES THAT ARE, so a misremembered one is fixed in the next call rather than by a list call first.
        var names = store.List().Select(n => n.Name).ToList();
        return names.Count == 0
            ? $"There is no note called '{name}' — there are no notes yet."
            : $"There is no note called '{name}'. Notes: {string.Join(", ", names)}.";
    }

    private string Write(ToolCall call, string name)
    {
        var scope = Arg(call, "scope")?.Trim().ToLowerInvariant() switch
        {
            "global" => MemoryScope.Global,
            "project" => (MemoryScope?)MemoryScope.Project,
            _ => null,
        };
        // NO DEFAULT SCOPE: which one a fact belongs to is the decision that matters, and a default would make it silently.
        if (scope is null) return "A write needs a scope: 'global' for something about the user, 'project' for this project.";
        var problem = store.Write(new MemoryNote(name, Arg(call, "description") ?? "", scope.Value, Arg(call, "content") ?? ""));
        if (problem is null) written?.Invoke(name);
        return problem ?? $"Saved '{name}' ({(scope == MemoryScope.Global ? "global" : "project")}). It is in the index once the conversation is cleared or compacted, and in every new session.";
    }

    private string Delete(string name)
    {
        if (!store.Delete(name)) return $"There is no note called '{name}'.";
        written?.Invoke(name);
        return $"Deleted '{name}'.";
    }

    private static string? Arg(ToolCall call, string key) =>
        call.Arguments.ValueKind == JsonValueKind.Object && call.Arguments.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

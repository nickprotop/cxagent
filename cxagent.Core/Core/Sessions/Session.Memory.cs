using System.Text;
using CxAgent.Core.Commands;
using CxAgent.Core.Memory;

namespace CxAgent.Core.Sessions;

/// <summary>The notes this session keeps across sessions, the index its agents' prompts list, and <c>/memory</c>.</summary>
public sealed partial class Session
{
    private readonly object _memoryGate = new();
    private IReadOnlyList<MemoryNote>? _memoryIndex;

    /// <summary>
    /// Every note as the model last knew it, by name: the snapshot the index was taken from, then each note its own
    /// tool wrote or deleted, then each change it was told of. The files are compared with this when a message is sent.
    /// </summary>
    private Dictionary<string, string>? _memoryKnown;

    /// <summary>What this session has done — turns, tool calls, how long it has been open — for every front end.</summary>
    public SessionTally Tally { get; } = new();

    /// <summary>The notes this session reads and writes, or null when memory is off.</summary>
    public MemoryStore? Memory { get; private set; }

    /// <summary>
    /// Points the session at its notes — on every wire, so a config change turning memory off, or a resume that moved
    /// the session to another folder, takes effect. The snapshot is kept unless the folder changed: a <c>/model</c>
    /// switch rebuilds the agent, and its prompt must list what its predecessor's did.
    /// </summary>
    internal void UseMemory(MemoryStore? store)
    {
        lock (_memoryGate)
        {
            if (store?.Project != Memory?.Project || store is null != Memory is null) ForgetSnapshot();
            Memory = store;
        }
    }

    /// <summary>
    /// The notes the index lists: taken the first time an agent asks, then the same list until the conversation is
    /// cleared or compacted.
    ///
    /// <para>FROZEN BECAUSE IT IS THE HEAD OF THE CACHE. The index sits in the system prompt, and a prefix cache stops at
    /// the first byte that differs — a note written mid-session that changed the index would re-send the whole
    /// conversation, hundreds of thousands of tokens, uncached. The model knows what it wrote from its own tool call;
    /// any other change it is told at the end of the conversation (see <see cref="TakeMemoryNote"/>).</para>
    /// </summary>
    internal IReadOnlyList<MemoryNote> MemoryIndex()
    {
        lock (_memoryGate)
        {
            if (_memoryIndex is null)
            {
                _memoryIndex = Memory?.List() ?? [];
                _memoryKnown = _memoryIndex.ToDictionary(n => n.Name, Fingerprint, StringComparer.Ordinal);
            }
            return _memoryIndex;
        }
    }

    /// <summary>The model's own tool wrote or deleted <paramref name="name"/>: it knows, and is not told again.</summary>
    internal void NoteAgentWroteMemory(string name)
    {
        lock (_memoryGate)
        {
            if (_memoryKnown is null) return;
            if (Memory?.Find(name) is { } note) _memoryKnown[name] = Fingerprint(note);
            else _memoryKnown.Remove(name);
        }
    }

    /// <summary>
    /// What changed in the notes since the model last knew them, as one line for the front of the next message — or
    /// null when nothing did, or no index has been taken yet (the first message's prompt is read from the files anyway).
    ///
    /// <para>AT THE END OF THE CONVERSATION, NOT IN THE INDEX: appended text leaves the cached prefix whole. Descriptions
    /// only, never bodies — the model reads a note when it needs it, as it does from the index.</para>
    /// </summary>
    internal string? TakeMemoryNote()
    {
        lock (_memoryGate)
        {
            if (_memoryKnown is not { } known || Memory is not { } store) return null;
            var now = store.List();
            var parts = new List<string>();
            foreach (var note in now)
            {
                if (!known.TryGetValue(note.Name, out var was))
                    parts.Add($"'{note.Name}' was added — {Described(note)}");
                else if (was != Fingerprint(note))
                    parts.Add($"'{note.Name}' was edited — {Described(note)}");
            }
            var names = now.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
            parts.AddRange(known.Keys.Where(k => !names.Contains(k)).Order(StringComparer.Ordinal).Select(k => $"'{k}' was deleted"));
            if (parts.Count == 0) return null;

            _memoryKnown = now.ToDictionary(n => n.Name, Fingerprint, StringComparer.Ordinal);
            return "[Memory notes changed outside this conversation: " + string.Join("; ", parts)
                   + ". Read a note again before relying on it.]";
        }
    }

    /// <summary>
    /// The conversation is fresh — cleared, compacted, or another one resumed in its place — and the cache is rebuilt
    /// anyway, so the index may catch up, and what the model knows starts again from it.
    /// </summary>
    internal void RefreshMemoryIndex()
    {
        lock (_memoryGate) ForgetSnapshot();
    }

    private void ForgetSnapshot()
    {
        _memoryIndex = null;
        _memoryKnown = null;
    }

    private static string Fingerprint(MemoryNote note) => note.Description + "\n" + note.Body;

    private static string Described(MemoryNote note) => note.Description.Length > 0 ? note.Description : "no description";

    /// <summary><c>/memory</c>: every note this session sees — the system notes, then this project's — with where they live.</summary>
    public CommandStatus ListMemory()
    {
        if (Memory is not { } store)
        {
            Say(new Message("Memory is off. It is turned on in F8 › General, or with memory.enabled in config.json.", Severity.Info));
            return CommandStatus.Reported;
        }
        var notes = store.List();
        var sb = new StringBuilder();
        foreach (var (scope, heading, folder) in new[]
                 {
                     (MemoryScope.System, "System", store.SystemFolder),
                     (MemoryScope.Project, $"Project ({store.Project ?? "no folder"})", store.ProjectFolder),
                 })
        {
            var mine = notes.Where(n => n.Scope == scope).ToList();
            sb.Append("**").Append(heading).Append("**");
            if (folder is not null) sb.Append(" — `").Append(folder).Append('`');
            sb.Append('\n');
            if (mine.Count == 0) sb.Append("- none yet\n");
            foreach (var n in mine) sb.Append("- `").Append(n.Name).Append('`').Append(n.Description.Length > 0 ? " — " + n.Description : "").Append('\n');
            sb.Append('\n');
        }
        sb.Append("`/memory open <name>` opens a note to read or edit; `/memory forget <name>` deletes one.");
        Say(new Message(sb.ToString(), Severity.Info));
        return CommandStatus.Reported;
    }

    /// <summary>
    /// What <c>/memory forget</c> asks before it deletes, or why it cannot — the question both front ends put, in their
    /// own way, before calling <see cref="ForgetMemory"/>.
    /// </summary>
    public MemoryForgetting AskForgetMemory(string name)
    {
        name = name.Trim();
        if (Memory is not { } store) return new MemoryForgetting(null, "Memory is off in this session.");
        if (name.Length == 0) return new MemoryForgetting(null, "Name the note: /memory forget <name>. /memory lists them.");
        if (store.Find(name) is not { } note) return new MemoryForgetting(null, $"There is no note called '{name}'. /memory lists them.");
        var scope = note.Scope == MemoryScope.System ? "system" : "project";
        return new MemoryForgetting(note,
            $"Forget {scope} note '{note.Name}'{(note.Description.Length > 0 ? $" — {note.Description}" : "")}? The file is deleted and cannot be brought back.");
    }

    /// <summary>
    /// Deletes a note the user confirmed. The model is told at the front of the next message — the index it has stays
    /// as it was, for the cache's sake.
    /// </summary>
    public CommandStatus ForgetMemory(string name)
    {
        if (Memory?.Delete(name.Trim()) == true)
            Say(new Message($"Forgot '{name.Trim()}'. The model is told with your next message.", Severity.Info));
        else
            Say(new Message($"There is no note called '{name.Trim()}'.", Severity.Warning));
        return CommandStatus.Reported;
    }
}

/// <summary>What forgetting a note would delete, and the question to ask first — or why it cannot be forgotten.</summary>
/// <param name="Note">The note, or null when there is nothing to forget.</param>
/// <param name="Text">The question to confirm, or, without a note, why not.</param>
public sealed record MemoryForgetting(MemoryNote? Note, string Text);

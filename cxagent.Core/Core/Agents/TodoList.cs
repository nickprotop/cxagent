using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CxAgent.Core.Agents;

/// <summary>What has happened to one item.</summary>
public enum TodoStatus
{
    Pending,
    InProgress,
    Completed,
    Cancelled,
}

/// <summary>One step in the model's own plan.</summary>
public sealed record TodoItem(string Text, TodoStatus Status)
{
    public bool IsOpen => Status is TodoStatus.Pending or TodoStatus.InProgress;
}

/// <summary>
/// The model's plan for the work in front of it — the list it writes, rewrites, and ticks off.
///
/// <para>WHY AN AGENT HOLDS ONE AT ALL. A long task drifts: after fifteen turns of reading files and
/// running commands, the third of five things the user asked for is somewhere far up a context that
/// may since have been compacted. A list the model maintains itself is the cheapest known fix, and
/// both reference implementations ship one.</para>
///
/// <para>STATE ON THE AGENT, NOT A TOOL RESULT — and this is the whole design decision. A tool result
/// is an ordinary message: compaction removes it like any other, which would delete the model's plan
/// at exactly the moment the conversation got long enough to need one. Held here and rendered into
/// the system prompt, it survives compaction the way the briefing does, because it is not history —
/// it is current intent.</para>
///
/// <para>REPLACED WHOLE, never patched, and the reason is worth stating: a partial update needs
/// stable ids, ids need the model to track them across turns, and a model that mis-numbers an id
/// silently marks the wrong item done.
/// Sending the list back entire is more tokens and no ambiguity.</para>
/// </summary>
public sealed class TodoList
{
    private readonly List<TodoItem> _items = [];

    public IReadOnlyList<TodoItem> Items => _items;

    /// <summary>True when there is nothing to render — the common case, and it must cost nothing.</summary>
    public bool IsEmpty => _items.Count == 0;

    public int OpenCount => _items.Count(i => i.IsOpen);

    /// <summary>
    /// Replaces the list with what the model just sent.
    /// </summary>
    public void Replace(IEnumerable<TodoItem> items)
    {
        _items.Clear();
        _items.AddRange(items);
    }

    /// <summary>
    /// The list as the model should see it at the top of its next turn.
    ///
    /// <para>MARKED THE WAY A PERSON WOULD MARK IT. <c>[x]</c> and <c>[ ]</c> are in every model's
    /// training data a thousand times over; a bespoke notation would be one more thing to explain in
    /// a prompt that is already long.</para>
    ///
    /// <para>COMPLETED ITEMS STAY VISIBLE. The obvious economy is to render only what is open, and it
    /// is wrong: a model that cannot see what it has already done will redo it, and "I already
    /// handled that" is not something a fresh context can know.</para>
    /// </summary>
    /// <param name="toolOffered">
    /// Whether <c>todowrite</c> is actually offered this turn.
    ///
    /// <para>THE LIST OUTLIVES THE TOOL. Items live in the agent's state, so a selection applied
    /// later leaves a non-empty list rendered beside an instruction to update it with something the
    /// model no longer has. The list is still worth showing — it is what the agent was doing — but
    /// the sentence naming the tool is not.</para>
    ///
    /// <para>A PARAMETER RATHER THAN A LOOKUP, so this stays a pure function of the list plus one
    /// fact. Reaching for a static here would make a renderer depend on whichever agent happened to
    /// run last.</para>
    /// </param>
    public string Render(bool toolOffered = true)
    {
        if (_items.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("# Your task list");
        sb.AppendLine();
        sb.AppendLine(toolOffered
            ? "You wrote this. Keep it current with todowrite as you work — mark an item "
              + "in_progress before starting it and completed once it is actually done."
            : "You wrote this earlier. You can no longer update it — the tool for that is not "
              + "available in this session — so treat it as a record of where you had got to.");
        sb.AppendLine();

        foreach (var item in _items)
            sb.AppendLine($"{Marker(item.Status)} {item.Text}");

        return sb.ToString();
    }

    private static string Marker(TodoStatus status) => status switch
    {
        TodoStatus.Completed => "- [x]",
        TodoStatus.Cancelled => "- [~]",
        TodoStatus.InProgress => "- [>]",
        _ => "- [ ]",
    };

    /// <summary>
    /// Reads the list out of a model's tool arguments.
    ///
    /// <para>TOLERANT ON THE WAY IN, because this is a model's JSON and the alternative to tolerance
    /// is a failed turn over a spelling. An unknown status becomes <c>pending</c> rather than an
    /// error: the model's INTENT was to record the item, and refusing the whole list because one
    /// word was "doing" instead of "in_progress" loses the plan to save a nicety.</para>
    /// </summary>
    public static IReadOnlyList<TodoItem> Parse(JsonElement todos)
    {
        if (todos.ValueKind != JsonValueKind.Array) return [];

        var items = new List<TodoItem>();
        foreach (var element in todos.EnumerateArray())
        {
            // A BARE STRING IS A PENDING ITEM. Models send ["do this", "then that"] often enough
            // that rejecting it would be pedantry — the meaning is unambiguous.
            if (element.ValueKind == JsonValueKind.String)
            {
                var bare = element.GetString();
                if (!string.IsNullOrWhiteSpace(bare))
                    items.Add(new TodoItem(bare!.Trim(), TodoStatus.Pending));
                continue;
            }

            if (element.ValueKind != JsonValueKind.Object) continue;

            var text = ReadString(element, "text") ?? ReadString(element, "content")
                    ?? ReadString(element, "task") ?? ReadString(element, "description");
            if (string.IsNullOrWhiteSpace(text)) continue;

            items.Add(new TodoItem(text!.Trim(), ParseStatus(ReadString(element, "status"))));
        }

        return items;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static TodoStatus ParseStatus(string? status) =>
        (status ?? "").Replace("_", "").Replace("-", "").Replace(" ", "").ToLowerInvariant() switch
        {
            "completed" or "complete" or "done" or "finished" => TodoStatus.Completed,
            "inprogress" or "active" or "doing" or "started" or "working" => TodoStatus.InProgress,
            "cancelled" or "canceled" or "skipped" or "abandoned" => TodoStatus.Cancelled,
            _ => TodoStatus.Pending,
        };

    /// <summary>
    /// What the tool hands back to the model after a write.
    ///
    /// <para>THE WHOLE LIST, not "ok". The result is what the model reads to confirm the write landed
    /// as it meant it — and it is the one place a mis-parsed status becomes visible to the thing that
    /// can correct it.</para>
    /// </summary>
    public string Describe()
    {
        if (_items.Count == 0) return "Plan cleared.";

        var sb = new StringBuilder();
        var done = _items.Count(i => i.Status == TodoStatus.Completed);
        var cancelled = _items.Count(i => i.Status == TodoStatus.Cancelled);

        // THE TALLY FIRST, so the row's body opens with the answer to "how far along is this" — the
        // question a reader has before they read any individual item.
        var tally = $"{done} of {_items.Count} done";
        if (cancelled > 0) tally += $", {cancelled} dropped";
        sb.AppendLine(done == _items.Count ? $"All {_items.Count} done." : tally);
        sb.AppendLine();

        // GROUPED, because a flat list of twelve buries the one line that matters. What is being
        // worked on now leads; what is left follows; what is finished goes last, where it can be
        // checked without being in the way.
        Group(sb, "Now", TodoStatus.InProgress);
        Group(sb, "Next", TodoStatus.Pending);
        Group(sb, "Done", TodoStatus.Completed);
        Group(sb, "Dropped", TodoStatus.Cancelled);

        return sb.ToString().TrimEnd();
    }

    private void Group(StringBuilder sb, string heading, TodoStatus status)
    {
        var items = _items.Where(i => i.Status == status).ToList();
        if (items.Count == 0) return;

        // STRUCK THROUGH ONCE IT IS SETTLED. The transcript renders this body as markdown, so `~~`
        // becomes real strikethrough — and a finished item that LOOKS finished is readable at a
        // glance, where a heading alone makes the reader hold "which group am I in" while scanning.
        // Cancelled too: it is equally not-to-be-done, and the heading distinguishes why.
        var strike = status is TodoStatus.Completed or TodoStatus.Cancelled;

        sb.AppendLine($"{heading}");
        foreach (var item in items)
            sb.AppendLine(strike
                ? $"  {Marker(status)} ~~{item.Text}~~"
                : $"  {Marker(status)} {item.Text}");
        sb.AppendLine();
    }

    [JsonIgnore]
    internal IReadOnlyList<TodoItem> Snapshot => [.. _items];
}

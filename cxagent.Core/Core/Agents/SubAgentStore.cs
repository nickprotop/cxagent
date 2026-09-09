using System.Collections.Concurrent;
using System.Text;

namespace CxAgent.Core.Agents;

/// <summary>A kept sub-agent, addressable by the name the model was given at spawn.</summary>
public sealed record StoredAgent(string Name, string TypeName, string? Description,
    DateTimeOffset At, SubAgent Agent);

/// <summary>
/// The sub-agents one session has spawned, kept so they can be asked more.
///
/// <para>THE CONTEXT IS THE AGENT, OPERATIONALLY, WHICH IS WHY KEEPING IT IS THE WHOLE FEATURE.
/// Everything else a child has — the executor registry, the provider, the type config — is shared or
/// reconstructible; the conversation is not. Re-running the original parameters would rebuild an
/// agent that must REDO the work to reach where this one already is, so asking the kept one skips
/// the file reads, the tool outputs and the dead ends it already ruled out.</para>
///
/// <para>NO BOUND, AND THE ARITHMETIC IS THE ARGUMENT. A context is a list of strings already in
/// memory; even a million-token conversation is roughly 4 MB. A resident context costs NOTHING PER
/// TURN — the parent's window is untouched, which is the entire point of a sub-agent — so there is
/// no per-turn cost to degrade and eviction would be machinery guarding an idle allocation.</para>
///
/// <para>ADDRESSED BY NAME RATHER THAN A NUMBER, which is where this diverges from a trigger id
/// deliberately. That is a small integer because a HUMAN retypes it from a listing; an agent handle
/// is typed only by the MODEL, whose failure mode is not mistyping but MISREMEMBERING. A name
/// carries its own meaning and is wrong VISIBLY when it is wrong.</para>
///
/// <para>BUSY IS HELD HERE AND NEVER PERSISTED. A crash mid-turn would otherwise leave a flag on
/// disk and the agent permanently unreachable, with nothing able to clear it.</para>
/// </summary>
public sealed class SubAgentStore
{
    private readonly ConcurrentDictionary<string, StoredAgent> _byName = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _busy = new(StringComparer.Ordinal);
    private readonly object _naming = new();

    /// <summary>Keeps this child under a fresh name and answers what it was called.</summary>
    public string Keep(SubAgent agent, string? description)
    {
        // NAMED UNDER A LOCK because two spawns racing would otherwise both read the same taken-set
        // and mint the same name, silently replacing one child with the other. Spawns ARE concurrent
        // — SubAgentSpawner has a concurrency slot precisely because several run at once.
        lock (_naming)
        {
            var name = Slug(description, _byName.Keys.ToArray());
            _byName[name] = new StoredAgent(name, agent.TypeName, description,
                DateTimeOffset.UtcNow, agent);
            return name;
        }
    }

    /// <summary>This session's agent of that name, or null when nothing holds it.</summary>
    public StoredAgent? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Every kept agent, oldest first — the order they were spawned in.</summary>
    public IReadOnlyList<StoredAgent> All() => _byName.Values.OrderBy(a => a.At).ToList();

    public bool IsBusy(string name) => _busy.ContainsKey(name);

    /// <summary>
    /// Claims this agent for one send, or answers false when another already has it.
    ///
    /// <para>REFUSED RATHER THAN QUEUED. <c>Agent</c> relies on "ONE TURN AT A TIME is enforced by
    /// <c>Session.Submit</c>", and a stored sub-agent sits behind no such gate — two sends would
    /// corrupt its context exactly as that comment warns. Queueing would make a tool call block on
    /// unrelated work with no way to report why; a refusal naming the state is something the model
    /// can act on immediately.</para>
    /// </summary>
    public bool TryBeginSend(string name) => _busy.TryAdd(name, 0);

    public void EndSend(string name) => _busy.TryRemove(name, out _);

    /// <summary>
    /// A handle from a description: lower-cased, non-alphanumerics folded to single hyphens.
    ///
    /// <para>SUFFIXED ON COLLISION RATHER THAN REPLACING, because two agents genuinely may be
    /// described the same way and the second must not silently take the first one's name — the
    /// model would then send to one believing it had reached the other.</para>
    ///
    /// <para>CAPPED, because a description is a sentence and a handle is retyped by a model into
    /// every later call. Forty characters keeps it meaningful without making it a paragraph.</para>
    /// </summary>
    public static string Slug(string? description, IReadOnlyCollection<string> taken)
    {
        var sb = new StringBuilder();
        foreach (var ch in (description ?? "").ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length == 0) slug = "agent";
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');

        if (!taken.Contains(slug)) return slug;
        for (var n = 2; ; n++)
        {
            var candidate = $"{slug}-{n}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}

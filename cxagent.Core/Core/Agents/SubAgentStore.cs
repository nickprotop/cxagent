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

    /// <summary>
    /// Handles claimed by a spawn that has not finished starting.
    ///
    /// <para>SEPARATE FROM <c>_byName</c> because a reserved name has no agent behind it yet: putting
    /// a placeholder in the store would make <c>Find</c> answer with something nobody can send to,
    /// and <c>All</c> list a child that does not exist.</para>
    /// </summary>
    private readonly HashSet<string> _reserved = new(StringComparer.Ordinal);

    /// <summary>Which reservation belongs to which tool call, so the spawner can claim it.</summary>
    private readonly Dictionary<string, string> _reservedFor = new(StringComparer.Ordinal);

    /// <summary>
    /// Claims the handle a child will answer to, before it exists.
    ///
    /// <para>NEEDED BECAUSE THE RECEIPT IS WRITTEN FIRST. A parent no longer waits for its children,
    /// so it names one in a tool result while the child is still starting — and that name has to be
    /// the one the child actually gets, or the model is told a handle that never resolves.</para>
    ///
    /// <para>THE RESERVATION IS WHAT MAKES THE SUFFIX RULE STILL WORK. Two children described the
    /// same way are spawned in one response; without holding the first name at dispatch, both would
    /// slug identically and the second would take the first one's handle.</para>
    /// </summary>
    /// <param name="callId">
    /// The tool call this reservation belongs to — how the spawner finds it again.
    ///
    /// <para>KEYED ON THE CALL RATHER THAN PASSED DOWN, because <c>TryInvokeAsync</c> already takes
    /// five parameters and a sixth would be the one nobody reads positionally (AV1561). Both sides
    /// hold the call, so the call is the key they already share.</para>
    /// </param>
    /// <param name="description">What the spawn called this child; the handle is slugged from it.</param>
    public string Reserve(string? description, string callId)
    {
        lock (_naming)
        {
            var name = Slug(description, [.. _byName.Keys, .. _reserved]);
            _reserved.Add(name);
            _reservedFor[callId] = name;
            return name;
        }
    }

    /// <summary>The handle reserved for this call, or null when nothing reserved one.</summary>
    public string? ReservationFor(string callId)
    {
        lock (_naming) return _reservedFor.GetValueOrDefault(callId);
    }

    /// <summary>
    /// Keeps this child, under <paramref name="reserved"/> when one was claimed for it.
    ///
    /// <para>NAMED UNDER A LOCK because two spawns racing would otherwise both read the same
    /// taken-set and mint the same name, silently replacing one child with the other. Spawns ARE
    /// concurrent — SubAgentSpawner has a concurrency slot precisely because several run at once.</para>
    /// </summary>
    public string Keep(SubAgent agent, string? description, string? reserved = null)
    {
        lock (_naming)
        {
            var name = reserved ?? Slug(description, [.. _byName.Keys, .. _reserved]);
            _reserved.Remove(name);
            foreach (var (k, v) in _reservedFor.Where(e => e.Value == name).ToList())
                _reservedFor.Remove(k);
            _byName[name] = new StoredAgent(name, agent.TypeName, description,
                DateTimeOffset.UtcNow, agent);
            return name;
        }
    }

    /// <summary>What this child was kept under, or null when nothing keeps it.</summary>
    public string? NameOf(SubAgent agent)
    {
        lock (_naming)
            return _byName.Values.FirstOrDefault(a => ReferenceEquals(a.Agent, agent))?.Name;
    }

    /// <summary>This session's agent of that name, or null when nothing holds it.</summary>
    public StoredAgent? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>
    /// The kept child with that AGENT id, or null when nothing holds it.
    ///
    /// <para>BY AGENT RATHER THAN BY HANDLE, for a caller that only ever sees the id: a child's tool
    /// calls carry its agent id and nothing else, so a front end tracking what a child is doing has
    /// no handle to look one up by.</para>
    ///
    /// <para>AND THIS IS WHY A CONSUMER NEED NOT KEEP ITS OWN REFERENCE. Every child kept here is
    /// pinned for the life of the session already, because agent_send resumes it on the context its
    /// spawn left behind; a second set of references elsewhere would pin the same Agents twice.</para>
    /// </summary>
    public StoredAgent? FindByAgentId(string agentId) =>
        _byName.Values.FirstOrDefault(a => a.Agent.Agent.Id == agentId);

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
    public bool TryBeginSend(string name)
    {
        if (!_busy.TryAdd(name, 0)) return false;

        // ANNOUNCED, BECAUSE NOTHING ELSE MARKS THE START OF A SEND. agent_send never becomes a job,
        // so no row is created for it and no tool report fires until the child's first call FINISHES
        // — which on a slow first call is long after the child started working. A front end showing
        // the child as settled has no earlier moment to start showing it working again.
        //
        // ALSO RAISED WHEN A SPAWN CLAIMS ITS OWN CHILD (SubAgentSpawner takes the same claim so a
        // send cannot corrupt a context the spawn is still appending to). A consumer cannot tell
        // the two apart from here and must not need to: during a spawn its row is already live, so
        // "the agent is working" is simply true both times.
        //
        // Find can miss only for a claim on a name nothing keeps, which is a claim no send can
        // follow — nothing to announce.
        if (Find(name) is { } began) SendBegan?.Invoke(began.Agent.Agent.Id);
        return true;
    }

    /// <summary>Releases this agent's claim, recording how the stretch ended.</summary>
    /// <param name="name">The handle the child is kept under — the same one the claim was taken with.</param>
    /// <param name="outcome">
    /// The envelope's own word for a spawn, null for a send.
    ///
    /// <para>CARRIED THROUGH THE RELEASE because the release is what announces the stop, and the
    /// listener that writes the account has no other way to learn a capped run was capped: the
    /// spawn method does not see its own envelope until after the claim is released, by which time
    /// the account is already written.</para>
    /// </param>
    public void EndSend(string name, string? outcome = null)
    {
        // ONLY WHEN A CLAIM WAS ACTUALLY RELEASED. EndSend runs in two finallys (the spawner's and
        // agent_send's), so an unconditional announcement would say "stopped" twice for one stop.
        if (!_busy.TryRemove(name, out _)) return;

        // ANNOUNCED, BECAUSE NOTHING ELSE MARKS THE END OF A SEND. agent_send never becomes a job,
        // so no tool report fires when it returns; and a child raises nothing when it goes idle,
        // because a turn ending and a goal ending look identical from outside. A front end that
        // started showing the child working has no other moment to stop.
        if (Find(name) is { } ended) SendEnded?.Invoke(ended.Agent.Agent.Id, outcome);
    }

    /// <summary>
    /// Raised with a child's AGENT id when a send-claim on it is taken, and when it is released.
    ///
    /// <para>THE AGENT ID RATHER THAN THE HANDLE, because a consumer that tracks children tracks
    /// them by the id their tool calls carry — a handle is this store's own naming and means nothing
    /// to a panel keyed on agents.</para>
    /// </summary>
    public event Action<string>? SendBegan;

    /// <inheritdoc cref="SendBegan"/>
    /// <remarks>
    /// CARRIES THE OUTCOME WORD ALONGSIDE THE ID, null unless the release named one. The release is
    /// the only announcement of a stop, so a listener writing the run's account has nowhere else to
    /// learn that a capped run was capped rather than completed.
    /// </remarks>
    public event Action<string, string?>? SendEnded;

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

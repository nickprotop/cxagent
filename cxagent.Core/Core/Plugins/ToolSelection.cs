using CxAgent.Core.Llm;

namespace CxAgent.Core.Plugins;

/// <summary>
/// Which tools an agent is offered, as WRITTEN — never as resolved.
///
/// <para>A selection is a list of terms: <c>inherited</c> to start from what this agent would
/// otherwise have, a bare <c>name</c> to include, <c>-name</c> to exclude, <c>+name</c> to include
/// even where an earlier level excluded it. A list WITHOUT <c>inherited</c> starts from nothing, so
/// it is an exact set.</para>
///
/// <para>THE TYPE HOLDS TERMS BECAUSE THE OFFERED SET MOVES. Skills appear when a catalog file is
/// added, injected tools vary per session, and MCP servers connect after config is read. An earlier
/// design had <c>Parse(terms, inherited) -&gt; names</c>, which reads like something you call once at
/// load — and a selection resolved before a server connected would withhold its tools FOREVER,
/// silently, from a config that never mentioned them. There is deliberately no method here that
/// takes names and returns names, so that mistake has no call site.</para>
///
/// <para>COMPOSE ONCE, APPLY ONCE. <see cref="Then"/> merges two levels' terms;
/// <see cref="Apply"/> resolves the result against the offered set exactly one time. Applying at
/// each level instead makes <c>+</c> inert — every Apply after the first sees a set the earlier one
/// already narrowed — which silently restores the narrowing-only rule this design replaced.</para>
///
/// <para>S0 IS THE ONLY FLOOR, and it is enforced by construction rather than by a check: Apply can
/// only return elements of what it was handed, and what it is handed is assembled downstream of
/// every structural gate — a child has no ask_user, a mode-single agent has no agent tool, an empty
/// catalog has no skill. No <c>+</c> at any depth reaches past it.</para>
///
/// <para>MCP TOOLS ARE NOT SELECTABLE and never reach this type: <c>enabled</c> per server is their
/// control. They are third-party code whose names are composed at runtime, and a selection that
/// never governs them cannot get their late arrival wrong.</para>
/// </summary>
/// <param name="Terms">The selection exactly as an embedder or a config file wrote it.</param>
public sealed record ToolSelection(IReadOnlyList<string> Terms)
{
    private const string Inherited = "inherited";

    /// <summary>
    /// Value equality over the terms, written by hand.
    ///
    /// <para>A positional record gives the LIST member REFERENCE equality, so two selections with
    /// identical terms would compare unequal. Session.Submit reports whether a queued call's
    /// selection differs from the running turn's; with reference equality that fires on every
    /// mid-turn correction from a caller that rebuilds its list — the exact noise the report exists
    /// not to make.</para>
    /// </summary>
    public bool Equals(ToolSelection? other) =>
        other is not null && Terms.SequenceEqual(other.Terms, StringComparer.Ordinal);

    public override int GetHashCode() =>
        Terms.Aggregate(17, (h, t) => (h * 31) + StringComparer.Ordinal.GetHashCode(t));

    /// <summary>
    /// Two levels as one selection: <paramref name="later"/>'s terms applied after
    /// <paramref name="earlier"/>'s.
    ///
    /// <para>NOT AN INTERSECTION. A later level may reopen what an earlier one closed, which is what
    /// <c>+</c> is for. Null on either side means "no opinion", so the other side stands; null on
    /// both means nobody expressed one.</para>
    /// </summary>
    public static ToolSelection? Then(ToolSelection? earlier, ToolSelection? later) =>
        earlier is null ? later
        : later is null ? earlier
        : new ToolSelection([.. earlier.Terms, .. later.Terms]);

    /// <summary>
    /// The offered set, narrowed by these terms.
    ///
    /// <para>Called PER REQUEST against the assembled tool list, never at load — see the type's own
    /// summary for why that distinction is the whole point.</para>
    /// </summary>
    /// <exception cref="FormatException">A term that is neither <c>inherited</c>, a bare name,
    /// <c>-name</c> nor <c>+name</c>. Config validates before this can happen; a code-level caller
    /// sees it on the first request, in their own C#.</exception>
    public IReadOnlyList<ToolDefinition> Apply(IReadOnlyList<ToolDefinition> offered)
    {
        // ORDER IS THE OFFERED SET'S, not the selection's. The assembly order is deliberate —
        // built-ins first, so a consumer tool cannot appear ahead of a name the model already
        // trusts — and a filter must not reorder what it filters.
        var chosen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in Terms)
        {
            var term = raw.Trim();
            if (term.Length == 0) continue;

            if (string.Equals(term, Inherited, StringComparison.Ordinal))
            {
                foreach (var tool in offered) chosen.Add(tool.Name);
                continue;
            }

            // A REMOVAL THAT MATCHES NOTHING IS HARMLESS, and that is the grammar's safety property:
            // under an exact-set style a typo silently withholds a real tool, while here a typo in a
            // removal removes nothing. The dangerous direction is the one the delta form makes safe.
            if (term[0] == '-') { chosen.Remove(term[1..]); continue; }

            // '+' AND A BARE NAME DIFFER ONLY IN INTENT. Both include; '+' says "even though an
            // earlier level removed it", which composition makes true by arriving later in Terms.
            // Keeping them distinct is for the READER of a config file, not for this loop.
            if (term[0] == '+') { chosen.Add(term[1..]); continue; }

            if (term[0] is '*' or '!' or '?' || term.Contains(' '))
                throw new FormatException(
                    $"tool selection term '{term}' is not understood. Use 'inherited', a tool name, "
                    + "'-name' to remove, or '+name' to add back.");

            chosen.Add(term);
        }

        // NOTHING IS FABRICATED. A name in the selection that matches no offered tool contributes
        // nothing — which is what makes S0 absolute without a check, and what makes an unknown name
        // (a typo, or an MCP tool that has not connected) safe rather than fatal.
        return [.. offered.Where(t => chosen.Contains(t.Name))];
    }
}

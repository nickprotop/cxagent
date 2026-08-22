using CxAgent.Core.Llm;

namespace CxAgent.Core.Jobs;

/// <summary>
/// Which tools an agent is offered, as WRITTEN — never as resolved.
///
/// <para>A selection is a list of terms: <c>inherited</c> to start from what this agent would
/// otherwise have, <c>all</c> to start over from everything it COULD have, a bare <c>name</c> to
/// include, <c>-name</c> to exclude, <c>+name</c> to include even where an earlier level excluded
/// it. A list with none of those starts from nothing, so it is an exact set.</para>
///
/// <para><c>inherited</c> AND <c>all</c> DIFFER ONLY BELOW THE TOP LEVEL, and that is the whole
/// point of having both: at S1 nothing has narrowed yet, so they mean the same set. Lower down,
/// <c>inherited</c> respects what the level above decided and <c>all</c> discards it. A session that
/// genuinely wants everything back from a narrowed manager writes <c>all</c> and means it.</para>
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
    private const string All = "all";

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
    /// Whether a term is grammar this type understands.
    ///
    /// <para>CONFIG VALIDATES WITHOUT RESOLVING. <see cref="Apply"/> throws on a malformed term, but
    /// Apply runs per REQUEST — long after config is read — so a bad term would open the session
    /// fine and then fail every turn. The loader calls this at load and warns instead, matching the
    /// warn-and-continue contract the rest of config already holds.</para>
    ///
    /// <para>NOT A NAME CHECK. An unknown tool NAME is legal and ignored: names arrive late (a
    /// skills catalog appears, an embedder injects per session) and a name matching nothing today
    /// may match tomorrow. Only the SHAPE is checked here.</para>
    /// </summary>
    public static bool IsWellFormed(string term)
    {
        var t = term.Trim();
        if (t.Length == 0) return false;
        if (t is Inherited or All) return true;

        var body = t[0] is '-' or '+' ? t[1..] : t;
        return body.Length > 0 && body.All(c => char.IsLetterOrDigit(c) || c is '_' or '.');
    }

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
    /// <summary>
    /// Whether a selection would offer one named tool — the question every gate outside the tool
    /// list actually asks.
    ///
    /// <para>ONE PLACE, BECAUSE TWO CALLERS ASK IT: SessionFactory's fan-out guard and the system
    /// prompt's spawn gate. Each can express the question by building a one-element list and calling
    /// Apply, which is the divergence this feature keeps finding in the skills catalog (Agent.cs
    /// `:696` and `:777`). A static on the type both already depend on is the one place neither can
    /// drift from.</para>
    ///
    /// <para>NULL MEANS NO OPINION, hence true: an agent with no selection is offered everything,
    /// and callers rely on that to leave the default untouched.</para>
    ///
    /// <para>A SYNTHETIC APPLY: at the gates that ask this, the agent's real offer does not exist
    /// yet — SessionFactory runs before the host, the prompt is built before the tool list. Sound
    /// because Apply only ever filters what it is handed: a name survives or it does not, and
    /// nothing else about the offered set changes that answer for a single name.</para>
    ///
    /// <para>THIS IS THE SELECTION ONLY. Whether the agent structurally HAS the tool is a separate
    /// question the caller combines with this one — S0 is not expressible as a term.</para>
    /// </summary>
    public static bool Offers(ToolSelection? selection, string name)
    {
        if (selection is null) return true;

        // A ONE-ELEMENT OFFER. Apply is the whole grammar — `all`, `inherited`, `-name`, `+name`,
        // and their ordering — so asking it about a single tool cannot fall out of step with what
        // the real offer does. Re-deriving the answer from Terms here is how these two drift.
        var one = new[]
        {
            new ToolDefinition(name, name,
                System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" })),
        };
        return selection.Apply(one).Count > 0;
    }

    public IReadOnlyList<ToolDefinition> Apply(IReadOnlyList<ToolDefinition> offered)
    {
        // ORDER IS THE OFFERED SET'S, not the selection's. The assembly order is deliberate —
        // built-ins first, so a consumer tool cannot appear ahead of a name the model already
        // trusts — and a filter must not reorder what it filters.
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        var seenInherited = false;

        foreach (var raw in Terms)
        {
            var term = raw.Trim();
            if (term.Length == 0) continue;

            // ALL IS A RESET, AND IT FIRES EVERY TIME. It means "everything this agent could have",
            // discarding whatever earlier levels removed — the one term that deliberately widens.
            // A session under a read-only manager can say ["all"] and get the full set back.
            //
            // STILL BOUNDED BY S0: `offered` is what this agent structurally has, so `all` on a
            // child never produces ask_user and `all` without a spawner never produces agent. It
            // resets the SELECTION, never the structure.
            //
            // THE DIFFERENCE FROM `inherited` IS WHOSE SET IT MEANS. `inherited` is "what the level
            // above left me"; `all` is "start over from everything". At the top level they are the
            // same thing, which is why `all` earns its keep only below it.
            if (string.Equals(term, All, StringComparison.Ordinal))
            {
                // NO Clear() HERE, DELIBERATELY. Adding every offered name IS the reset: whatever an
                // earlier level removed is back, and whatever it added was already in `offered`. A
                // Clear() first was written and then deleted — injecting its removal left the whole
                // suite green, which is the honest signal that it changed nothing.
                //
                // The one thing it cannot undo is a later term: `["all", "-run_shell"]` still ends
                // without run_shell, because terms apply in order.
                foreach (var tool in offered) chosen.Add(tool.Name);

                // AND IT SATISFIES `inherited` TOO, so a later `inherited` stays a no-op rather than
                // re-running as a second reset.
                seenInherited = true;
                continue;
            }

            // INHERITED MEANS "WHAT I INHERIT", AND ONLY THE FIRST ONE MEANS THE WHOLE SET. After
            // composition the terms of several levels sit in one list, so
            // ["inherited","-run_shell"] then ["inherited","-write_file"] would hit this twice —
            // and a second reset would re-add run_shell, silently undoing the level above. That is
            // the most ordinary config anyone will write: narrow globally, narrow further per
            // session.
            //
            // A LATER `inherited` IS A NO-OP, which is exactly what it means: this level starts from
            // what the previous one left, and saying so explicitly changes nothing.
            if (string.Equals(term, Inherited, StringComparison.Ordinal))
            {
                if (!seenInherited)
                {
                    foreach (var tool in offered) chosen.Add(tool.Name);
                    seenInherited = true;
                }
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

            if (!IsWellFormed(term))
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

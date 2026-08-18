using System.Linq;

namespace CxAgent.Core.Permissions;

/// <summary>
/// Collapses scopes that describe ONE folder onto that folder's identity as it reads today.
///
/// <para>WHY THERE IS ANYTHING TO COLLAPSE. Two eras of scope-writing left entries behind. Before
/// <see cref="FolderIdentity"/> existed, a scope was the bare path. After it landed but before
/// <c>FolderBirthTime</c> did, Linux <c>CreationTimeUtc</c> returned the CHANGE time, which moves
/// whenever anything in the directory is created, renamed or removed — so a long-lived project
/// accumulated a new scope every few days. This app's own store held four for one folder:
/// the bare path, plus #20260710, #20260812 and #20260813, of which only the first is the real
/// birth time. The other three can never match again, and the trust and grants recorded under them
/// are stranded.</para>
///
/// <para>WHAT IT DOES NOT DO: invent identity. A stale scope is folded in only when the folder is
/// STILL THERE and still reads as one identity. A path that no longer exists — 61 of this store's
/// rules pointed at deleted /tmp directories — is left untouched for the pruning question, which is
/// a different decision with a different risk.</para>
///
/// <para>RULES MOVE TOO, and that is the deliberate part. The identity system exists to say that a
/// recreated folder is a DIFFERENT folder, and re-pointing rules re-grants permissions across
/// scopes it separated. That is sound only because the stale scopes here are the same folder — a
/// clock artefact, not a recreation — which is precisely what the existence check below
/// establishes. Applied to a path whose folder was genuinely deleted and remade, this would be the
/// bug the identity system was built to prevent.</para>
/// </summary>
public static class PermissionScopeMigration
{
    /// <param name="Scopes">How many stale scopes were folded away.</param>
    /// <param name="Rules">How many rules were re-pointed onto the current identity.</param>
    /// <param name="Trusted">How many folders had trust carried forward.</param>
    public readonly record struct Result(int Scopes, int Rules, int Trusted)
    {
        public bool ChangedAnything => Scopes > 0 || Rules > 0 || Trusted > 0;
    }

    /// <summary>
    /// Rewrites <paramref name="store"/> in place. Safe to run repeatedly: once every scope for a
    /// path equals that path's current identity there is nothing left to fold, and the result is
    /// all zeroes.
    /// </summary>
    public static Result Run(PermissionRulesStore store)
    {
        var scopes = store.AllScopes();

        // BACKED UP BEFORE THE FIRST WRITE, not after. This rewrites decisions the user made, and
        // "the upgrade dropped my grants" has to be answerable with a file rather than an argument.
        // Best-effort: failing to copy must not block the fix, for the same reason the store's own
        // .bad backup is best-effort — the user's next grant matters more than the archive.
        var backedUp = false;
        void BackUpOnce()
        {
            if (backedUp) return;
            backedUp = true;
            try
            {
                if (File.Exists(store.FilePath))
                    File.Copy(store.FilePath, store.FilePath + ".premigration", overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Already there from an earlier run, or unwritable. Either way, proceed.
            }
        }

        // Group by the PATH, which is what "the same folder" means here — the suffix is the thing
        // under suspicion, so it cannot also be the grouping key.
        var byPath = scopes
            .GroupBy(FolderIdentity.PathOf)
            .Where(g => g.Count() > 1 || g.Any(s => s != FolderIdentity.ScopeFor(g.Key)));

        var foldedScopes = 0;
        var movedRules = 0;
        var carriedTrust = 0;

        foreach (var group in byPath)
        {
            var path = group.Key;

            // GONE MEANS LEAVE IT. Identity cannot be recomputed for a folder that is not there, and
            // guessing would be the exact mistake this whole mechanism exists to avoid.
            if (!Directory.Exists(path)) continue;

            var current = FolderIdentity.ScopeFor(path);

            // A path that yields no suffix (filesystem reports no birth time) collapses to the bare
            // path, which is already how it is stored — nothing to do, and folding would be a no-op
            // that still counted as a change.
            var stale = group.Where(s => s != current).ToList();
            if (stale.Count == 0) continue;

            // TRUST: any stale scope that was trusted carries forward. Trust is one bit and the
            // folder is the same folder, so the union is the honest answer — Untrusted stays
            // untrusted only if nothing said otherwise.
            BackUpOnce();   // first actual change to the file

            if (store.GetTrust(current) != TrustState.Trusted
                && stale.Any(s => store.GetTrustRaw(s) == TrustState.Trusted))
            {
                store.SetTrust(path, TrustState.Trusted);
                carriedTrust++;
            }

            // RULES MOVE ONLY FOR THE SCOPES THAT ARE PLAUSIBLY THIS SAME FOLDER. Trust above is
            // one bit about a folder the user still has; a rule is a specific grant, and moving one
            // across a genuine recreation is the failure FolderIdentity exists to prevent.
            var artefacts = stale.Where(s => LooksLikeAClockArtefact(s, path)).ToList();
            movedRules += store.RepointRules(artefacts, current);
            foldedScopes += store.ForgetScopes(stale);
        }

        return new Result(foldedScopes, movedRules, carriedTrust);
    }

    /// <summary>
    /// Whether a stale scope is most likely THIS folder recorded under a bad clock, rather than a
    /// different folder that once wore this path.
    ///
    /// <para>THE ASYMMETRY IS THE WHOLE SIGNAL. The ctime bug read the CHANGE time, which by
    /// definition is at or after the birth time and moves forward as the folder is used — so an
    /// artefact suffix is LATER than the real birth time, never earlier. A suffix that predates the
    /// folder's birth cannot describe this folder at all: something else stood at that path.</para>
    ///
    /// <para>A BARE PATH IS AN ARTEFACT, because it predates identity entirely — it says nothing
    /// about which generation it meant, and the folder in front of us is the only candidate.</para>
    ///
    /// <para>THE WINDOW IS DELIBERATELY GENEROUS AND STILL FINITE. Change times move whenever the
    /// agent writes, so a project worked on for weeks legitimately produced suffixes weeks after its
    /// birth; a year later is a different story and far more likely a reused path. Wrong in the
    /// permissive direction costs a re-grant of a rule the user already made once, in the same
    /// folder they still have. Wrong in the other costs one prompt.</para>
    /// </summary>
    private static bool LooksLikeAClockArtefact(string staleScope, string path)
    {
        var suffix = staleScope.Length > path.Length ? staleScope[(path.Length + 1)..] : null;
        if (string.IsNullOrEmpty(suffix)) return true;   // pre-identity bare path

        if (!DateTime.TryParseExact(suffix, "yyyyMMddTHHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var recorded))
            return false;   // unparseable: do not guess in the permissive direction

        var currentSuffix = FolderIdentity.ScopeFor(path);
        if (currentSuffix.Length <= path.Length) return false;   // folder reports no birth time now

        if (!DateTime.TryParseExact(currentSuffix[(path.Length + 1)..], "yyyyMMddTHHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var birth))
            return false;

        var drift = recorded - birth;
        return drift >= TimeSpan.Zero && drift < TimeSpan.FromDays(120);
    }
}

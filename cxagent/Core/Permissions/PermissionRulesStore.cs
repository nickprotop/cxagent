using System.Text.Json;
using System.Text.Json.Serialization;
using CxAgent.Core.Storage;
using System.Linq;

namespace CxAgent.Core.Permissions;

/// <summary>
/// Persisted "Always allow" rules, one JSON file (permissions.json) under AppPaths.ConfigDir.
/// Rules are scoped by the working-dir folder they were granted in — a grant made in one
/// project must never silently cover another. Grants can arrive from background threads
/// (a job finishing while the user is elsewhere), so all access is lock-protected, and saves
/// are atomic (write to a tmp file, then move over the real one) so a crash mid-write can never
/// leave permissions.json half-written.
///
/// A missing file is treated as an empty rule set, silently — there is nothing to preserve and
/// nothing to tell the user. An UNPARSEABLE file (present but the JSON — or one enum inside it —
/// is broken, e.g. a hand-edit typo) is ALSO treated as an empty rule set in memory, so the app
/// still starts, but that is a distinct case from "absent": <see cref="LoadError"/> records it, so
/// a caller with a transcript can tell the user every rule and all trust just silently vanished,
/// and the FIRST save after such a load backs the unreadable file up to permissions.json.bad
/// before writing anything new — so a user mid-hand-edit never simply loses that edit to the next
/// grant's overwrite.
/// </summary>
public class PermissionRulesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _lock = new();
    private readonly List<Rule> _rules;
    private readonly Dictionary<string, TrustState> _trust;

    // Scopes this instance has itself classified via SetTrust (not merely inherited from a
    // Load or a merge). Only these scopes are allowed to overwrite what is on disk when another
    // process's newer write is merged in — an instance that never called SetTrust for a folder
    // must never clobber that folder's on-disk trust value with its own stale in-memory copy.
    private readonly HashSet<string> _locallySetTrustScopes = new();

    // Set once, at construction, if the file existed but failed to parse. Drives both LoadError
    // (so a caller can tell the user) and the one-time .bad backup on the next Save.
    private bool _loadFailed;

    private sealed record Rule(string Scope, PermissionKind Kind, string Pattern);

    public PermissionRulesStore(AppPaths appPaths)
    {
        _path = Path.Combine(appPaths.ConfigDir, "permissions.json");
        var loaded = Load(_path);
        _rules = loaded.Rules;
        _trust = loaded.Trust;
        _loadFailed = loaded.Failed;
        LoadError = loaded.Failed
            ? "permissions.json could not be read and was treated as empty (every rule and all folder trust are gone until the file is fixed). The original file will be preserved as permissions.json.bad on the next change."
            : null;
    }

    /// <summary>Null after a normal load (file absent, or present and valid). Non-null after a
    /// load that found the file but could not parse it — Core has no UI to surface this itself, so
    /// a caller with a transcript (<see cref="CxAgent.UI.InteractivePermissionGate"/>) reads this
    /// once at startup and echoes it. Never cleared automatically: it describes what happened at
    /// construction, not live state.</summary>
    public string? LoadError { get; }

    /// <summary>Where this store persists (&lt;ConfigDir&gt;/permissions.json) — read-only, for
    /// display (the Settings page's Permissions tab shows this so a user who wants to revoke a
    /// rule by hand knows exactly which file to edit).</summary>
    public string FilePath => _path;

    private static (List<Rule> Rules, Dictionary<string, TrustState> Trust, bool Failed) Load(string path)
    {
        if (!File.Exists(path))
            return (new List<Rule>(), new Dictionary<string, TrustState>(), false);

        try
        {
            var json = File.ReadAllText(path);
            var stored = JsonSerializer.Deserialize<StoredFile>(json, JsonOptions);
            if (stored is null) return (new List<Rule>(), new Dictionary<string, TrustState>(), false);
            var rules = (stored.Rules ?? new List<StoredRule>())
                .Select(r => new Rule(r.Scope, r.Kind, r.Pattern))
                .ToList();
            var trust = new Dictionary<string, TrustState>(stored.Trust ?? new Dictionary<string, TrustState>());
            return (rules, trust, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or NotSupportedException)
        {
            // File present but unreadable or not valid JSON (including one typo'd enum value,
            // which JsonException covers): empty rule set, not a crash — but this IS distinct from
            // "no file", so the caller is told via the Failed flag.
            return (new List<Rule>(), new Dictionary<string, TrustState>(), true);
        }
    }

    /// <summary>True if a previously-granted rule covers this (scope, kind, subject). Shell rules
    /// match as a prefix (so a hand-edited "git status*" wildcard works, and a plain stored
    /// command "ls" matches only the exact string "ls", not "ls -la"); read/write entries match
    /// the resolved path exactly, or as a subtree when the pattern ends with '/' (the form the
    /// "Always" prompt itself writes for file grants — "Always allow writes under X/"); other
    /// kinds match exactly.</summary>
    public bool Matches(string scope, PermissionKind kind, string subject)
    {
        scope = FolderIdentity.ScopeFor(scope);
        lock (_lock)
        {
            foreach (var rule in _rules)
            {
                if (rule.Scope != scope || rule.Kind != kind) continue;
                if (MatchesPattern(rule.Pattern, subject)) return true;
            }
            return false;
        }
    }

    private static bool MatchesPattern(string pattern, string subject)
    {
        if (pattern.EndsWith('*'))
            return subject.StartsWith(pattern[..^1], StringComparison.Ordinal);

        // Directory-with-trailing-slash: the form the "Always" prompt writes for file grants
        // (spec: "read/write entries match the resolved path exactly, or as a subtree when the
        // entry ends with '/'"). The trailing '/' is already part of the pattern, so a subject
        // in a sibling directory that merely shares the prefix ("/proj/" vs "/projX/...") does
        // NOT match — no separator-injection needed here, unlike the boundary check, because
        // the separator is already baked into the stored pattern.
        if (pattern.EndsWith('/'))
            return subject == pattern || subject.StartsWith(pattern, StringComparison.Ordinal);

        return subject == pattern;
    }

    public void Add(string scope, PermissionKind kind, string rule)
    {
        scope = FolderIdentity.ScopeFor(scope);
        lock (_lock)
        {
            _rules.Add(new Rule(scope, kind, rule));
            Save();
        }
    }

    /// <summary>Trust-on-first-use state for a folder. Never asked = Unknown, which the policy
    /// treats as untrusted for the silent class — an unanswered question must never behave like
    /// a yes.</summary>
    public TrustState GetTrust(string scope)
    {
        scope = FolderIdentity.ScopeFor(scope);
        lock (_lock)
        {
            return _trust.TryGetValue(scope, out var state) ? state : TrustState.Unknown;
        }
    }

    /// <summary>Read-only snapshot for the Settings page: this scope's rules, and how many OTHER
    /// scopes hold rules (so the page can say they exist without listing them). Lock-protected
    /// copy — never a live view of the mutable lists. Grants can arrive from background threads
    /// (P12 wired the permission gate to persist on scheduler threads) while this page renders, so
    /// a lazy IEnumerable over _rules would be a collection-modified-during-enumeration crash in
    /// the render loop; this materialises both results before releasing the lock.</summary>
    public (IReadOnlyList<(PermissionKind Kind, string Pattern)> Rules, int OtherScopeCount) RulesFor(string scope)
    {
        lock (_lock)
        {
            var rules = _rules
                .Where(r => r.Scope == scope)
                .Select(r => (r.Kind, r.Pattern))
                .ToList();
            var otherScopes = _rules
                .Where(r => r.Scope != scope)
                .Select(r => r.Scope)
                .Distinct()
                .Count();
            return (rules, otherScopes);
        }
    }

    public void SetTrust(string scope, TrustState state)
    {
        scope = FolderIdentity.ScopeFor(scope);
        lock (_lock)
        {
            _trust[scope] = state;
            _locallySetTrustScopes.Add(scope);
            Save();
        }
    }

    // Must be called while holding _lock. Reloads whatever is on disk right now and merges it
    // with this instance's in-memory state before writing, so a second cxagent process (over the
    // same permissions.json, e.g. two windows in two projects) cannot silently erase the first's
    // grants with a whole-file last-writer-wins overwrite — see finding I2. Rules are additive
    // grants that are never removed programmatically in v1, so the merged rule set is simply the
    // union of on-disk and in-memory (deduplicated). Trust is one bit per scope: for a scope this
    // instance never classified via SetTrust, the on-disk value wins, so an instance that merely
    // loaded a value (or never touched a folder at all) can never revoke another instance's newer
    // trust decision; for a scope this instance DID call SetTrust on, its own value wins, because
    // that is a decision this instance genuinely made this session.
    private void Save()
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        // I3: the FIRST save after a load that couldn't parse the file backs up whatever is
        // currently on disk to permissions.json.bad BEFORE this method writes anything — otherwise
        // the very next grant (this call) would overwrite the user's unreadable hand-edit with a
        // fresh file built from an empty-because-it-failed-to-load in-memory state, and that edit
        // would be gone for good. Best-effort: a failure to preserve the bad file must not block
        // saving the grant the user just made (same "don't revoke what they just said yes to"
        // reasoning as the IOException guards around _store.Add/_store.SetTrust in the gate).
        if (_loadFailed)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var badPath = Path.Combine(dir, "permissions.json.bad");
                    File.Copy(_path, badPath, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort backup; the save below must still proceed.
            }
            _loadFailed = false;   // only the first save after a failed load backs up
        }

        var onDisk = Load(_path);

        foreach (var rule in onDisk.Rules)
        {
            if (!_rules.Contains(rule)) _rules.Add(rule);
        }

        foreach (var (scope, state) in onDisk.Trust)
        {
            if (!_locallySetTrustScopes.Contains(scope)) _trust[scope] = state;
        }

        var stored = new StoredFile(
            _rules.Select(r => new StoredRule(r.Scope, r.Kind, r.Pattern)).ToList(),
            new Dictionary<string, TrustState>(_trust));
        var json = JsonSerializer.Serialize(stored, JsonOptions);

        var tmp = Path.Combine(dir, $".permissions.json.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    private sealed record StoredRule(string Scope, PermissionKind Kind, string Pattern);

    private sealed record StoredFile(List<StoredRule>? Rules, Dictionary<string, TrustState>? Trust);
}

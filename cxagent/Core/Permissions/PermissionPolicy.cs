using System.Text;
using CxAgent.Core.Agent;
using CxAgent.Core.Models;

namespace CxAgent.Core.Permissions;

/// <summary>
/// The one place that decides (a) what a plugin invocation is asking permission for
/// (<see cref="RequestsFor"/>), and (b) whether that request can proceed without a prompt
/// (<see cref="IsSilentlyAllowed"/>). File operations under the working-dir root are silent;
/// everything else — including every shell command, regardless of what it touches — requires
/// either a prompt or a previously stored "Always" rule.
/// </summary>
public class PermissionPolicy
{
    private readonly string _root;
    private readonly PermissionRulesStore _rules;

    public PermissionPolicy(string workingDirRoot, PermissionRulesStore rules,
        EditMode edits = EditMode.AcceptEdits)
    {
        _root = workingDirRoot;
        _rules = rules;
        Edits = edits;
    }

    /// <summary>
    /// When a write happens without asking.
    ///
    /// <para>SETTABLE, because the mode is session state a user flips mid-session with Shift+Tab, and
    /// <c>InteractivePermissionGate</c> holds its policy in a readonly field — rebuilding the gate to
    /// change one enum would mean reconstructing six constructor arguments AND discarding any prompt
    /// already queued behind it, so a user answering a prompt would watch it vanish.</para>
    ///
    /// <para>A TURN READS IT WHEN IT ASKS, so a flip takes effect on the NEXT action rather than
    /// retroactively. That matches <see cref="WorkingMode"/>'s immutability reasoning: a mid-turn
    /// switch must not change the answer to a question already being asked.</para>
    /// </summary>
    public EditMode Edits { get; set; }

    // IN-CWD IS A SCOPE BOUNDARY, NOT A SAFETY ONE. The working directory is a git repo, so "inside
    // the boundary" includes .git/hooks/* — which executes as the user on the next git command — and
    // .git/config, which carries core.pager and core.fsmonitor. A user reading "accept edits" on the
    // composer pictures source files, not a hook that runs as them.
    //
    // DELIBERATELY SHORT: directories whose contents EXECUTE, not everything that might matter. An
    // agent legitimately editing a git hook or an editor task is rare enough that one prompt is the
    // right price, and it is the prompt a user would most want to see.
    //
    // One accidental mitigation exists and is NOT relied on here: `git` is absent from
    // ReadOnlyCommands' safe verbs, so the agent can write a hook silently but cannot silently
    // trigger it. The user runs `git commit` themselves constantly, which is the point of a hook.
    private static readonly string[] ExecutableConfigDirs = [".git", ".vscode", ".claude", ".idea"];

    /// <summary>The one mapping from plugin params to permission requests: shell → one Shell
    /// request; file → per-action read/write requests (copy/move produce both a read of the
    /// source and a write of the dest, checked independently); http → one Http request for the
    /// URL's origin.</summary>
    /// <param name="root">
    /// What a RELATIVE path is relative to — the agent's folder. Null means the process's, which is
    /// what every caller got before this parameter existed.
    ///
    /// <para>IT MUST MATCH THE PLUGIN'S BASE. The file plugin resolves `src/foo.cs` against the
    /// agent's directory; a gate that resolved the same string against a different one would decide
    /// about a file nobody is going to touch — allowing a write to a checkout the user never
    /// approved, with every layer behaving correctly on the way.</para>
    /// </param>
    public static IReadOnlyList<PermissionRequest> RequestsFor(string pluginType,
        JobParameters parameters, string? root = null)
    {
        switch (pluginType)
        {
            case "shell":
                return new[] { ShellRequest(parameters) };

            case "file":
                return FileRequests(parameters, root);

            case "http":
                return new[] { HttpRequest(parameters) };

            default:
                return Array.Empty<PermissionRequest>();
        }
    }

    private static PermissionRequest ShellRequest(JobParameters parameters)
    {
        var command = parameters.Get<string>("command");
        var workingDir = parameters.Get<string?>("working_dir", null);
        var env = parameters.Get<Dictionary<string, string>?>("env", null);

        // A shell job carrying a custom env is a different program than the plain command
        // string, so it can never be truthfully generalised into a stored rule for that
        // string — AlwaysRule is null, and Display must show the env so the user can SEE
        // what's different.
        var hasEnv = env is { Count: > 0 };

        var display = new StringBuilder(command);
        if (!string.IsNullOrEmpty(workingDir))
            display.Append(" (in ").Append(workingDir).Append(')');
        if (hasEnv)
        {
            display.Append(" [env: ");
            display.Append(string.Join(", ", env!.Select(kv => $"{kv.Key}={kv.Value}")));
            display.Append(']');
        }

        // THE COMMAND'S NAME, NOT THE WHOLE COMMAND. This was the literal string, which meant
        // `find Services -type f` and `find . -type f` were unrelated grants — 111 rules
        // accumulated in one user's store and essentially none could ever match again.
        //
        // CommandArity decides how many words NAME a command, so `git status` grants `git status *`
        // and never `git push`. See it for why granting the first word alone is the dangerous
        // version of this idea.
        var alwaysRule = hasEnv ? null : CommandArity.RuleFor(command);

        // SUBJECT IS THE BARE COMMAND. Display gains " (in /path)" for the reader, and anything that
        // PARSES the command — the read-only check, the rule match — would otherwise see a command
        // called "ls (in".
        return new PermissionRequest(PermissionKind.Shell, display.ToString(), alwaysRule,
            Subject: command);
    }

    private static List<PermissionRequest> FileRequests(JobParameters parameters, string? root)
    {
        var action = parameters.Get<string>("action");

        // TOLERANT OF AN ABSENT PATH, because `list` and `search` no longer require one — the glob
        // and grep tools take a pattern and default the directory. Neither action appears in the
        // switch below (they are read-only and raise no request at all), but this line ran BEFORE
        // the switch, and the one-argument Get is Values[key]: it threw "The given key 'path' was
        // not present in the dictionary" out of the permission gate, before the plugin that knows
        // the default was ever reached. Fixing the plugin alone left this in place, and a live
        // session failed 18 times on `grep {"pattern": ...}` — the exact call the tool advertises.
        var path = parameters.Get<string?>("path", null);
        var dest = parameters.Get<string?>("dest", null);

        var requests = new List<PermissionRequest>();
        var target = string.Empty;

        // AN ACTION THAT NEEDS A PATH AND HAS NONE STILL ASKS. The plugin's own validation rejects
        // such a call, so this is unreachable today — but this is the gate, and the failure mode of
        // silently raising NO request for a write is that the write goes through unasked if that
        // validation ever moves. Fail toward asking, the same direction TryResolve already fails.
        if (action is "read" or "write" or "append" or "delete" or "copy" or "move")
        {
            if (path is null)
                return [new PermissionRequest(
                    action is "read" or "copy" or "move"
                        ? PermissionKind.FileRead : PermissionKind.FileWrite,
                    $"{action} (no path given)", null)];
            target = path;   // non-null from here, and the compiler can see it
        }

        switch (action)
        {
            case "read":
                requests.Add(FileRequest(PermissionKind.FileRead, target, root));
                break;
            case "write":
            case "append":
                requests.Add(FileRequest(PermissionKind.FileWrite, target, root));
                break;
            case "delete":
                requests.Add(FileRequest(PermissionKind.FileWrite, target, root));
                break;
            case "copy":
            case "move":
                requests.Add(FileRequest(PermissionKind.FileRead, target, root));
                if (dest is not null)
                    requests.Add(FileRequest(PermissionKind.FileWrite, dest, root));
                break;
        }
        return requests;
    }

    /// <summary>Builds a file request whose Display is the RESOLVED path (what the user is
    /// actually being asked about, not the model's possibly-relative-or-symlinked spelling) and
    /// whose AlwaysRule is the resolved path's CONTAINING DIRECTORY plus a trailing separator —
    /// the form the "Always" prompt is documented to write (spec §Storage: "Always allow writes
    /// under /tmp/scratch/"). Granting the exact file would re-prompt on every sibling; this is
    /// the affordance the spec promises. If the path fails to resolve, AlwaysRule is null — fail
    /// toward asking, exactly like the boundary check (<see cref="TryResolve"/>) already does,
    /// never toward a rule built from an unresolved string (that gap was C2).</summary>
    private static PermissionRequest FileRequest(PermissionKind kind, string path, string? root)
    {
        var resolved = TryResolve(path, root);
        if (resolved is null)
            return new PermissionRequest(kind, path, null);

        var dir = Path.GetDirectoryName(resolved);
        var alwaysRule = string.IsNullOrEmpty(dir) ? null : dir + Path.DirectorySeparatorChar;
        return new PermissionRequest(kind, resolved, alwaysRule);
    }

    private static PermissionRequest HttpRequest(JobParameters parameters)
    {
        var url = parameters.Get<string>("url");
        var origin = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : url;
        return new PermissionRequest(PermissionKind.Http, origin, origin);
    }

    /// <summary>True when this request needs no prompt: an in-boundary file read/write, or a
    /// request matched by a previously stored "Always" rule. Shell has no in-boundary free
    /// pass — a command string says nothing reliable about what it touches — so it is silent
    /// only via a matching rule.</summary>
    public bool IsSilentlyAllowed(PermissionRequest request)
    {
        // THE MODE TEST SITS INSIDE THE TRUST GUARD, AND-ed with it, so an untrusted folder is
        // STRUCTURALLY incapable of a silent write: modes narrow, trust bounds. Placing it here makes
        // the floor a property of the code's shape rather than a rule someone has to remember.
        //
        // READS KEEP THEIR FREE PASS UNDER AlwaysAsk. The axis is named EDITS, and making the agent
        // prompt to read a file would break every ordinary investigation for no safety gain — a read
        // inside a trusted folder is what the boundary was drawn to permit.
        // ONE DEFINITION OF THE FLOOR, in AllowsSilentWrites, because Auto has to consult it
        // separately — the classifier runs after this method has returned false, and a second copy of
        // "trusted and in-boundary" would be the copy that drifts.
        //
        // AUTO IS NOT SILENT HERE. It reaches the classifier instead, which is the only mode whose
        // answer this method cannot give.
        if (AllowsSilentWrites(request)
            && (request.Kind == PermissionKind.FileRead
                || Edits == EditMode.AcceptEdits))
            return true;

        // A COMMAND THAT CAN ONLY LOOK, in a folder the user has trusted. The comment above used to
        // say shell has no in-boundary free pass because "a command string says nothing reliable
        // about what it touches" — true of commands in general, and false of the short list in
        // ReadOnlyCommands, which is exactly the set that cannot write however it is invoked.
        //
        // MEASURED, not assumed: one agentic drive made thirteen shell calls in a single turn, and
        // it only finished because approvals were automated. `ls`, `cat` and `grep` are the file
        // READ that already passes silently here, spelled as a command — charging for one and not
        // the other is a distinction the user cannot act on, and it is why run_shell kept beating
        // our own read-only tools: the model reaches for the verb it knows and the gate bills the
        // user for it.
        //
        // TRUST IS STILL REQUIRED. An untrusted folder prompts for everything, unchanged.
        //
        // ON Display, NOT AlwaysRule. AlwaysRule is now a PATTERN — `find*` — since rules started
        // generalising over arguments, so asking whether it is read-only would be asking about a
        // glob rather than about the command the model actually sent.
        if (request.Kind == PermissionKind.Shell
            && _rules.GetTrust(_root) == TrustState.Trusted
            && ReadOnlyCommands.IsReadOnly(request.What, out var changesTo)
            && (changesTo is null || IsInsideBoundary(changesTo)))
            return true;

        if (request.AlwaysRule is null) return false;

        var subject = RuleSubject(request);
        if (subject is null) return false;   // resolution failed: fail toward asking, never toward silence.

        return _rules.Matches(_root, request.Kind, subject);
    }

    // The subject a stored rule is compared against. Shell rules are compared against the
    // command text (AlwaysRule) as-is — there is no filesystem path to resolve. File rules are
    // compared against the resolved FILE path — Display, which FileRequest already populated
    // with TryResolve's output, never AlwaysRule (that's the file's own containing directory,
    // not the file). It is re-resolved here rather than trusted as pre-resolved so a subject
    // built by hand (or by any future caller that doesn't go through FileRequest) still gets
    // the same defence: "../" or a symlink lexically inside a granted directory must never
    // match without actually resolving to somewhere inside it.
    /// <summary>
    /// The subject a stored rule is matched against. PATH-BEARING kinds are RESOLVED first —
    /// finding C2 was that this compared the raw string, so `granted/../victim.txt` matched a grant
    /// on `granted/` and wrote outside it with no prompt at all.
    ///
    /// The switch lists every kind ON PURPOSE, with no `_ =>` fallback. Design weakness D1: a rule
    /// is specified as a text pattern rather than a resolved-path predicate, so if a future
    /// <see cref="PermissionKind"/> carries a path and is quietly routed down the raw-string branch,
    /// C2 comes back by omission and nothing fails. Adding a kind without touching this switch
    /// raises CS8509 naming the new kind (verified — this project does NOT treat warnings as errors,
    /// so it is a loud prompt to decide, not a hard stop). A `_ =>` fallback would silence it and
    /// pick the unsafe branch by default, which is why there isn't one.
    /// </summary>
    private string? RuleSubject(PermissionRequest request) => request.Kind switch
    {
        // Path-bearing: resolve before matching, and fail toward asking if it cannot be resolved.
        PermissionKind.FileRead or PermissionKind.FileWrite => TryResolve(request.Display, _root),

        // SHELL MATCHES THE COMMAND, NOT THE RULE. This read AlwaysRule, which was the same string
        // as the command back when a rule WAS the whole command. It no longer is — a rule is now
        // `git status *` — so comparing AlwaysRule against a stored pattern would compare a pattern
        // to a pattern, and `git status *` would only ever match a command literally called
        // "git status *". The subject is what the model actually asked to run.
        //
        // Display, not the raw parameter, because ShellRequest already assembled it and it carries
        // the working directory when one was given. The stored `... *` pattern is a prefix match, so
        // the trailing " (in /path)" costs nothing.
        PermissionKind.Shell => request.What,

        // Http is unchanged: its rule IS its subject, the request origin.
        PermissionKind.Http => request.AlwaysRule,

        // Nor is an MCP call. Its subject is the SERVER AND TOOL ("mcp:files_read"), never the
        // arguments: those follow a schema written by a third party, and we cannot tell which of them
        // name a path, a URL or a credential. Generalising over something we cannot read would be
        // inventing a guarantee — so a rule covers "this tool on this server" and nothing narrower.
        PermissionKind.Mcp => request.AlwaysRule,
    };

    /// <summary>Public wrapper over the same boundary check <see cref="IsSilentlyAllowed"/> uses
    /// internally (Task 4): whether to offer the "Trust this folder" button on a file prompt
    /// depends on the SAME geometry, not a re-derived copy of it — an in-boundary path in an
    /// untrusted folder is exactly the case Task 2.5 describes as "the boundary is still computed
    /// and still shown ... that phrasing is what makes the button intelligible."</summary>
    public bool IsInBoundary(string path) => IsInsideBoundary(path);

    private bool IsInsideBoundary(string path)
    {
        var resolved = TryResolve(path, _root);
        if (resolved is null) return false;   // resolution failed: fail toward asking, never toward silence.

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root));
        return resolved == normalizedRoot ||
               resolved.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves a path to its real, symlink-free absolute form, so a link inside the boundary
    /// that points outside counts as OUTSIDE (GetFullPath alone is lexical only — it collapses
    /// ".." but has no idea a directory is actually a door to somewhere else).
    ///
    /// RELATIVE TO THE SESSION'S ROOT, not the process's. The model is told "relative paths resolve
    /// from the working directory", and the file plugin now makes that true — so a gate resolving
    /// the SAME string against a different base would check a path nobody is going to touch. That is
    /// not a near-miss: it is a check that passes on one file while another is written.
    ///
    /// Strategy: GetFullPath first (collapses "..", makes it absolute), then resolve EVERY component
    /// in turn, following a link wherever one sits. If resolution throws for any reason (permissions,
    /// a dangling link, a race), we return null and the caller treats that as outside the boundary —
    /// failing toward asking, never toward silent allow.
    /// </summary>
    /// <summary>
    /// Whether a silent write here is permissible AT ALL, ignoring which edit mode is set.
    ///
    /// <para>THE FLOOR, FACTORED OUT SO <c>Auto</c> CANNOT STEP OVER IT. Every other mode meets the
    /// floor inside <see cref="IsSilentlyAllowed"/>; the classifier runs after that method has already
    /// said no, so without this it would have been the one mode able to widen past a trust decision —
    /// exactly the power the trust-bounds rule exists to deny. A classifier is still a mode.</para>
    ///
    /// <para>Trusted folder, inside the boundary, and not an executable-config directory. It says
    /// nothing about whether the write SHOULD happen — that is the classifier's question.</para>
    /// </summary>
    public bool AllowsSilentWrites(PermissionRequest request) =>
        request.Kind is PermissionKind.FileRead or PermissionKind.FileWrite
        && _rules.GetTrust(_root) == TrustState.Trusted
        && IsInsideBoundary(request.Display)
        && !IsExecutableConfig(request);

    /// <summary>
    /// True when this WRITE lands in a directory whose contents execute — see
    /// <see cref="ExecutableConfigDirs"/> for why in-cwd is scope rather than safety.
    /// </summary>
    /// <remarks>
    /// WRITES ONLY. Reading <c>.git/config</c> to answer a question about the repo is ordinary, and
    /// making reads prompt here would be friction with no safety behind it.
    /// </remarks>
    private bool IsExecutableConfig(PermissionRequest request)
    {
        if (request.Kind != PermissionKind.FileWrite) return false;

        var resolved = TryResolve(request.Display, _root);
        if (resolved is null) return true;   // unresolvable: fail toward asking, as everywhere here

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root));
        if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return false;   // outside the boundary entirely; the caller already refuses it

        var first = resolved[(root.Length + 1)..].Split(Path.DirectorySeparatorChar)[0];

        return ExecutableConfigDirs.Contains(first, StringComparer.OrdinalIgnoreCase);
    }

    private static string? TryResolve(string path, string? root)
    {
        try
        {
            var full = root is { Length: > 0 } ? Path.GetFullPath(path, root) : Path.GetFullPath(path);

            // COMPONENT BY COMPONENT, NOT JUST THE DEEPEST EXISTING ENTRY. This used to walk up to the
            // first entry that exists and resolve THAT, which silently skipped a symlinked DIRECTORY
            // whenever anything below it existed: the entry we landed on was a plain file, and
            // ResolveLinkTarget on a plain file is null — the link is the PARENT, and nothing looked
            // at it.
            //
            // MEASURED against a real fixture with `root/link -> outside`, on a trusted folder:
            //     link/x.txt       (existing)          silently allowed   WRONG
            //     link/sub/new.txt (nested, new)       silently allowed   WRONG
            //     link/x.txt       (not existing)      asked              correct
            // The only shape the old walk caught was the narrow one where the link itself was the
            // deepest existing entry — which is exactly what the existing symlink test constructs, so
            // it passed while the escape sat underneath it.
            //
            // The check was therefore INVERTED RELATIVE TO RISK: creating a new file outside the
            // boundary was caught, overwriting an existing one was not. No attacker needed —
            // node_modules links, vendor/, monorepo package links and a docs -> ../shared-docs
            // convention are ordinary layouts.
            //
            // Components below the deepest existing entry cannot be links (they do not exist), so
            // walking the whole path costs nothing extra for them and follows a link at any depth.
            var parts = new List<string>();
            for (var cursor = full; cursor is not null; cursor = Path.GetDirectoryName(cursor))
            {
                var name = Path.GetFileName(cursor);
                if (string.IsNullOrEmpty(name))
                {
                    parts.Add(cursor);   // the filesystem root ("/" or "C:\")
                    break;
                }
                parts.Add(name);
            }
            parts.Reverse();

            var resolved = parts[0];
            for (var i = 1; i < parts.Count; i++)
            {
                resolved = Path.Combine(resolved, parts[i]);

                // A LINK IS FOLLOWED WHEREVER IT SITS. returnFinalTarget walks a chain of links to
                // its end; a component that does not exist is not a link, so this is a no-op there.
                var target = Directory.Exists(resolved)
                    ? Directory.ResolveLinkTarget(resolved, returnFinalTarget: true)
                    : File.Exists(resolved)
                        ? File.ResolveLinkTarget(resolved, returnFinalTarget: true)
                        : null;

                if (target is not null)
                    resolved = target.FullName;
            }

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved));
        }
        // ArgumentException is in this list because RequestsFor now RESOLVES (the C1/C2 fix), so a
        // path that Path.GetFullPath rejects outright — an embedded NUL, an empty string — reaches
        // here where the old string-only code could not throw at all. Without it the exception
        // escapes RequestsFor and surfaces as a raw "Null character in path" crash caught by the
        // job executor, losing PermissionDenied = true. Fail toward asking, as everything else here does.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            return null;   // fail toward asking: never let a resolution error produce silent allow.
        }
    }
}

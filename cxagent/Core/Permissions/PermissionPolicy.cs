using System.Text;
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

    public PermissionPolicy(string workingDirRoot, PermissionRulesStore rules)
    {
        _root = workingDirRoot;
        _rules = rules;
    }

    /// <summary>The one mapping from plugin params to permission requests: shell → one Shell
    /// request; file → per-action read/write requests (copy/move produce both a read of the
    /// source and a write of the dest, checked independently); http → one Http request for the
    /// URL's origin.</summary>
    public static IReadOnlyList<PermissionRequest> RequestsFor(string pluginType, JobParameters parameters)
    {
        switch (pluginType)
        {
            case "shell":
                return new[] { ShellRequest(parameters) };

            case "file":
                return FileRequests(parameters);

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

        var alwaysRule = hasEnv ? null : command;
        return new PermissionRequest(PermissionKind.Shell, display.ToString(), alwaysRule);
    }

    private static List<PermissionRequest> FileRequests(JobParameters parameters)
    {
        var action = parameters.Get<string>("action");
        var path = parameters.Get<string>("path");
        var dest = parameters.Get<string?>("dest", null);

        var requests = new List<PermissionRequest>();
        switch (action)
        {
            case "read":
                requests.Add(FileRequest(PermissionKind.FileRead, path));
                break;
            case "write":
            case "append":
                requests.Add(FileRequest(PermissionKind.FileWrite, path));
                break;
            case "delete":
                requests.Add(FileRequest(PermissionKind.FileWrite, path));
                break;
            case "copy":
            case "move":
                requests.Add(FileRequest(PermissionKind.FileRead, path));
                if (dest is not null)
                    requests.Add(FileRequest(PermissionKind.FileWrite, dest));
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
    private static PermissionRequest FileRequest(PermissionKind kind, string path)
    {
        var resolved = TryResolve(path);
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
        if (request.Kind is PermissionKind.FileRead or PermissionKind.FileWrite
            && _rules.GetTrust(_root) == TrustState.Trusted
            && IsInsideBoundary(request.Display))
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
        if (request.Kind == PermissionKind.Shell
            && _rules.GetTrust(_root) == TrustState.Trusted
            && ReadOnlyCommands.IsReadOnly(request.AlwaysRule))
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
    private static string? RuleSubject(PermissionRequest request) => request.Kind switch
    {
        // Path-bearing: resolve before matching, and fail toward asking if it cannot be resolved.
        PermissionKind.FileRead or PermissionKind.FileWrite => TryResolve(request.Display),

        // Not path-bearing. Shell matches the exact command text; Http matches the request origin.
        // Neither is a filesystem path, so there is nothing to resolve — the stored rule IS the
        // subject.
        PermissionKind.Shell or PermissionKind.Http => request.AlwaysRule,

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
        var resolved = TryResolve(path);
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
    /// Strategy: GetFullPath first (collapses "..", makes it absolute), then walk up to the
    /// deepest EXISTING ancestor and resolve THAT (ResolveLinkTarget needs a real entry to
    /// inspect), then re-append whatever doesn't exist yet. If resolution throws for any
    /// reason (permissions, a dangling link, a race), we return null and the caller treats
    /// that as outside the boundary — failing toward asking, never toward silent allow.
    /// </summary>
    private static string? TryResolve(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);

            // Find the deepest existing ancestor (including the path itself if it exists).
            var existing = full;
            var remainder = new List<string>();
            while (!Directory.Exists(existing) && !File.Exists(existing))
            {
                var parent = Path.GetDirectoryName(existing);
                if (string.IsNullOrEmpty(parent) || parent == existing)
                {
                    // No ancestor exists at all (e.g. root doesn't exist, or a relative path
                    // collapsed to nothing usable) — nothing to resolve against.
                    existing = full;
                    remainder.Clear();
                    break;
                }
                remainder.Add(Path.GetFileName(existing));
                existing = parent;
            }

            var resolvedExisting = existing;
            if (Directory.Exists(existing) || File.Exists(existing))
            {
                // Resolve the full symlink chain: if `existing` itself is a link, or the final
                // target is, ResolveLinkTarget(returnFinalTarget: true) walks it all the way.
                var target = Directory.Exists(existing)
                    ? Directory.ResolveLinkTarget(existing, returnFinalTarget: true)
                    : File.ResolveLinkTarget(existing, returnFinalTarget: true);
                if (target is not null)
                    resolvedExisting = target.FullName;
            }

            remainder.Reverse();
            var rebuilt = remainder.Count == 0
                ? resolvedExisting
                : Path.Combine(new[] { resolvedExisting }.Concat(remainder).ToArray());

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rebuilt));
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

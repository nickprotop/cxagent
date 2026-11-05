using System.Text.Json;

namespace CxAgent.Core.Sandbox;

/// <summary>
/// How the model's shell commands are contained: whether at all, whether they reach the network, and the
/// folders and variables the user adds to the built-in lists.
///
/// <para>PATHS ARE HELD AS WRITTEN — <c>~/out</c> stays <c>~/out</c> — and expanded against the home of the
/// machine the command runs on when the layout is built. A saved config then round-trips unchanged, and one
/// file works for a user whose home is somewhere else.</para>
///
/// <para>USER CONFIG ONLY. Nothing a repository brings is read into this: a cloned repository that could
/// widen <see cref="Writable"/> or turn the sandbox off would be loosening the one thing meant to hold
/// against it.</para>
/// </summary>
/// <param name="Enabled">Whether commands run inside the sandbox where it is available.</param>
/// <param name="Network">Whether a contained command reaches the network.</param>
/// <param name="Writable">Folders a contained command may write, beyond the project and the built-in caches.</param>
/// <param name="Hidden">Folders or files a contained command sees empty, beyond the built-in credential list.</param>
/// <param name="HiddenEnv">Variable names or <c>*</c> patterns removed from a contained command's environment.</param>
/// <param name="PassEnv">Variable names kept even when a pattern would remove them.</param>
public sealed record SandboxSettings(
    bool Enabled,
    bool Network,
    IReadOnlyList<string> Writable,
    IReadOnlyList<string> Hidden,
    IReadOnlyList<string> HiddenEnv,
    IReadOnlyList<string> PassEnv)
{
    /// <summary>No sandbox: what an embedder gets when its <c>AgentConfig</c> names none.</summary>
    public static readonly SandboxSettings Off = new(false, true, [], [], [], []);

    /// <summary>
    /// The app's default when config.json names none: on, network open, nothing added.
    ///
    /// <para>ON FOR THE APP AND OFF FOR THE LIBRARY. A person at the terminal is who the sandbox protects; an
    /// embedder's host often runs where bubblewrap cannot start at all (a container without user namespaces,
    /// CI), and turning it on underneath them would refuse their commands on upgrade.</para>
    /// </summary>
    public static readonly SandboxSettings AppDefault = new(true, true, [], [], [], []);

    /// <summary>Whether a path in <see cref="Writable"/> or <see cref="Hidden"/> names one place on every launch:
    /// absolute, or under the home.</summary>
    public static bool IsAnchored(string path) => path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || Path.IsPathRooted(path);

    /// <summary>
    /// Reads config.json's <c>sandbox</c> block. Absent is <see cref="AppDefault"/>; a block of the wrong shape
    /// warns and is <see cref="AppDefault"/> too, so a typo leaves the user protected rather than not.
    /// </summary>
    /// <param name="root">The config document's root object.</param>
    /// <param name="warnings">Where a shape problem is reported.</param>
    public static SandboxSettings Read(JsonElement root, List<string> warnings)
    {
        if (!root.TryGetProperty("sandbox", out var block)) return AppDefault;
        if (block.ValueKind != JsonValueKind.Object)
        {
            warnings.Add("sandbox must be an object — the sandbox stays on with its defaults.");
            return AppDefault;
        }

        bool Flag(string name, bool fallback)
        {
            if (!block.TryGetProperty(name, out var v)) return fallback;
            if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
            warnings.Add($"sandbox.{name} must be true or false — using {(fallback ? "true" : "false")}.");
            return fallback;
        }

        IReadOnlyList<string> List(string name)
        {
            if (!block.TryGetProperty(name, out var v)) return [];
            if (v.ValueKind != JsonValueKind.Array)
            {
                warnings.Add($"sandbox.{name} must be a list of strings — ignored.");
                return [];
            }
            return [.. v.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                .Select(e => e.GetString()!)];
        }

        // A RELATIVE PATH WOULD RESOLVE AGAINST WHEREVER cxagent WAS STARTED — a different folder each launch — so it is
        // refused out loud rather than guessed at.
        IReadOnlyList<string> Paths(string name) => [.. List(name).Where(p =>
        {
            if (IsAnchored(p)) return true;
            warnings.Add($"sandbox.{name}: '{p}' is relative — use a path starting with / or ~. Ignored.");
            return false;
        })];

        return new(Flag("enabled", true), Flag("network", true),
            Paths("writable"), Paths("hidden"), List("hiddenEnv"), List("passEnv"));
    }
}

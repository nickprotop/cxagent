using System.Security.Cryptography;
using System.Text.Json;

namespace CxAgent.Core.Hooks;

/// <summary>
/// The <c>hooks</c> block from the user's config, kept as written. Null <see cref="Block"/> is no block.
///
/// <para>THE ELEMENT, NOT PARSED HOOKS. Parsing tags each hook with its source, and the same block is
/// parsed again for every session; holding the JSON keeps this a plain config value that a loader can
/// build without knowing what a hook is.</para>
/// </summary>
public sealed record HooksSettings(JsonElement? Block)
{
    /// <summary>The <c>provider:model</c> entry hooks that need a model use when they name none.</summary>
    public string? DefaultModel { get; init; }

    public static HooksSettings None { get; } = new((JsonElement?)null);
}

/// <summary>A hooks source that could not be read whole, and why.</summary>
public sealed record HookProblem(HookSource Source, string File, string Message);

/// <summary>
/// The hooks a session runs, as read when it started.
///
/// <para>A SNAPSHOT ON PURPOSE. A repository's hooks file can change under a running session — a pull,
/// another editor, the model itself — and a change that took effect on its own would be a command nobody
/// reviewed running with the user's rights. The fingerprints let the session notice the change and say
/// so; applying it is the user's step.</para>
/// </summary>
/// <param name="RepoRoot">The repository's top folder, or null when no repository encloses the working folder.</param>
/// <param name="Fingerprints">Each project file's path and its content hash at read time ("absent" when missing).</param>
public sealed record HookSnapshot(IReadOnlyList<HookDefinition> Hooks, IReadOnlyList<HookProblem> Problems,
    string? RepoRoot, IReadOnlyDictionary<string, string> Fingerprints)
{
    public static HookSnapshot Empty { get; } = new([], [], null, new Dictionary<string, string>());
}

/// <summary>Reads the user's block and the repository's two hooks files into one <see cref="HookSnapshot"/>.</summary>
public static class HookSources
{
    public const string SharedFile = "hooks.json";
    public const string LocalFile = "hooks.local.json";

    /// <summary>The project's hooks file of this source, or null outside a repository.</summary>
    public static string? ProjectFile(string? repoRoot, HookSource source) => repoRoot is null ? null
        : source switch
        {
            HookSource.Project => Path.Combine(repoRoot, ".cxagent", SharedFile),
            HookSource.Local => Path.Combine(repoRoot, ".cxagent", LocalFile),
            _ => null,
        };

    public static HookSnapshot Read(HooksSettings user, string workingDirectory)
    {
        var hooks = new List<HookDefinition>();
        var problems = new List<HookProblem>();

        if (user.Block is { } block)
        {
            var parsed = HookConfigParser.Parse(block, HookSource.User);
            hooks.AddRange(parsed.Hooks);
            problems.AddRange(parsed.Problems.Select(p => new HookProblem(HookSource.User, "config.json", p)));
        }

        var root = RepoRoot(workingDirectory);
        var prints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in new[] { HookSource.Project, HookSource.Local })
        {
            if (ProjectFile(root, source) is not { } file) continue;
            prints[file] = Fingerprint(file);
            if (!File.Exists(file)) continue;
            ReadProjectFile(file, source, hooks, problems);
        }
        return new(hooks, problems, root, prints);
    }

    /// <summary>The project files that differ from what <paramref name="snapshot"/> read.</summary>
    public static IReadOnlyList<string> ChangedSince(HookSnapshot snapshot) =>
        [.. snapshot.Fingerprints.Where(kv => Fingerprint(kv.Key) != kv.Value).Select(kv => kv.Key)];

    /// <summary>
    /// The top of the enclosing repository, or null.
    ///
    /// <para>THE SAME WALK the instruction files and project skills use, so all three agree on what "the
    /// project" is. Outside a repository there is no project to bring hooks: a scratch folder's
    /// <c>.cxagent</c> is not read.</para>
    /// </summary>
    public static string? RepoRoot(string workingDirectory)
    {
        var levels = Llm.ProjectLevels.From(Path.GetFullPath(workingDirectory));
        var top = levels[^1];
        var git = Path.Combine(top, ".git");
        return Directory.Exists(git) || File.Exists(git) ? Path.TrimEndingDirectorySeparator(top) : null;
    }

    private static void ReadProjectFile(string file, HookSource source, List<HookDefinition> hooks, List<HookProblem> problems)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("hooks", out var block))
            {
                problems.Add(new(source, file, "needs a top-level \"hooks\" object."));
                return;
            }
            var parsed = HookConfigParser.Parse(block, source);
            hooks.AddRange(parsed.Hooks);
            problems.AddRange(parsed.Problems.Select(p => new HookProblem(source, file, p)));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            problems.Add(new(source, file, $"could not be read: {ex.Message}"));
        }
    }

    private static string Fingerprint(string file)
    {
        try
        {
            return File.Exists(file) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) : "absent";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // UNREADABLE IS ITS OWN STATE, so a file that becomes readable again reads as a change.
            return "unreadable";
        }
    }
}

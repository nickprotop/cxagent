using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Plugins.Worktree;

/// <summary>
/// Sandboxes for work that should not land until someone says so: a git worktree per task, merged or
/// thrown away whole.
///
/// <para>WHY THIS IS A PLUGIN AT ALL. A sub-agent edits the files the user is working on, live,
/// and there is no taking back an edit half of which was right. A worktree is a second checkout on
/// its own branch: an agent rooted there changes nothing the user sees until a merge brings it in,
/// and a discard leaves no trace. Spawning with a <c>root</c> already exists in Core; what was
/// missing was a folder worth rooting an agent in.</para>
///
/// <para>MERGE AND DISCARD ASK; THE REST DO NOT. Making a worktree, listing them and reading a diff
/// change nothing the user has — a worktree is a folder they cannot see in their status and a branch
/// under a prefix of its own. A merge changes their current branch, and a discard destroys work, so
/// each asks, naming the worktree; a discard never offers "Always", because one standing yes would
/// let every later discard throw work away unasked.</para>
/// </summary>
public sealed class WorktreePlugin : IPlugin, IPluginGateSource
{
    private IPluginContext? _context;
    private Worktrees? _worktrees;

    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        var settings = Settings(context.Settings, out var executable);
        _worktrees = new Worktrees(new Git(executable, pid => context.RegisterChildProcess(pid)), settings);

        // BESIDE THIS ASSEMBLY, not AppContext.BaseDirectory: that is the host's folder, and a
        // plugin is loaded from wherever it was installed.
        var here = Path.GetDirectoryName(typeof(WorktreePlugin).Assembly.Location)!;
        var parsed = PluginManifest.Parse(File.ReadAllText(Path.Combine(here, "worktree.plugin.json")));
        var manifest = parsed.Manifest
            ?? throw new InvalidOperationException(
                $"worktree.plugin.json could not be read: {string.Join("; ", parsed.Errors)}");
        return Task.FromResult(manifest);
    }

    /// <summary>Nothing to start: every call is a few git commands that have exited before it returns.</summary>
    public Task Start(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Nothing to stop. THE WORKTREES OUTLIVE THE SESSION ON PURPOSE — they are the work, and an
    /// unmerged one is still listed, diffed and merged by the next session in this folder.
    /// </summary>
    public Task Stop(CancellationToken ct) => Task.CompletedTask;

    public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context, CancellationToken ct)
    {
        var worktrees = _worktrees ?? throw new InvalidOperationException("Invoke before Load.");

        // THE CALLER'S FOLDER: the worktree folder is inside it, and a sub-agent's root is resolved
        // against the parent's folder, so the two must be the same one.
        var folder = context.WorkingDirectory ?? _context?.WorkingDirectory ?? Directory.GetCurrentDirectory();

        var outcome = toolName switch
        {
            "worktree_create" => Named(call, out var name) ?? worktrees.Create(folder, name, call.Get<string?>("base", null)),
            "worktree_list" => worktrees.List(folder),
            "worktree_diff" => Named(call, out var name) ?? worktrees.Diff(folder, name, call.Get("stat_only", false)),
            "worktree_merge" => Named(call, out var name) ?? worktrees.Merge(folder, name, call.Get<string?>("message", null), call.Get("keep", false)),
            "worktree_discard" => Named(call, out var name) ?? worktrees.Discard(folder, name),
            _ => Outcome.Refused($"this plugin has no tool named '{toolName}'."),
        };

        return Task.FromResult(outcome.Ok
            ? new JobResult { Success = true, Output = new Dictionary<string, object?> { ["content"] = outcome.Text } }
            : new JobResult { Success = false, ErrorMessage = outcome.Text });
    }

    /// <summary>
    /// What the prompt says. Synchronous and from the arguments alone, as the contract requires — so
    /// it names the worktree and the effect, not the branch it will land on, which only git knows.
    /// </summary>
    public PluginGate? Gate(string toolName, JobParameters call)
    {
        var name = call.Get<string?>("name", null) ?? "?";
        return toolName switch
        {
            "worktree_merge" => new PluginGate(
                $"Merge worktree '{name}' into your current branch"
                + (call.Get("keep", false) ? "" : ", then remove the worktree and its branch"))
            { Subject = name },
            "worktree_discard" => new PluginGate(
                $"Discard worktree '{name}' — its folder and branch, and any work in it not merged", AlwaysAskable: false)
            { Subject = name },
            _ => null,
        };
    }

    /// <summary>A missing name is a call the model can fix, not an exception — <c>Get</c> throws on an absent key.</summary>
    private static Outcome? Named(JobParameters call, out string name)
    {
        name = call.Get<string?>("name", null) ?? "";
        return string.IsNullOrWhiteSpace(name) ? Outcome.Refused("this tool needs a 'name': the worktree's name.") : null;
    }

    /// <summary>
    /// The settings object, read leniently: a missing or mistyped key is its default rather than a
    /// plugin that will not load — every setting has one that works.
    /// </summary>
    public static WorktreeSettings Settings(JsonElement json, out string executable)
    {
        var defaults = new WorktreeSettings();
        executable = Text(json, "git") ?? "git";
        return new WorktreeSettings(Text(json, "folder") ?? defaults.Folder, Text(json, "branchPrefix") ?? defaults.BranchPrefix);
    }

    private static string? Text(JsonElement json, string key) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text : null;
}

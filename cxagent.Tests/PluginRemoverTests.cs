using CxAgent.Core.Llm;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Deleting a plugin's files, which is irreversible and therefore planned before it is done.
///
/// <para>THE PLAN IS THE POINT. Every path is resolved and checked before anything is removed, so a
/// confirmation can name exactly what is about to go — "uninstall csharp-lsp?" gives a user nothing
/// to check.</para>
/// </summary>
public class PluginRemoverTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "plugin-remove-" + Guid.NewGuid().ToString("N"));

    public PluginRemoverTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private string Place(string directory, string name, params string[] extras)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".dll"), "x");
        File.WriteAllText(Path.Combine(directory, name + ".plugin.json"),
            $$"""{"pluginContract":2,"name":"{{name}}","version":"1.0.0","spawns":false,"tools":[]}""");
        foreach (var e in extras) File.WriteAllText(Path.Combine(directory, e), "x");
        return directory;
    }

    private static Dictionary<string, PluginConfig> Configured(params (string Name, string File)[] entries) =>
        entries.ToDictionary(e => e.Name, e => new PluginConfig(e.File));

    /// <summary>
    /// A NESTED PLUGIN IS ITS DIRECTORY, by the load-set design's own definition — the folder is
    /// what the hash covers and what dependency resolution reads. So everything in it goes.
    /// </summary>
    [Fact]
    public void ANestedPluginRemovesItsWholeDirectory()
    {
        var home = Place(Path.Combine(_root, "demo"), "demo", "demo.deps.json", "extra.dll");

        var plan = PluginRemover.Plan("demo", "demo.dll", home, [_root], Configured(("demo", "demo.dll")));

        var removable = Assert.IsType<RemovalPlan.Removable>(plan);
        Assert.Equal(4, removable.Files.Count);
        Assert.Empty(removable.LeftBehind);
    }

    /// <summary>
    /// A LOOSE PLUGIN HAS NO DIRECTORY OF ITS OWN, so only its two named files go — and anything
    /// else in the folder is listed rather than guessed at. The sidecar carries no file inventory,
    /// so a dependency beside a loose plugin is genuinely unattributable.
    /// </summary>
    [Fact]
    public void ALoosePluginRemovesNamedFilesAndListsTheRest()
    {
        Place(_root, "demo", "some-dependency.dll");

        var plan = PluginRemover.Plan("demo", "demo.dll", _root, [_root], Configured(("demo", "demo.dll")));

        var removable = Assert.IsType<RemovalPlan.Removable>(plan);
        Assert.Equal(2, removable.Files.Count);
        Assert.Contains(removable.LeftBehind, f => f.EndsWith("some-dependency.dll", StringComparison.Ordinal));
    }

    /// <summary>
    /// TWO ENTRIES CAN NAME ONE BINARY. config.sample.json ships csharp-lsp and
    /// csharp-lsp-omnisharp pointing at the same file with different settings; deleting the files
    /// for one would break a plugin the user did not touch.
    /// </summary>
    [Fact]
    public void AFileAnotherEntryStillNamesIsNotRemoved()
    {
        Place(_root, "demo");

        var plan = PluginRemover.Plan("demo", "demo.dll", _root, [_root],
            Configured(("demo", "demo.dll"), ("demo-other-settings", "demo.dll")));

        var blocked = Assert.IsType<RemovalPlan.Blocked>(plan);
        Assert.Contains("demo-other-settings", blocked.Reason);
    }

    /// <summary>
    /// A PATH OUTSIDE THE PLUGINS FOLDERS IS A BUG HERE OR A MALFORMED CONFIG, and neither is a
    /// reason to delete something. Refused before anything is resolved further.
    /// </summary>
    [Fact]
    public void APathOutsideEveryPluginsFolderIsBlocked()
    {
        var elsewhere = Place(Path.Combine(_root, "..", "outside-" + Guid.NewGuid().ToString("N")), "demo");

        try
        {
            var plan = PluginRemover.Plan("demo", "demo.dll", elsewhere, [_root],
                Configured(("demo", "demo.dll")));

            Assert.IsType<RemovalPlan.Blocked>(plan);
        }
        finally { Directory.Delete(elsewhere, recursive: true); }
    }

    /// <summary>Planning removes nothing; only Remove does.</summary>
    [Fact]
    public void PlanningDoesNotDelete()
    {
        var home = Place(Path.Combine(_root, "demo"), "demo");

        PluginRemover.Plan("demo", "demo.dll", home, [_root], Configured(("demo", "demo.dll")));

        Assert.True(File.Exists(Path.Combine(home, "demo.dll")));
    }

    /// <summary>And Remove does what the plan said, returning what it could not.</summary>
    [Fact]
    public void RemoveDeletesExactlyThePlannedFiles()
    {
        var home = Place(Path.Combine(_root, "demo"), "demo", "demo.deps.json");
        var plan = (RemovalPlan.Removable)PluginRemover.Plan(
            "demo", "demo.dll", home, [_root], Configured(("demo", "demo.dll")));

        var failures = PluginRemover.Remove(plan);

        Assert.Empty(failures);
        Assert.False(Directory.Exists(home));
    }
}

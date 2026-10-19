namespace CxAgent.Plugins.Testing;

/// <summary>
/// The sidecar and the binary in a plugin folder — the shape a user installs: one
/// <c>&lt;name&gt;.plugin.json</c> and the library of the same stem beside it.
/// </summary>
internal sealed record PluginFolder(string Sidecar, string Binary)
{
    // THE BINARY'S EXTENSION IS NOT IN THE SIDECAR, so the three a plugin can be are tried in turn.
    private static readonly string[] Extensions = [".dll", ".so", ".dylib"];

    public static PluginFolder? Find(string folder, List<PluginProblem> problems)
    {
        if (!Directory.Exists(folder))
        {
            problems.Add(new("sidecar", $"'{folder}' is not a folder."));
            return null;
        }

        var sidecars = Directory.GetFiles(folder, "*.plugin.json");
        if (sidecars.Length == 0)
        {
            problems.Add(new("sidecar", $"no *.plugin.json in '{folder}'."));
            return null;
        }

        if (sidecars.Length > 1)
        {
            problems.Add(new("sidecar", "more than one sidecar: "
                + string.Join(", ", sidecars.Select(Path.GetFileName).Order()) + ". A plugin folder holds one."));
            return null;
        }

        var stem = Path.GetFileName(sidecars[0])[..^".plugin.json".Length];
        var binary = Extensions.Select(e => Path.Combine(folder, stem + e)).FirstOrDefault(File.Exists);
        if (binary is null)
        {
            problems.Add(new("file", $"no {string.Join(" or ", Extensions.Select(e => stem + e))} beside {Path.GetFileName(sidecars[0])}."));
            return null;
        }

        return new PluginFolder(sidecars[0], binary);
    }
}

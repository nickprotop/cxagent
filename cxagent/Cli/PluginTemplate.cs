using System.Reflection;
using System.Text.RegularExpressions;
using CxAgent.Core.Plugins;

namespace CxAgent.Cli;

/// <summary>One file of a template, its path and text with the tokens still in them.</summary>
internal sealed record TemplateFile(string RelativePath, string Text);

/// <summary>What fills a template's tokens.</summary>
/// <param name="Name">The plugin's name — <c>CXP_NAME</c>.</param>
/// <param name="Namespace">A C# namespace made from it — <c>CXP_ROOT_NAMESPACE</c>.</param>
/// <param name="AbstractionsVersion">The contract package this build carries — <c>CXP_ABSTRACTIONS_VERSION</c>.</param>
/// <param name="TestingVersion">The test package released with this build — <c>CXP_TESTING_VERSION</c>.</param>
internal sealed record TemplateValues(string Name, string Namespace, string AbstractionsVersion, string TestingVersion)
{
    public static TemplateValues For(string name) => new(name, NamespaceOf(name), ContractPackageVersion(), ThisBuildsVersion());

    // THE SAME RULE AS dotnet new's safe_namespace, so both ways to start a plugin name it alike:
    // anything that is not a letter, digit or underscore becomes an underscore.
    private static string NamespaceOf(string name)
    {
        var safe = Regex.Replace(name, "[^A-Za-z0-9_]", "_");
        return safe.Length > 0 && char.IsDigit(safe[0]) ? "_" + safe : safe;
    }

    private static string ContractPackageVersion() =>
        typeof(PluginManifest).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "PackageVersion")?.Value ?? "4.0.0";

    // THIS BUILD'S OWN VERSION — the test package is released with it — without the +commit suffix,
    // which a PackageReference version cannot carry.
    private static string ThisBuildsVersion() =>
        PluginContract.HostVersionOf(typeof(PluginTemplate).Assembly).Split('+')[0];
}

/// <summary>
/// The plugin templates embedded in this app — the files under <c>cxagent.Core/sdk/templates</c> that
/// the dotnet new template package carries too — and how their tokens are filled.
/// </summary>
internal static class PluginTemplate
{
    /// <summary>The files of the <paramref name="language"/> template, without dotnet new's own config.</summary>
    public static IReadOnlyList<TemplateFile> Files(string language)
    {
        var prefix = $"template/{language}/";
        var assembly = typeof(PluginTemplate).Assembly;
        var files = new List<TemplateFile>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(r => r.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var path = resource[prefix.Length..].Replace('\\', '/');
            if (path.StartsWith(".template.config/", StringComparison.Ordinal)) continue;

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            files.Add(new TemplateFile(path, reader.ReadToEnd()));
        }

        return files;
    }

    public static string Fill(string text, TemplateValues values) => text
        .Replace("CXP_ROOT_NAMESPACE", values.Namespace, StringComparison.Ordinal)
        .Replace("CXP_ABSTRACTIONS_VERSION", values.AbstractionsVersion, StringComparison.Ordinal)
        .Replace("CXP_TESTING_VERSION", values.TestingVersion, StringComparison.Ordinal)
        .Replace("CXP_NAME", values.Name, StringComparison.Ordinal);

    internal static TemplateValues ValuesForTest(string name) => TemplateValues.For(name);
}

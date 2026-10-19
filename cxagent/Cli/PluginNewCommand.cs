using System.Text.RegularExpressions;

namespace CxAgent.Cli;

/// <summary>
/// <c>cxagent plugin new &lt;name&gt; --lang csharp [--dir d]</c> — writes a working plugin project from
/// the embedded template.
///
/// <para>IT NEVER OVERWRITES: a folder that exists and holds anything is refused, so a mistyped
/// directory cannot cost someone their work.</para>
/// </summary>
internal static partial class PluginNewCommand
{
    // THE ONE RULE FOR A NEW PLUGIN'S NAME: it becomes a folder, a config key, a file stem and every
    // tool's prefix, so it is lowercase, starts with a letter, and holds only letters, digits and '-'.
    [GeneratedRegex("^[a-z][a-z0-9-]{0,39}$")]
    private static partial Regex ValidName();

    public static Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? name = null, lang = null, dir = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--lang": lang = i + 1 < args.Length ? args[++i] : ""; break;
                case "--dir": dir = i + 1 < args.Length ? args[++i] : ""; break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal) || name is not null)
                        return Task.FromResult(PluginCli.Fail(stderr, $"unexpected '{args[i]}'."));
                    name = args[i];
                    break;
            }
        }

        if (name is null || !ValidName().IsMatch(name))
            return Task.FromResult(PluginCli.Fail(stderr,
                $"'{name}' is not a plugin name: lowercase letters, digits and '-', starting with a letter, at most 40."));
        if (lang is "c")
            return Task.FromResult(PluginCli.Fail(stderr, "C plugins arrive with the C SDK; --lang c is not available yet."));
        if (lang is not "csharp") return Task.FromResult(PluginCli.Fail(stderr, "--lang is csharp or c."));
        if (dir is "") return Task.FromResult(PluginCli.Fail(stderr, "--dir takes a folder."));

        var target = Path.GetFullPath(dir ?? name);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            return Task.FromResult(PluginCli.Fail(stderr, $"'{target}' is not empty; new never writes over anything."));

        var values = TemplateValues.For(name);
        foreach (var file in PluginTemplate.Files("csharp"))
        {
            var path = Path.Combine(target, PluginTemplate.Fill(file.RelativePath, values));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, PluginTemplate.Fill(file.Text, values));
        }

        stdout.WriteLine($"Created {name} in {target}.");
        stdout.WriteLine("Next:");
        stdout.WriteLine($"  cd {target}");
        stdout.WriteLine("  dotnet build && cxagent plugin check bin/Debug/net10.0");
        stdout.WriteLine($"  cxagent plugin call bin/Debug/net10.0 {name}_greet '{{\"name\":\"Ada\"}}'");
        return Task.FromResult(0);
    }
}

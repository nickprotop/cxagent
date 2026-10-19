using System.Text.RegularExpressions;
using CxAgent.Cli;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The template's two readers — dotnet new's template.json and `cxagent plugin new` — fill the same tokens.</summary>
public class PluginTemplateTests
{
    private static string TemplateDir([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "cxagent.Core", "sdk", "templates", "csharp");

    private static HashSet<string> Tokens(string text) => Regex.Matches(text, "CXP_[A-Z]+(?:_[A-Z]+)*").Select(m => m.Value).ToHashSet();

    /// <summary>Every token a file uses is one template.json declares, so dotnet new fills what cxagent plugin new fills.</summary>
    [Fact]
    public void EveryTokenInTheFiles_IsDeclaredInTemplateJson()
    {
        var declared = Tokens(File.ReadAllText(Path.Combine(TemplateDir(), ".template.config", "template.json")));
        var used = Directory.EnumerateFiles(TemplateDir(), "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".template.config"))
            .SelectMany(f => Tokens(File.ReadAllText(f) + Path.GetFileName(f)))
            .ToHashSet();

        Assert.Subset(declared, used);
    }

    /// <summary>The tokens the app fills are the ones template.json declares.</summary>
    [Fact]
    public void TheAppFills_EveryDeclaredToken()
    {
        var declared = Tokens(File.ReadAllText(Path.Combine(TemplateDir(), ".template.config", "template.json")));

        var filled = PluginTemplate.Fill(string.Join(' ', declared), PluginTemplate.ValuesForTest("weather"));

        Assert.DoesNotContain("CXP_", filled);
    }

    /// <summary>
    /// dotnet new's sourceName gives its safe_namespace form to every token that starts the same way,
    /// so "my-tool" became "my_tool" in content while files kept "my-tool". The name is its own symbol,
    /// taken as typed.
    /// </summary>
    [Fact]
    public void TheName_IsTakenAsTyped_NotThroughSourceName()
    {
        var json = File.ReadAllText(Path.Combine(TemplateDir(), ".template.config", "template.json"));

        Assert.DoesNotContain("sourceName", json);
        Assert.Contains("\"replaces\": \"CXP_NAME\"", json);
        Assert.Contains("\"fileRename\": \"CXP_NAME\"", json);
    }
}

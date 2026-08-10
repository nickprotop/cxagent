using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Placeholder substitution, so a credential never has to be typed into config.json.
///
/// <para>The file is 0600 and a literal still works, so this is not about who can read the file — it
/// is about what a config gets used for. People paste config.json into issues, commit it to dotfiles
/// and screen-share it; a placeholder survives all three.</para>
/// </summary>
public class ConfigVariableTests
{
    private static readonly Dictionary<string, string> NoEnv = new();

    [Fact]
    public void Substitute_ExpandsAnEnvironmentVariable()
    {
        var warnings = new List<string>();
        var env = new Dictionary<string, string> { ["MY_KEY"] = "s3cret" };

        Assert.Equal("Bearer s3cret",
            ConfigVariable.Substitute("Bearer {env:MY_KEY}", warnings, env));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// AN UNSET VARIABLE IS EMPTY AND A WARNING, NOT AN ERROR.
    ///
    /// <para>This runs inside the loader, whose MCP block is non-fatal by design: one unset variable
    /// must not take the config down and every provider with it. The user is told without being
    /// stopped.</para>
    /// </summary>
    [Fact]
    public void Substitute_AnUnsetVariable_IsEmptyAndWarns()
    {
        var warnings = new List<string>();

        Assert.Equal("Bearer ", ConfigVariable.Substitute("Bearer {env:ABSENT}", warnings, NoEnv));
        Assert.Contains(warnings, w => w.Contains("ABSENT", StringComparison.Ordinal));
    }

    [Fact]
    public void Substitute_ExpandsSeveralPlaceholdersInOneValue()
    {
        var warnings = new List<string>();
        var env = new Dictionary<string, string> { ["A"] = "one", ["B"] = "two" };

        Assert.Equal("one-two", ConfigVariable.Substitute("{env:A}-{env:B}", warnings, env));
    }

    /// <summary>A literal value passes through untouched — placeholders are opt-in.</summary>
    [Fact]
    public void Substitute_LeavesALiteralAlone()
    {
        var warnings = new List<string>();

        Assert.Equal("Bearer sk-literal-token",
            ConfigVariable.Substitute("Bearer sk-literal-token", warnings, NoEnv));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// A FILE'S CONTENTS ARE TRIMMED. A key file written by an editor ends with a newline, and a
    /// trailing \n in an Authorization header is the classic silent 401 — the value looks right in
    /// every log and is rejected by every server.
    /// </summary>
    [Fact]
    public void Substitute_ReadsAFile_AndTrimsIt()
    {
        var path = Path.Combine(Path.GetTempPath(), "cxa-key-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "file-token\n");
        try
        {
            var warnings = new List<string>();

            Assert.Equal("Bearer file-token",
                ConfigVariable.Substitute($"Bearer {{file:{path}}}", warnings, NoEnv));
            Assert.Empty(warnings);
        }
        finally { File.Delete(path); }
    }

    /// <summary>An unreadable file warns and yields empty, rather than throwing out of the loader.</summary>
    [Fact]
    public void Substitute_AnUnreadableFile_IsEmptyAndWarns()
    {
        var warnings = new List<string>();
        var missing = Path.Combine(Path.GetTempPath(), "cxa-absent-" + Guid.NewGuid().ToString("N"));

        Assert.Equal("", ConfigVariable.Substitute($"{{file:{missing}}}", warnings, NoEnv));
        Assert.Single(warnings);
    }

    /// <summary>Values are expanded; KEYS are not. A header name is not somewhere anyone puts a
    /// secret, and substituting there would only create ways to build a malformed header.</summary>
    [Fact]
    public void SubstituteValues_ExpandsValuesButNotKeys()
    {
        var warnings = new List<string>();
        Environment.SetEnvironmentVariable("CXA_TEST_TOKEN", "abc123");
        try
        {
            var result = ConfigVariable.SubstituteValues(
                new Dictionary<string, string> { ["Authorization"] = "Bearer {env:CXA_TEST_TOKEN}" },
                warnings, "mcp.srv.headers");

            Assert.Equal("Bearer abc123", result!["Authorization"]);
            Assert.True(result.ContainsKey("Authorization"));
        }
        finally { Environment.SetEnvironmentVariable("CXA_TEST_TOKEN", null); }
    }

    /// <summary>A complaint names the setting it came from — "{env:FOO} is not set" alone leaves the
    /// user hunting through a config for which server wanted it.</summary>
    [Fact]
    public void SubstituteValues_NamesTheSettingInItsWarning()
    {
        var warnings = new List<string>();

        ConfigVariable.SubstituteValues(
            new Dictionary<string, string> { ["Authorization"] = "{env:NOPE_NOT_SET}" },
            warnings, "mcp.srv.headers");

        var warning = Assert.Single(warnings);
        Assert.Contains("mcp.srv.headers", warning, StringComparison.Ordinal);
        Assert.Contains("Authorization", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The key comparer is the CALLER's choice, because HTTP header names are case-insensitive while
    /// Unix environment names are not — collapsing the two here would quietly merge PATH and Path.
    /// </summary>
    [Fact]
    public void SubstituteValues_KeepsCaseDistinctKeys_WhenTheComparerIsOrdinal()
    {
        var warnings = new List<string>();

        var result = ConfigVariable.SubstituteValues(
            new Dictionary<string, string> { ["PATH"] = "a", ["Path"] = "b" },
            warnings, "mcp.srv.env", StringComparer.Ordinal);

        Assert.Equal(2, result!.Count);
    }
}

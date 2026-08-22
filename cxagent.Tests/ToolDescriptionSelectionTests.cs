using CxAgent.Core.Llm;
using CxAgent.Core.Jobs;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Descriptions that name ANOTHER tool — the surface written once and read every turn.
///
/// <para>Before the selection existed these were simply true. Now "use replace_in_file instead" can
/// name a tool this agent was never offered, and a model that follows it spends a turn being told
/// the tool is not available. Definitions() already receives the allowed set, so this needs no new
/// plumbing — and definitions are rebuilt per turn by design, so unlike the system prompt there is
/// no cached prefix to churn.</para>
/// </summary>
public class ToolDescriptionSelectionTests
{
    private static string DescriptionOf(string name, IReadOnlyList<BuiltinTool> offered)
        => ToolBindings.For(offered, JobRegistry.CreateWithBuiltins())
            .First(d => d.Name == name).Description;

    // --- The pointer appears when its target is offered ---------------------------------

    [Fact]
    public void WriteFilePointsAtReplaceInFileWhenBothAreOffered()
    {
        var d = DescriptionOf(Tool.WriteFile, [BuiltinTool.WriteFile, BuiltinTool.ReplaceInFile]);

        Assert.Contains("use replace_in_file instead", d);
    }

    [Fact]
    public void WriteFileDropsThePointerWhenReplaceInFileIsWithheld()
    {
        // THE ADVICE IS GOOD AND UNREACHABLE. Routing the model to a withheld tool costs a turn and
        // leaves it where it started, with less budget.
        var d = DescriptionOf(Tool.WriteFile, [BuiltinTool.WriteFile]);

        Assert.DoesNotContain("replace_in_file", d);

        // THE REST OF THE DESCRIPTION SURVIVES. Only the pointer is conditional — the overwrite
        // warning is about write_file itself and is true however the set is narrowed.
        Assert.Contains("REPLACING everything in it", d);
        Assert.Contains("read it first", d, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WebFetchPointsAtHttpRequestOnlyWhenItIsOffered()
    {
        Assert.Contains("http_request",
            DescriptionOf(Tool.WebFetch, [BuiltinTool.WebFetch, BuiltinTool.HttpRequest]));

        Assert.DoesNotContain("http_request", DescriptionOf(Tool.WebFetch, [BuiltinTool.WebFetch]));
    }

    [Fact]
    public void WebFetchKeepsItsOwnDescriptionEitherWay()
    {
        Assert.Contains("Read a web page as text", DescriptionOf(Tool.WebFetch, [BuiltinTool.WebFetch]));
    }

    // --- A mention that is NOT a pointer stays whole -------------------------------------

    [Fact]
    public void GrepKeepsItsRunShellMentionEvenWhenRunShellIsWithheld()
    {
        // NOT A CROSS-REFERENCE. It points AWAY from run_shell, toward the tool being described —
        // "use this rather than that" is an argument for grep, correct however the set is narrowed.
        // Treating every mention as a pointer would delete the sentence that justifies the tool.
        var d = DescriptionOf(Tool.Grep, [BuiltinTool.SearchFiles]);

        Assert.Contains("rather than run_shell", d);
    }

    [Fact]
    public void GlobKeepsItsRunShellMentionToo()
        => Assert.Contains("rather than run_shell", DescriptionOf(Tool.Glob, [BuiltinTool.ListFiles]));

    // --- The default is untouched --------------------------------------------------------

    [Fact]
    public void WithEveryToolOfferedEveryPointerIsPresent()
    {
        // The whole set is the common case, and it must read exactly as it did before the feature.
        var all = Enum.GetValues<BuiltinTool>();

        Assert.Contains("use replace_in_file instead", DescriptionOf(Tool.WriteFile, all));
        Assert.Contains("http_request", DescriptionOf(Tool.WebFetch, all));
    }
}

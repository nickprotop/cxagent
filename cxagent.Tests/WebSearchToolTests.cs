using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>web_search as a built-in: its name, its binding, who is offered it, and what the prompt says.</summary>
public class WebSearchToolTests
{
    private static ToolDefinition Def(string name) =>
        ToolBindings.For([BuiltinTool.WebSearch, BuiltinTool.WebFetch], JobRegistry.CreateWithBuiltins())
            .Single(t => t.Name == name);

    [Fact]
    public void Web_search_is_a_known_name()
    {
        Assert.Equal("web_search", Tool.WebSearch);
        Assert.True(Tool.IsKnown(Tool.WebSearch));
        Assert.Equal("-web_search", Tool.Not.WebSearch);
        Assert.Equal("+web_search", Tool.Also.WebSearch);
    }

    [Fact]
    public void Minus_web_search_removes_it() =>
        Assert.False(ToolSelection.Offers(new ToolSelection(["inherited", Tool.Not.WebSearch]), Tool.WebSearch));

    [Fact]
    public void Web_search_binding_requires_only_query()
    {
        var schema = Def("web_search").InputSchema;
        Assert.Equal(["query"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.True(schema.GetProperty("properties").TryGetProperty("max_results", out _));
        Assert.Equal(["duckduckgo"], schema.GetProperty("properties").GetProperty("engine").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Web_fetch_points_to_web_search_only_when_offered()
    {
        Assert.Contains("web_search", Def("web_fetch").Description);
        var alone = ToolBindings.For([BuiltinTool.WebFetch], JobRegistry.CreateWithBuiltins()).Single();
        Assert.DoesNotContain("web_search", alone.Description);
    }

    /// <summary>NOT A WRITE: a read-only sub-agent type, which removes the write tools, keeps search.</summary>
    [Fact]
    public void Web_search_is_offered_to_a_read_only_sub_agent()
    {
        var explore = new ToolSelection(["inherited", Tool.Not.WriteFile, Tool.Not.ReplaceInFile]);
        Assert.True(ToolSelection.Offers(explore, Tool.WebSearch));
    }
}

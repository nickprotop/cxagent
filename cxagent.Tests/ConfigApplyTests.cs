using CxAgent.Core.Llm;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The process's current config: one holder, replaced by a save, telling who asked.</summary>
public class ConfigApplyTests
{
    [Fact]
    public void Replace_RaisesReplaced_WithTheNewValue()
    {
        var first = ResolvedConfig.ForTesting(new MockLlmProvider("a"));
        var holder = new CurrentConfig(first);
        ResolvedConfig? seen = null;
        holder.Replaced += next => seen = next;

        var second = ResolvedConfig.ForTesting(new MockLlmProvider("b"));
        holder.Replace(second);

        Assert.Same(second, holder.Value);
        Assert.Same(second, seen);
    }
}

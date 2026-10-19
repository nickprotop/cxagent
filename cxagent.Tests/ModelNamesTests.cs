using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>One shortener for every place a model is named on screen, so the status bar and a child's row never disagree.</summary>
public class ModelNamesTests
{
    [Theory]
    [InlineData("qwen3.6-35b-a3b-ud-iq4_xs.gguf", "qwen3.6-35b-a3b-ud-iq4_xs")]
    [InlineData("model.GGUF", "model")]
    [InlineData("deepseek/deepseek-v3.2", "deepseek/deepseek-v3.2")]   // a vendor prefix stays (spec A3)
    [InlineData("claude-opus-5-5", "claude-opus-5-5")]
    public void Short_DropsAModelFilesExtension_Only(string id, string shown) =>
        Assert.Equal(shown, ModelNames.Short(id));

    [Theory]
    [InlineData("local:qwen3.6-35b-a3b-ud-iq4_xs.gguf", "local:qwen3.6-35b-a3b-ud-iq4_xs")]
    [InlineData("deepseek:deepseek/deepseek-v3.2", "deepseek:deepseek/deepseek-v3.2")]
    [InlineData("mock-model", "mock-model")]
    public void ShortLabel_ShortensTheModelPart(string label, string shown) =>
        Assert.Equal(shown, ModelNames.ShortLabel(label));
}

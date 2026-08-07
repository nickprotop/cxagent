using SharpConsoleUI.Controls;
using SharpConsoleUI.Flows;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class MaskedPromptStepTests
{
    private static FlowChrome Chrome() => new("API key", null, widthHint: 50, autoSizeHeight: true);

    [Fact]
    public void BuildContent_ReturnsAPromptControl_MaskedWithAsteriskByDefault()
    {
        var step = new MaskedPromptStep("API key:");

        var prompt = Assert.IsType<PromptControl>(step.BuildContent(Chrome()));

        Assert.Equal('*', prompt.MaskCharacter);
    }

    [Fact]
    public void CustomMaskCharacter_IsHonoured()
    {
        var step = new MaskedPromptStep("API key:", mask: '#');

        var prompt = Assert.IsType<PromptControl>(step.BuildContent(Chrome()));

        Assert.Equal('#', prompt.MaskCharacter);
    }

    [Fact]
    public void Completion_IsNotAlreadyResolved_BeforeTheUserEnters()
    {
        var step = new MaskedPromptStep("API key:");
        step.BuildContent(Chrome());

        // A step whose Completion is pre-resolved would advance the wizard instantly, skipping the
        // key entry entirely.
        Assert.False(step.Completion.IsCompleted);
    }
}

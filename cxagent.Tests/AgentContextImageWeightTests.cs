using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// An image counts in the context's size by its dimensions. It costs the model tokens and has no
/// characters, so without a weight one screenshot would raise the measured density of every text
/// character, and a short message carrying one would read as an impossible measurement.
/// </summary>
public class AgentContextImageWeightTests
{
    private static AttachedImage Screenshot() => new("[Image #1 1568×882]", "h", 1568, 882);

    // ceil(1568 * 882 / 750) tokens, at four characters each.
    private const int ScreenshotChars = 1844 * 4;

    [Fact]
    public void TotalChars_CountsAnImageByItsDimensions()
    {
        var context = new AgentContext(window: 100_000);
        context.Add(new ChatMessage { Role = "user", Content = "look", Images = [Screenshot()] });

        Assert.Equal(4 + ScreenshotChars, context.TotalChars());
    }

    [Fact]
    public void RecordUsage_AcceptsAShortMessageCarryingAScreenshot()
    {
        var context = new AgentContext(window: 100_000);
        context.Add(new ChatMessage { Role = "user", Content = "what is this?", Images = [Screenshot()] });

        context.RecordUsage(1_900);

        Assert.Equal(1_900, context.Used);
    }

    /// <summary>
    /// Text added after an image grows the projection at the text's own density: 4,000 characters of
    /// text at four characters a token is a thousand tokens more, not the inflated rate the image's
    /// tokens would give a conversation that counted only characters.
    /// </summary>
    [Fact]
    public void ProjectedUsed_AfterAnImage_DoesNotInflateText()
    {
        var context = new AgentContext(window: 100_000);
        context.Add(new ChatMessage { Role = "user", Content = new string('x', 4_000), Images = [Screenshot()] });
        context.RecordUsage(1_000 + 1844);

        context.Add(new ChatMessage { Role = "assistant", Content = new string('y', 4_000) });

        Assert.InRange(context.ProjectedUsed!.Value, 3_800, 3_900);
    }
}

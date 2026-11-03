using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Compression and images: the summariser reads the label the message carries, never the image, and
/// the newest half, kept as it was, keeps its images.
/// </summary>
public class ImageCompressionTests
{
    private static AttachedImage Image(string label) => new(label, "h", 1568, 882) { Png = new byte[2048] };

    private static AgentContext Conversation()
    {
        var context = new AgentContext(window: 100_000);
        context.Add(new ChatMessage { Role = "system", Content = "you are the agent" });
        context.Add(new ChatMessage { Role = "user", Content = "old: what is [Image #1 1568×882]? " + new string('x', 5_000), Images = [Image("[Image #1 1568×882]")] });
        for (var i = 0; i < 10; i++)
        {
            context.Add(new ChatMessage { Role = "assistant", Content = new string('a', 5_000) });
            context.Add(new ChatMessage { Role = "user", Content = new string('u', 5_000) });
        }
        context.Add(new ChatMessage { Role = "user", Content = "new: and [Image #2 1568×882]?", Images = [Image("[Image #2 1568×882]")] });
        return context;
    }

    private static MockLlmProvider Summariser()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "they asked about a screenshot", StopReason = "end_turn" });
        return mock;
    }

    [Fact]
    public async Task TheSummariser_SeesTheLabel_NotTheImage()
    {
        var mock = Summariser();

        await SessionCompressor.CompressAsync(Conversation(), mock, CancellationToken.None);

        Assert.DoesNotContain(mock.LastMessages!, m => m.Images is { Count: > 0 });
        Assert.Contains(mock.LastMessages!, m => m.Content.Contains("[Image #1 1568×882]"));
    }

    [Fact]
    public async Task KeptMessages_KeepTheirImages()
    {
        var context = Conversation();

        await SessionCompressor.CompressAsync(context, Summariser(), CancellationToken.None);

        var kept = context.Messages.Where(m => m.Images is { Count: > 0 }).SelectMany(m => m.Images!).Select(i => i.Label);
        Assert.Equal(["[Image #2 1568×882]"], kept);
    }
}

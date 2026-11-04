using System.Text.Json.Nodes;
using CxAgent.Core.Llm.Providers;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A message's images on the wire: each endpoint's own shape when the model can see them, and the
/// placeholder in the text when it cannot or the bytes are gone, so a request never fails over an image.
/// </summary>
public class ImageWireTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];

    private static AttachedImage Image(byte[]? png = null) =>
        new("[Image #1 4×2]", "h", 4, 2) { Bytes = png ?? Png };

    private static List<ChatMessage> Asked(AttachedImage image) =>
        [new ChatMessage { Role = "user", Content = "what is [Image #1 4×2]?", Images = [image] }];

    private static JsonNode? OpenAiUser(List<ChatMessage> messages, bool sendImages = true) =>
        OpenAiWire.BuildRequestBody("m", messages, null, stream: false, sendImages: sendImages)["messages"]![0];

    private static JsonNode? AnthropicUser(List<ChatMessage> messages, bool sendImages = true, bool cache = false) =>
        AnthropicWire.BuildRequestBody("m", 100, messages, null, stream: false, cache: cache, sendImages: sendImages)["messages"]![0];

    [Fact]
    public void OpenAi_SendsTheTextThenADataUrlPerImage()
    {
        var content = OpenAiUser(Asked(Image()))!["content"]!.AsArray();

        Assert.Equal("text", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("what is [Image #1 4×2]?", content[0]!["text"]!.GetValue<string>());
        Assert.Equal("image_url", content[1]!["type"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(Png),
            content[1]!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void Anthropic_SendsTheImageBlocksThenTheText()
    {
        var content = AnthropicUser(Asked(Image()))!["content"]!.AsArray();

        Assert.Equal("image", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("base64", content[0]!["source"]!["type"]!.GetValue<string>());
        Assert.Equal("image/png", content[0]!["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(Png), content[0]!["source"]!["data"]!.GetValue<string>());
        Assert.Equal("text", content[1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void OpenAi_AModelThatCannotSee_GetsThePlaceholderInPlainText()
    {
        var user = OpenAiUser(Asked(Image()), sendImages: false)!;

        Assert.Equal("what is [Image #1 4×2]?\n[image not sent: this model cannot see images — Image #1, 4×2]", user["content"]!.GetValue<string>());
    }

    [Fact]
    public void Anthropic_AModelThatCannotSee_GetsThePlaceholderInPlainText()
    {
        var user = AnthropicUser(Asked(Image()), sendImages: false)!;

        Assert.Equal("what is [Image #1 4×2]?\n[image not sent: this model cannot see images — Image #1, 4×2]", user["content"]!.GetValue<string>());
    }

    [Fact]
    public void OpenAi_GoneBytes_SendTheUnavailableNote()
    {
        var user = OpenAiUser(Asked(Image() with { Bytes = null }))!;

        Assert.Equal("what is [Image #1 4×2]?\n[image unavailable: Image #1]", user["content"]!.GetValue<string>());
    }

    [Fact]
    public void Anthropic_GoneBytes_SendTheUnavailableNote()
    {
        var user = AnthropicUser(Asked(Image() with { Bytes = null }))!;

        Assert.Equal("what is [Image #1 4×2]?\n[image unavailable: Image #1]", user["content"]!.GetValue<string>());
    }

    /// <summary>The automatic cache marker moves to the newest block; with images in the message that is
    /// still the text block at its end.</summary>
    [Fact]
    public void Anthropic_TheCacheMarker_LandsOnTheTextBlock()
    {
        var body = AnthropicWire.BuildRequestBody("m", 100, Asked(Image()), null, stream: false, cache: true);
        var marked = body.ToJsonString();

        var content = body["messages"]![0]!["content"]!.AsArray();
        Assert.Null(content[0]!["cache_control"]);
        Assert.True(content[^1]!["cache_control"] is not null || marked.Contains("cache_control"),
            "no cache marker anywhere in the request");
    }

    [Fact]
    public void AMessageWithoutImages_IsSentAsBefore()
    {
        var plain = new List<ChatMessage> { new() { Role = "user", Content = "hi" } };

        Assert.Equal("hi", OpenAiUser(plain)!["content"]!.GetValue<string>());
        Assert.Equal("hi", AnthropicUser(plain)!["content"]!.GetValue<string>());
    }

    /// <summary>A JPEG goes as a JPEG: a model told PNG for JPEG bytes refuses the request.</summary>
    [Fact]
    public void AJpeg_IsSentWithItsOwnType()
    {
        var jpeg = Asked(Image() with { MediaType = "image/jpeg" });

        var openAi = OpenAiWire.BuildRequestBody("m", jpeg, null, stream: false).ToJsonString();
        var anthropic = AnthropicWire.BuildRequestBody("m", 100, jpeg, null, stream: false).ToJsonString();

        Assert.Contains("data:image/jpeg;base64,", openAi);
        Assert.Contains("\"media_type\":\"image/jpeg\"", anthropic);
    }
}

using System.Text.Json.Nodes;
using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The browser doing what the terminal does: the commands it may now run, the thinking level, asides, images, an
/// export, and a background command's output.
/// </summary>
public sealed partial class WebSessionHubTests
{
    private static LlmResponse Reply(string text) => new() { Text = text, StopReason = "end_turn" };

    // A 1×1 PNG, as a browser's paste hands one over.
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [Theory]
    [InlineData("/think")]
    [InlineData("/btw")]
    [InlineData("/agents")]
    [InlineData("/skills")]
    [InlineData("/commands")]
    [InlineData("/mcp")]
    [InlineData("/hooks")]
    [InlineData("/tasks")]
    [InlineData("/sessions")]
    public void The_terminal_s_own_commands_are_allowed_in_the_browser(string command) =>
        Assert.Null(CxAgent.Web.WebCommands.Refusal(command));

    [Fact]
    public void A_session_list_is_allowed_but_resuming_from_it_is_not()
    {
        // THE SIDEBAR RESUMES. "/sessions resume 2" typed in a tab would swap the conversation under every other tab
        // that has it open, which the sidebar's open never does.
        Assert.NotNull(CxAgent.Web.WebCommands.Refusal("/sessions resume 2"));
    }

    [Fact]
    public async Task The_panel_says_how_hard_the_model_thinks_and_whether_an_aside_is_open()
    {
        var (tab, _) = await CreatedAsync();
        await using var __ = tab;
        var panel = await tab.NextAsync(Type("panel"));
        Assert.NotNull(panel["thinking"]!["levels"]);
        Assert.False(panel["aside"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_btw_answer_reaches_the_browser_and_leaving_ends_the_aside()
    {
        _model.EnqueueResponse(Reply("main answer"));
        _model.EnqueueResponse(Reply("the aside's answer"));
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "hi")));
        await TurnDoneAsync(tab);

        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "/btw what did you say?")));
        Assert.Equal("what did you say?", (await tab.NextAsync(Kind("asideAsked")))["text"]!.GetValue<string>());
        Assert.Contains("the aside's answer", (await tab.NextAsync(Kind("asideText")))["text"]!.GetValue<string>(), StringComparison.Ordinal);
        await tab.NextAsync(Kind("asideEnded"));
        var session = _manager.Sessions.Single();
        Assert.True(session.InAside);

        await tab.SendAsync(Msg("leaveAside", ("sessionId", id)));
        await tab.NextAsync(m => m["type"]?.GetValue<string>() == "panel" && m["aside"]!.GetValue<bool>() == false);
        Assert.False(session.InAside);
    }

    [Fact]
    public async Task A_pasted_image_goes_with_the_message_and_is_shown_on_it()
    {
        _model.EnqueueResponse(Reply("I see it"));
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "what is this?"),
            ("images", new JsonArray(new JsonObject { ["name"] = "shot.png", ["data"] = OnePixelPng }))));

        var shown = await tab.NextAsync(Kind("images"));
        var image = shown["images"]!.AsArray().Single()!;
        Assert.StartsWith("[Image #1 1×1]", image["label"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith("data:image/png;base64,", image["src"]!.GetValue<string>(), StringComparison.Ordinal);
        await TurnDoneAsync(tab);
        Assert.Contains(_model.LastMessages!, m => m.Images is { Count: 1 });
    }

    [Fact]
    public async Task An_image_that_is_not_one_is_refused_and_nothing_is_sent()
    {
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "what is this?"),
            ("images", new JsonArray(new JsonObject { ["name"] = "x.png", ["data"] = Convert.ToBase64String("not a png"u8.ToArray()) }))));
        await tab.NextAsync(Type("notice"));
        Assert.Equal(0, _model.ChatCallCount);
    }

    [Fact]
    public async Task Export_hands_the_browser_the_conversation_as_markdown()
    {
        _model.EnqueueResponse(Reply("the answer"));
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "the question")));
        await TurnDoneAsync(tab);

        await tab.SendAsync(Msg("export", ("sessionId", id), ("full", false)));
        var export = await tab.NextAsync(Type("export"));
        Assert.EndsWith(".md", export["name"]!.GetValue<string>(), StringComparison.Ordinal);
        var markdown = export["markdown"]!.GetValue<string>();
        Assert.Contains("the question", markdown, StringComparison.Ordinal);
        Assert.Contains("the answer", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_thinking_level_the_model_does_not_offer_is_said_not_set()
    {
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "/think ultra")));
        await tab.NextAsync(Kind("said"));
    }
}

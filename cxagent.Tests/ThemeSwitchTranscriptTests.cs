using CxAgent.UI;
using SharpConsoleUI.Controls;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A theme switch recolours the conversation already on screen, in every session tab — not only the
/// messages that arrive after it, and not only in the tab being looked at.
/// </summary>
[Collection("file-tabs")]
public class ThemeSwitchTranscriptTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose()
    {
        // ColorScheme is static: put the default back so a later test does not inherit test-pale.
        ColorScheme.DeriveFrom(SharpConsoleUI.Themes.Theme.From(new SharpConsoleUI.Themes.ModernGrayTheme()).Build());
        _fixture.Dispose();
    }

    // Same construction as AThemeSwitch_RecoloursAnOpenEditor: only the window background differs,
    // and every surface is derived from it.
    //
    // EACH TEST SWITCHES TO DARK FIRST rather than assuming where it starts. ColorScheme is static,
    // and a test earlier in this collection can leave it on a pale theme — then the "before" message
    // is already pale and a switch to pale proves nothing.
    private static void SwitchTo(MainWindow main, string name, SharpConsoleUI.Color ground)
    {
        ColorScheme.DeriveFrom(SharpConsoleUI.Themes.Theme.From(new SharpConsoleUI.Themes.ModernGrayTheme())
            .WithName(name)
            .With(t => t.WindowBackgroundColor = ground)
            .Build());
        main.ReapplyTheme();
    }

    private static void SwitchToDark(MainWindow main) =>
        SwitchTo(main, "test-dark", new SharpConsoleUI.Color(0x0d, 0x0d, 0x0d));

    private static void SwitchToPale(MainWindow main) =>
        SwitchTo(main, "test-pale", new SharpConsoleUI.Color(0xf5, 0xf5, 0xf5));

    // The transcript's children are the message panels, in order; the newest is last.
    private static SharpConsoleUI.Color? LastBackground(ChatTranscriptControl chat) =>
        ((CollapsiblePanel)chat.Children[^1]).BackgroundColor;

    [Fact]
    public void AMessageAddedBeforeTheSwitch_TakesTheNewSurface()
    {
        var main = _fixture.Host.Main;
        SwitchToDark(main);
        main.Chat.AddMessage(ChatRole.User, "asked before the switch");
        var before = LastBackground(main.Chat);

        SwitchToPale(main);

        Assert.NotEqual(before, LastBackground(main.Chat));
        Assert.Equal(ColorScheme.UserSurface, LastBackground(main.Chat));
    }

    [Fact]
    public void ASessionTabNotBeingLookedAt_IsRecolouredToo()
    {
        var main = _fixture.Host.Main;
        SwitchToDark(main);
        var first = main.Chat;
        first.AddMessage(ChatRole.Assistant, "answered in the first tab");
        main.AddSessionTab(_fixture.Host.Session);   // the new tab becomes the active one

        SwitchToPale(main);

        Assert.NotSame(first, main.Chat);
        Assert.Equal(ColorScheme.AssistantSurface, LastBackground(first));
    }

    /// <summary>
    /// The composer of a tab not being looked at takes the new theme too: its surface, and the colour
    /// of a pasted token waiting in it. Only the active one was reapplied, so going back to a tab
    /// found its composer still in the theme before.
    /// </summary>
    [Fact]
    public void AComposerNotBeingLookedAt_IsRecolouredToo()
    {
        var main = _fixture.Host.Main;
        SwitchToDark(main);
        var firstTab = main.ActiveTab;
        var first = main.Input;
        main.AddSessionTab(_fixture.Host.Session);   // the new tab becomes the active one

        SwitchToPale(main);

        Assert.NotSame(first, main.Input);
        Assert.Equal(ColorScheme.ComposerSurface, first.InputBackgroundColor);
        Assert.Equal(ColorScheme.ComposerSurface, first.InputFocusedBackgroundColor);
        Assert.Equal(ColorScheme.AccentRgb, first.TokenForegroundColor);

        // AND THE BOX AROUND IT: its surface, the grip down its left edge, and the mode line under it.
        var grip = ColorScheme.Grip;
        Assert.Equal(ColorScheme.ComposerSurface, firstTab.PromptBox!.BackgroundColor);
        Assert.Contains($"#{grip.R:x2}{grip.G:x2}{grip.B:x2}", firstTab.Grip!.Text);
        Assert.Equal(ColorScheme.ComposerSurface, firstTab.ModeLine!.BackgroundColor);
    }
}

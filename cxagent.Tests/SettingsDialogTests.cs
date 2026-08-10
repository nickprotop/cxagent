using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Drivers;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Task 3: the Settings dialog SHELL — window, NavigationView scaffold, Save/Cancel, and the
/// result contract. Pages are stubs here (Tasks 4-5 fill them in); these tests only prove the
/// shell's structural promises: Save composes and writes exactly when the session is dirty,
/// Cancel (and a no-op Save) write nothing and resolve null.
///
/// Headless, no render loop: <see cref="Window.Close"/> only enters the async-thread-cleanup/
/// grace-period path when the window was actually RUN with its own thread
/// (Window.State.cs:126-175, `_windowThreadCts != null &amp;&amp; _windowTask != null`). A dialog built
/// here and never run has neither, so it falls through to the synchronous
/// "no async thread - delegate to CloseWindow" branch — same precedent as MainWindowTests.cs:15.
/// </summary>
public class SettingsDialogTests
{
    private static ConsoleWindowSystem Sys() =>
        new(new HeadlessConsoleDriver(80, 24),
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: true));

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-sd-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ProviderSettings OneProvider(string name, string model) =>
        ProviderCatalogEditor.AddOrReplace(
            ProviderCatalogEditor.EmptyCatalog(), name,
            new ProviderInstanceConfig("openai-compatible", model, "k", "https://x.invalid/v1", null),
            makeDefault: true);

    /// <summary>Two provider instances, "first"/"second", with the built-in reviewer role bound to
    /// ("first", "m1") — mirrors SettingsSessionTests' fixture, needed here to prove Save composes
    /// through roles untouched.</summary>
    /// <summary>Two provider instances, "first"/"second". Was TwoProvidersWithABoundReviewer,
    /// which bound the reviewer role to prove Save composed through roles untouched; roles are gone,
    /// and the Save-round-trip assertion it fed is still worth having.</summary>
    private static ProviderSettings TwoProviders() =>
        ProviderCatalogEditor.AddOrReplace(
            OneProvider("first", "m1"), "second",
            new ProviderInstanceConfig("openai-compatible", "m2", "k", "https://x.invalid/v1", null),
            makeDefault: false);

    private static SettingsDialog NewDialog(AppPaths paths, SettingsSession session,
        ConsoleWindowSystem? system = null)
    {
        var rules = new PermissionRulesStore(new AppPaths(MakeTempDir()));
        return new SettingsDialog(system ?? Sys(), null, paths, session, rules, MakeTempDir());
    }

    [Fact]
    public async Task Cancel_ReturnsNull_AndWritesNothing()
    {
        var paths = new AppPaths(MakeTempDir());
        ProviderConfigWriter.Write(paths, OneProvider("first", "m1"));
        var before = File.ReadAllText(Path.Combine(paths.ConfigDir, "config.json"));

        var dialog = NewDialog(paths, new SettingsSession(OneProvider("first", "m1")));
        var task = dialog.RunAsync(SettingsPage.Providers, CancellationToken.None);
        dialog.Cancel();

        Assert.Null(await task);
        Assert.Equal(before, File.ReadAllText(Path.Combine(paths.ConfigDir, "config.json")));
    }

    [Fact]
    public async Task SaveAfterAnEdit_ReturnsTheComposedSettings_AndDiskAgrees()
    {
        var paths = new AppPaths(MakeTempDir());
        ProviderConfigWriter.Write(paths, TwoProviders());
        var session = new SettingsSession(TwoProviders());
        session.UpdateCatalog(ProviderCatalogEditor.SetDefault(session.Working, "second"));

        var dialog = NewDialog(paths, session);
        var task = dialog.RunAsync(SettingsPage.Providers, CancellationToken.None);
        dialog.Save();

        var result = await task;
        Assert.Equal("second", result!.DefaultProvider);
        var loaded = ProviderConfigLoader.LoadAndValidate(paths, new Dictionary<string, string>());
        Assert.Equal("second", loaded.DefaultProvider);
    }

    [Fact]
    public async Task SaveWithNothingDirty_IsACancel()
    {
        // Null result is what gates the caller's WireRunner — a pristine Save must not re-wire:
        // a redundant re-wire tears down a running goal's schedulers and churns the transcript
        // sink for nothing (Decision 2; the I5 hazard for free). Also fixes F7's old habit of
        // re-wiring unconditionally on a no-edit save (AppBootstrap.cs:499).
        var paths = new AppPaths(MakeTempDir());
        var dialog = NewDialog(paths, new SettingsSession(OneProvider("first", "m1")));
        var task = dialog.RunAsync(SettingsPage.Providers, CancellationToken.None);
        dialog.Save();
        Assert.Null(await task);
        Assert.False(File.Exists(Path.Combine(paths.ConfigDir, "config.json")));
    }

    [Fact]
    public void RunAsync_ExposesEveryNavItem_ByName()
    {
        // Structural check the sibling-feature retro calls out explicitly: a shell test that only
        // asserts "it builds" would still pass with every page empty and every nav item missing.
        // This would fail if any AddItem call were dropped or misnamed.
        var paths = new AppPaths(MakeTempDir());
        var dialog = NewDialog(paths, new SettingsSession(OneProvider("first", "m1")));
        var window = dialog.Build();

        var nav = FindNavigationView(window);
        Assert.NotNull(nav);
        var names = nav!.Items.Select(i => i.Text).ToList();
        Assert.Contains("Providers", names);
        Assert.DoesNotContain("Roles", names);   // the Roles page went with the role system
        Assert.Contains("Orchestrator", names);
        Assert.Contains("Permissions", names);
    }

    private static SharpConsoleUI.Controls.NavigationView? FindNavigationView(SharpConsoleUI.Window window) =>
        window.GetControls().OfType<SharpConsoleUI.Controls.NavigationView>().FirstOrDefault();

    [Fact]
    public async Task OrchestratorEdit_ComposesIntoTheSavedSettings()
    {
        // The Orchestrator page prompts edit session.Working.Orchestrator via UpdateOrchestrator
        // (SettingsSession.cs:52-57), which only marks Dirty on an actual value change — so driving
        // it through UpdateOrchestrator directly (as the page's OnInputChanged handlers do) and then
        // Save is the same behavioral contract the page itself exercises, without needing a live
        // keystroke-simulation of PromptControl.
        var paths = new AppPaths(MakeTempDir());
        ProviderConfigWriter.Write(paths, OneProvider("first", "m1"));
        var session = new SettingsSession(OneProvider("first", "m1"));
        session.UpdateOrchestrator(session.Working.Orchestrator with { MaxWorkerTurns = 7 });

        var dialog = NewDialog(paths, session);
        var task = dialog.RunAsync(SettingsPage.Orchestrator, CancellationToken.None);
        dialog.Save();

        var result = await task;
        Assert.Equal(7, result!.Orchestrator.MaxWorkerTurns);
        var loaded = ProviderConfigLoader.LoadAndValidate(paths, new Dictionary<string, string>());
        Assert.Equal(7, loaded.Orchestrator.MaxWorkerTurns);
    }

    [Fact]
    public void OrchestratorPage_RendersPromptsSeededFromTheWorkingCopy()
    {
        // A shell test that only asserts "it builds" would still pass with an empty page (the same
        // trap RunAsync_ExposesEveryNavItem_ByName calls out for the nav items themselves). This
        // asserts the page actually seeded a prompt with the session's real MaxWorkerTurns value.
        var settings = OneProvider("first", "m1") with
        {
            Orchestrator = OrchestratorSettings.Unbounded with { MaxWorkerTurns = 13 },
        };
        var paths = new AppPaths(MakeTempDir());
        var dialog = NewDialog(paths, new SettingsSession(settings));
        var window = dialog.Build();
        var nav = FindNavigationView(window)!;

        dialog.SelectPage(SettingsPage.Orchestrator);
        var prompts = nav.ContentPanel.Children.OfType<SharpConsoleUI.Controls.PromptControl>().ToList();
        Assert.Contains(prompts, p => p.Input == "13");
    }

    [Fact]
    public void PermissionsPage_RendersTheStoresTrustState_InTheLiveWindow()
    {
        // Same "not just a shell" concern as the Orchestrator structural test above, but for the
        // read-only Permissions page: proves BuildPermissionsPage actually calls
        // PermissionsPageText.Build and paints its lines, not merely that the nav item exists.
        var dir = MakeTempDir();
        var rulesPaths = new AppPaths(MakeTempDir());
        var rules = new PermissionRulesStore(rulesPaths);
        rules.SetTrust(dir, TrustState.Trusted);

        var paths = new AppPaths(MakeTempDir());
        var session = new SettingsSession(OneProvider("first", "m1"));
        var dialog = new SettingsDialog(Sys(), null, paths, session, rules, dir);
        var window = dialog.Build();
        var nav = FindNavigationView(window)!;

        dialog.SelectPage(SettingsPage.Permissions);
        var markup = nav.ContentPanel.Children.OfType<SharpConsoleUI.Controls.MarkupControl>().First();
        var visible = string.Concat(SharpConsoleUI.Parsing.MarkupParser
            .Parse(markup.Text, SharpConsoleUI.Color.White, SharpConsoleUI.Color.Black)
            .Select(c => c.Character));
        Assert.Contains("Trusted", visible);
    }

    [Fact]
    public void PermissionsPage_NeverCallsAddOrSetTrust()
    {
        // Spec Decision 7: the page is read-only. Building and rendering it must not itself grant
        // or revoke anything — Save on this dialog only ever calls ProviderConfigWriter.Write
        // (SettingsDialog.cs:170), never anything on the PermissionRulesStore.
        var dir = MakeTempDir();
        var rulesPaths = new AppPaths(MakeTempDir());
        var rules = new PermissionRulesStore(rulesPaths);

        var paths = new AppPaths(MakeTempDir());
        var session = new SettingsSession(OneProvider("first", "m1"));
        var dialog = new SettingsDialog(Sys(), null, paths, session, rules, dir);
        var window = dialog.Build();
        dialog.SelectPage(SettingsPage.Permissions);

        Assert.False(File.Exists(Path.Combine(rulesPaths.ConfigDir, "permissions.json")));
    }

    [Fact]
    public void ProviderAndRoleRows_AreMarkupEscaped_SoBracketsStillRender()
    {
        // Defect P7: an unescaped '[' followed by text MarkupParser recognizes as a tag (a color name,
        // "bold", "dim", "/", …) is silently swallowed rather than rendered — the exact row the user
        // most needs to see, since "[MISSING instance]" (RoleEditor.cs:52) is a live '[' the moment a
        // binding goes stale. "red" is deliberately a REAL color-name tag (unlike an arbitrary bracketed
        // word, which MarkupParser passes through unchanged and would make this assertion pass whether
        // or not escaping ran) — verified directly against MarkupParser.Parse: "we[red]ird" renders as
        // "weird" unescaped, and back to "we[red]ird" once Escape() has run.
        var settings = OneProvider("we[red]ird", "m1");
        var rows = SettingsDialog.ProviderRowLabels(settings);          // pure static, used by the page
        var visible = string.Concat(SharpConsoleUI.Parsing.MarkupParser
            .Parse(rows[0], SharpConsoleUI.Color.White, SharpConsoleUI.Color.Black)
            .Select(c => c.Character));
        Assert.Contains("we[red]ird", visible);
    }

    [Theory]
    [InlineData(200, 50)]   // ultrawide
    [InlineData(100, 30)]
    [InlineData( 80, 24)]   // the common case
    [InlineData( 60, 20)]   // narrow: the width that must finally reach NavigationView's Compact mode
    [InlineData( 50, 18)]   // narrower still
    public void TheDialogFitsTheTerminal_AtEveryWidth_NotFixedAt76x24(int termW, int termH)
    {
        // The P14 live drive found this: with a hardcoded WithSize(76, 24) the dialog silently failed
        // to open or rendered clipped at every width below ~79, because a 76-wide window does not fit
        // a 70- or 60-column desktop. NavigationView's Compact/Minimal modes were unreachable at ANY
        // width as a result -- the thresholds were configured but nothing ever handed the control a
        // width low enough to trip them.
        //
        // Asserts the PROPERTY (it fits, and it scales) rather than exact numbers, because the sizing
        // is now Placement.Center(SizePreset.Large) -- the framework's own declarative placement,
        // which also re-resolves on desktop resize. Pinning 85%-of-desktop arithmetic here would just
        // re-implement the framework's formula in the test and break when it tunes it.
        var sys = new ConsoleWindowSystem(new HeadlessConsoleDriver(termW, termH),
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: true));
        var paths = new AppPaths(MakeTempDir());
        var window = NewDialog(paths, new SettingsSession(OneProvider("first", "m1")), sys).Build();

        Assert.True(window.Width  <= termW, $"dialog is {window.Width} wide on a {termW}-col terminal");
        Assert.True(window.Height <= termH, $"dialog is {window.Height} tall on a {termH}-row terminal");
        Assert.True(window.Width  > 0 && window.Height > 0, "dialog must have a real size");
    }

    [Fact]
    public void OnANarrowTerminal_TheDialogIsNarrowEnoughToReachCompactNav()
    {
        // The specific consequence the drive measured: no Compact/Minimal nav mode EVER appeared at
        // any width 50-79. NavigationView switches at WithExpandedThreshold(58) on the width it is
        // GIVEN, so this pins that a 60-col terminal actually hands it something under that -- the
        // link between "dialog fits" and "responsive nav works at all".
        var sys = new ConsoleWindowSystem(new HeadlessConsoleDriver(60, 20),
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: true));
        var paths = new AppPaths(MakeTempDir());
        var window = NewDialog(paths, new SettingsSession(OneProvider("first", "m1")), sys).Build();

        Assert.True(window.Width < 58,
            $"a 60-col terminal must give the nav less than its 58-col expanded threshold, got {window.Width}");
    }
    /// <summary>
    /// AN MCP COMMAND IS ARBITRARY USER TEXT — flags, paths, package names — so the same escaping
    /// defect bites harder here than on the Providers page. "[red]" is a REAL color-name tag, not an
    /// arbitrary bracketed word: MarkupParser passes the latter through unchanged, which would make
    /// this assertion pass whether or not escaping ran.
    /// </summary>
    [Fact]
    public void McpRows_AreMarkupEscaped_SoBracketsStillRender()
    {
        var settings = OneProvider("p", "m1") with
        {
            McpServers = new Dictionary<string, McpServerConfig>
            {
                ["srv"] = new(["npx", "-y", "we[red]ird-server"]),
            },
        };

        var rows = SettingsDialog.McpRowLabels(settings);
        var visible = string.Concat(SharpConsoleUI.Parsing.MarkupParser
            .Parse(rows[0], SharpConsoleUI.Color.White, SharpConsoleUI.Color.Black)
            .Select(c => c.Character));

        Assert.Contains("we[red]ird-server", visible);
    }

    /// <summary>
    /// The MCP nav item is reachable from its enum value. SelectPage matches items[i].Text against
    /// page.ToString(), so a page labelled "MCP" is unreachable from a value spelled "Mcp" — and F5
    /// deep-linking goes through exactly that path, which would fail silently.
    /// </summary>
    [Fact]
    public void TheMcpPage_IsReachableFromItsEnumValue()
    {
        Assert.Equal("MCP", SettingsPage.MCP.ToString());
    }
}

using CxAgent.Core.Llm;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Flows;
using SharpConsoleUI.Layout;
using Ctl = SharpConsoleUI.Builders.Controls;

namespace CxAgent.UI;

/// <summary>State threaded through the setup wizard's steps.</summary>
public sealed class WizardState
{
    public string InstanceName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? ApiKey { get; set; }
    public string? BaseUrl { get; set; }
    public string Model { get; set; } = "";
    public ProbeResult? Probe { get; set; }
}

public static partial class SetupWizard
{
    /// <summary>
    /// Pure projection of collected answers into ProviderSettings. Separated from RunAsync so the
    /// mapping is unit-testable without a UI host (RunAsync's flow needs a live window and is covered
    /// by the mandatory tmux drive instead).
    ///
    /// MERGES into <paramref name="existing"/> when one is supplied. This is what makes re-running the
    /// wizard (F5) ADD a provider instead of replacing the catalog. Building a fresh settings object
    /// from the answers alone drops every part of the existing config the wizard does not ask about,
    /// and the writer persists that emptiness faithfully.
    /// </summary>
    public static ProviderSettings BuildSettings(WizardState s, ProviderSettings? existing = null)
    {
        var baseline = existing ?? ProviderCatalogEditor.EmptyCatalog();

        // The preset a wizard answer came from is not carried in WizardState, so it is recovered by
        // matching kind + baseUrl — that is the only way OpenRouter's attribution headers survive.
        var preset = ProviderKindCatalog.Presets
            .FirstOrDefault(p => p.Kind == s.Kind && p.BaseUrl == s.BaseUrl);
        bool firstRun = baseline.Providers.Count == 0;

        var merged = ProviderCatalogEditor.AddOrReplace(baseline, s.InstanceName,
            new ProviderInstanceConfig(
                Kind: s.Kind,
                Model: s.Model,
                ApiKey: string.IsNullOrWhiteSpace(s.ApiKey) ? null : s.ApiKey,
                BaseUrl: string.IsNullOrWhiteSpace(s.BaseUrl) ? null : s.BaseUrl,
                ExtraHeaders: preset?.ExtraHeaders),
            makeDefault: firstRun);

        return merged;
    }

    /// <summary>
    /// Runs the 6-step first-run wizard. Returns the collected <see cref="ProviderSettings"/> on
    /// completion, or <c>null</c> when the user cancelled (or the flow faulted).
    /// </summary>
    /// <param name="existing">
    /// The catalog currently on disk, or <c>null</c> on a genuine first run (no config, or one that
    /// does not load). The wizard MERGES its answer into this, so pressing F5 adds a provider rather
    /// than replacing everything the user already configured. Only the final projection uses it — the
    /// probe and model steps deliberately build a throwaway single-provider registry.
    /// </param>
    /// <param name="deferredSave">
    /// When true, step 6's finish text says the result will be saved when the Settings dialog itself
    /// is saved, rather than "Saving this configuration." — this wizard writes nothing to disk itself
    /// (RunAsync never calls ProviderConfigWriter); the caller decides when to persist. The Providers
    /// page's "Add provider…" sub-flow passes true, because its result lands on the settings session's
    /// working copy (<see cref="SettingsSession.AbsorbWizardResult"/>) and is only written on the
    /// dialog's own Save. First-run/repair callers leave this false — for them, the wizard's return
    /// value IS what gets written immediately.
    /// </param>
    public static async Task<ProviderSettings?> RunAsync(
        ConsoleWindowSystem ws, Window? parent, CancellationToken ct = default,
        ProviderSettings? existing = null, bool deferredSave = false)
    {
        var state = new WizardState();

        var result = await Flow.Wizard<WizardState>()
            .Seed(state)
            .WithStepIndicator()
            .WithSeamlessHost()
            .WithTitle("cxagent setup")

            // 1) Welcome.
            .Step(async (ctx, s) =>
            {
                bool go = await ctx.Confirm(
                    "Welcome to cxagent",
                    "cxagent needs one LLM provider before it can run anything. "
                    + "This wizard collects the endpoint, credentials, and model, tests the connection, "
                    + "and writes the result to your config.",
                    "Begin", "Cancel");
                return go ? FlowVerdict.Next : FlowVerdict.Cancel;
            })

            // 2) Provider kind + instance name.
            .Step(async (ctx, s) =>
            {
                var kinds = ProviderKindCatalog.All;
                var chosen = await ctx.Show(
                    new ChoiceStepContent(
                        "Which provider do you want to use?",
                        kinds.Select(k => k.DisplayName).ToList()),
                    "Provider",
                    FlowButtons.None);
                if (chosen is null)
                    return FlowVerdict.Cancel;

                s.Kind = kinds.First(k => k.DisplayName == chosen).Kind;

                // The instance name is the key this provider is stored under in config.json and the
                // name the user refers to it by later; default it to the kind so a user who just
                // presses Enter still gets a working, sensibly-named config.
                var name = await ctx.Prompt(
                    "Name this provider",
                    "A short name for this provider instance (used in config and when routing):",
                    initial: s.Kind);
                if (name is null)
                    return FlowVerdict.Back;
                if (string.IsNullOrWhiteSpace(name))
                    return FlowVerdict.Stay;

                s.InstanceName = name.Trim();
                return FlowVerdict.Next;
            })

            // 3) Endpoint / key, driven by the kind's declared requirements.
            .Step(async (ctx, s) =>
            {
                var info = ProviderKindCatalog.For(s.Kind);

                if (info.RequiresBaseUrl)
                {
                    var url = await ctx.Prompt(
                        "Endpoint",
                        $"Base URL for {info.DisplayName}:",
                        initial: s.BaseUrl);
                    if (url is null)
                        return FlowVerdict.Back;
                    if (string.IsNullOrWhiteSpace(url))
                        return FlowVerdict.Stay;
                    s.BaseUrl = url.Trim();
                }

                if (info.RequiresApiKey)
                {
                    // Masked rather than ctx.Prompt: an API key must not be echoed in plain text.
                    var key = await ctx.Show(
                        new MaskedPromptStep("API key:"), "Credentials", FlowButtons.None);
                    if (string.IsNullOrWhiteSpace(key))
                        return FlowVerdict.Stay;
                    s.ApiKey = key;
                }

                return FlowVerdict.Next;
            })

            // 4) Test Connection.
            .Step(async (ctx, s) =>
            {
                s.Probe = await ctx.RunWithProgress(
                    "Test Connection",
                    "Contacting the provider…",
                    async (token, progress) =>
                    {
                        progress.Report($"Sending a test request to {s.Kind}…");
                        var registry = ProviderRegistry.Build(BuildSettings(s));
                        return await ProviderProbe.TestAsync(registry.Default, token);
                    });

                if (s.Probe is null)
                    return FlowVerdict.Stay; // progress dialog cancelled — re-run the test.

                // Gate on Reachable FIRST: ProbeResult.SupportsTools is false when unreachable, which
                // conflates "no tool support" with "could not tell". Reporting the tool-calling line on
                // the unreachable branch would tell a user with a typo'd key that their provider lacks
                // tool support — a wrong diagnosis. So the unreachable branch never mentions it.
                var msg = s.Probe!.Reachable
                    ? $"Connected. Tool calling: {(s.Probe.SupportsTools ? "supported" : "NOT supported")}"
                    : $"Could not reach the provider: {s.Probe.Error}\n(You can continue and fix this later in Settings.)";

                // Failure is not fatal — the user may simply be offline — but it must be stated plainly
                // and they must be able to go back and correct the endpoint or key.
                bool proceed = await ctx.Confirm("Test Connection", msg, "Continue", "Back");
                return proceed ? FlowVerdict.Next : FlowVerdict.Back;
            })

            // 5) Model — the SAME searchable picker the catalog editor uses. A flat ChoiceStepContent
            // here rendered every id as a button, so choosing an OpenRouter model meant scrolling
            // several hundred of them; the first-run wizard is the path every new user takes, so it
            // must not be the worse of the two routes to the same job.
            .Step(async (ctx, s) =>
            {
                var info = ProviderKindCatalog.For(s.Kind);
                // No baseline, deliberately: .Default must resolve to the instance being configured,
                // not to something already in the catalog on an F5 re-run.
                var provider = ProviderRegistry.Build(BuildSettings(s)).Default;
                var initial = string.IsNullOrWhiteSpace(s.Model) ? info.ModelHint : s.Model;

                // PickDetailedAsync, not PickAsync: this step needs DISMISS and EMPTY apart. PickAsync
                // collapses both to null, which would map an Escape (meaning "back, I typo'd the base
                // URL") onto Stay — re-showing the same dialog with no exit but cancelling the wizard.
                // ws/parent are closed over from RunAsync's own parameters: FlowContext holds its
                // ConsoleWindowSystem privately (_ws) with no public accessor, and SharpConsoleUI is
                // off-limits to this task, so the picker's host cannot be reached through ctx.
                // ctx.Token, not the outer ct: the flow's linked token, so cancelling the wizard also
                // aborts an in-flight model listing.
                var pick = await ModelPicker.PickDetailedAsync(ws, parent, provider, initial, ctx.Token);

                if (pick.Cancelled)
                    return FlowVerdict.Back;                 // dismissed → previous step
                if (string.IsNullOrWhiteSpace(pick.Model))
                    return FlowVerdict.Stay;                 // empty → re-ask

                s.Model = pick.Model.Trim();
                return FlowVerdict.Next;
            })

            // 6) Done.
            .Step(async (ctx, s) =>
            {
                var reach = s.Probe?.Reachable == true ? "reachable" : "not verified";

                // A single Finish button, NOT ctx.Confirm: past ctx.Commit()'s Back barrier there is
                // no "cancel" or "back" semantics left, so a two-button row would paint an affordance
                // that cannot do anything. ctx.Confirm always renders two buttons and FlowButtons.Ok
                // hardcodes the label "OK", so the one-button "Finish" row comes from self-resolving
                // content presented with FlowButtons.None.
                var finishNote = deferredSave
                    ? "This will be saved when you save Settings."
                    : "Saving this configuration.";
                await ctx.Show(
                    new ChoiceStepContent(
                        $"Provider '{s.InstanceName}' ({s.Kind})\nModel: {s.Model}\nConnection: {reach}\n\n"
                        + finishNote,
                        new[] { "Finish" }),
                    "Setup complete",
                    FlowButtons.None);

                ctx.Commit(); // Back barrier: the answers are final past this point.
                return FlowVerdict.Finish;
            })

            .Run(ws, parent, cancellationToken: ct);

        // Cancelled and Faulted both yield null — the caller treats "no settings" uniformly.
        // This is the ONLY BuildSettings call that merges: the probe (step 4) and model (step 5) steps
        // pass no baseline on purpose, so their throwaway registry's .Default is the instance being
        // configured rather than some pre-existing one.
        return result.Completed ? BuildSettings(result.Value!, existing) : null;
    }
}

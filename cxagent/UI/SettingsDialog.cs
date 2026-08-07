using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Ctl = SharpConsoleUI.Builders.Controls;

namespace CxAgent.UI;

/// <summary>The three pages of the consolidated Settings dialog. Nav item order (Providers,
/// Orchestrator, Permissions) mirrors the enum's declaration order.
///
/// <para>There was a fourth, Roles. Roles encoded capability as identity, and the orchestrator had
/// to pick the identity before it knew what the work would need; it could not, and the failures
/// came faster than they could be guarded.</para></summary>
public enum SettingsPage { Providers, Orchestrator, Permissions }

/// <summary>
/// The consolidated Settings dialog: one NavigationView-based window replacing the three
/// independent F5/F7/F8 editors. Pattern copied from cxfiles' <c>OptionsModal</c> (working-copy
/// state, fluent NavigationView, sticky Save/Cancel row) — cxfiles' <c>ModalBase</c> itself is
/// cxfiles-local and is NOT ported; this class talks to <see cref="SharpConsoleUI"/> directly, per
/// spec Decision 6.
///
/// Nothing is persisted until <see cref="Save"/>. All four pages share one <see cref="SettingsSession"/>
/// working copy (built by the caller from whatever is on disk); <see cref="SettingsSession.TryCompose"/>
/// is the single source of truth for "did anything actually change" — a null compose is treated the
/// same as an explicit Cancel: nothing is written, and <see cref="RunAsync"/> resolves null, which is
/// exactly the signal the caller uses to skip a needless WireRunner re-wire (see SettingsSession's own
/// doc comment for why that matters — a redundant re-wire tears down a running goal's schedulers).
///
/// This task builds the SHELL only: the four pages are one-line Markup stubs. Tasks 4-5 fill them in
/// via the same <c>panel =&gt; BuildXPage(panel)</c> seam OptionsModal uses; Task 6 wires the F5/F7/F8
/// global shortcuts to construct this dialog and route to <see cref="SelectPage"/>.
///
/// No <c>OnKeyPressed</c> Escape handler here: it would never fire (Decision 1 —
/// <c>InputCoordinator.cs:131</c> intercepts Escape before a window handler sees it). Escape is
/// routed to <see cref="Cancel"/> by Task 6's global handler instead.
/// </summary>
public sealed class SettingsDialog
{
    private readonly ConsoleWindowSystem _system;
    private readonly Window? _parent;
    private readonly AppPaths _paths;
    private readonly SettingsSession _session;
    private readonly PermissionRulesStore _rules;
    private readonly string _workingDir;

    // TCS discipline copied from cxfiles' ModalBase.cs:22-49: created before the window is shown,
    // resolved exactly once via TrySetResult (Save/Cancel each try first), with the window's
    // OnClosed handler resolving null as a fallback for any close this class didn't itself drive
    // (the title-bar close button, Ctrl+W, etc.) — TrySetResult is a no-op if Save/Cancel already won.
    private TaskCompletionSource<ProviderSettings?>? _tcs;
    private Window? _window;
    private NavigationView? _nav;

    public SettingsDialog(ConsoleWindowSystem ws, Window? parent, AppPaths paths,
        SettingsSession session, PermissionRulesStore rules, string workingDir)
    {
        _system = ws;
        _parent = parent;
        _paths = paths;
        _session = session;
        _rules = rules;
        _workingDir = workingDir;
    }

    /// <summary>Builds the window WITHOUT showing it — a test-only seam for asserting on structure
    /// (nav items, etc.) without going through the TCS/show/close lifecycle. Production code should
    /// use <see cref="RunAsync"/>, which calls this internally.</summary>
    public Window Build()
    {
        if (_window is not null) return _window;

        var providersColor = new SharpConsoleUI.Color(0, 200, 255);

        var nav = Ctl.NavigationView()
            .WithNavWidth(20)
            .WithExpandedThreshold(58)
            .WithPaneHeader("[bold cyan]Settings[/]")
            .WithContentBorder(BorderStyle.Rounded)
            .WithContentPadding(1, 0, 1, 0)
            .AddHeader("Settings", providersColor, header => header
                .AddItem("Providers", subtitle: "Instances and default",
                    content: panel => BuildProvidersPage(panel))
                .AddItem("Orchestrator", subtitle: "Consult and edit limits",
                    content: panel => BuildOrchestratorPage(panel))
                .AddItem("Permissions", subtitle: "Always-allow rules",
                    content: panel => BuildPermissionsPage(panel)))
            .Fill()
            .Build();
        _nav = nav;

        var saveBtn = Ctl.Button("  Save  ")
            .OnClick((_, _) => Save())
            .Build();
        var cancelBtn = Ctl.Button(" Cancel ")
            .OnClick((_, _) => Cancel())
            .Build();
        var buttons = HorizontalGridControl.ButtonRow(saveBtn, cancelBtn);
        buttons.StickyPosition = StickyPosition.Bottom;
        buttons.Margin = new Margin(1, 0, 1, 0);

        var builder = new WindowBuilder(_system)
            .WithTitle("Settings")
            .AsModal()
            .Centered()
            .Minimizable(false)
            // Declarative placement, NOT a fixed size. This was `WithSize(76, 24)`, and the P14 live
            // drive found the consequence: below ~79 columns the dialog silently failed to open or
            // rendered clipped, because a 76-wide window does not fit a 70- or 60-column desktop.
            // NavigationView's Compact/Minimal modes were therefore unreachable at EVERY width — the
            // responsive thresholds were configured (WithExpandedThreshold(58) above) but nothing ever
            // handed the control a width low enough to trip them.
            //
            // Center(Large) is 85% of the usable desktop in each dimension, and the framework
            // "re-resolves it against the live usable desktop when the window is built and RE-RESOLVES
            // ON DESKTOP RESIZE" (WindowBuilder.WithPlacement). That second half matters: the same
            // drive found repaint corruption when resizing the terminal with the dialog open, which a
            // one-shot clamp computed at Build() time would not have fixed. Preferred over
            // hand-clamping DesktopDimensions for exactly that reason — the framework already owns
            // this, including the resize path.
            .WithPlacement(Placement.Center(SizePreset.Large))
            .WithBorderStyle(BorderStyle.Rounded)
            .AddControls(nav, buttons)
            .OnClosed((_, _) => _tcs?.TrySetResult(null));

        if (_parent is not null)
            builder = builder.WithParent(_parent);

        _window = builder.Build();
        return _window;
    }

    /// <summary>Shows the dialog and completes when it closes: the saved settings, or null on
    /// cancel/dismiss (nothing written in that case). The CALLER re-wires — same contract as
    /// ProviderCatalogEditor.RunAsync had, so single-save/single-re-wire stays structural.</summary>
    public Task<ProviderSettings?> RunAsync(SettingsPage initialPage, CancellationToken ct)
    {
        _tcs = new TaskCompletionSource<ProviderSettings?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var window = Build();
        _system.AddWindow(window);

        SelectPage(initialPage);

        if (ct.CanBeCanceled)
            ct.Register(() => _tcs?.TrySetResult(null));

        return _tcs.Task;
    }

    /// <summary>Reentrant F5/F7/F8 route here (globals fire first): selects the given page in the
    /// already-open dialog rather than opening a second one. Public for the same reason
    /// <see cref="Save"/>/<see cref="Cancel"/> are.</summary>
    public void SelectPage(SettingsPage page)
    {
        if (_nav is null) return;
        var target = page.ToString();
        var items = _nav.Items;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Text == target)
            {
                _nav.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>Save button; public so an outside global handler can drive it (ApproveDraft/
    /// DiscardDraft are public for the same reason).</summary>
    public void Save()
    {
        var composed = _session.TryCompose();
        if (composed is null)
        {
            // Nothing changed: behave exactly as Cancel (SaveWithNothingDirty_IsACancel) — no
            // write, no re-wire signal.
            Cancel();
            return;
        }

        ProviderConfigWriter.Write(_paths, composed);
        _tcs?.TrySetResult(composed);
        _window?.Close();
    }

    public void Cancel()
    {
        _tcs?.TrySetResult(null);
        _window?.Close();
    }

    /// <summary>
    /// One escaped Markup-ready line per configured instance — the pure half of the Providers page,
    /// split out so row escaping (P7) is unit-testable without a live window. Escaping the WHOLE line
    /// (not just the name) is correct here because <see cref="ProviderCatalogEditor.DescribeRows"/>'s
    /// Line already interleaves the name with punctuation this method does not need to parse back out.
    /// </summary>
    public static IReadOnlyList<string> ProviderRowLabels(ProviderSettings settings) =>
        ProviderCatalogEditor.DescribeRows(settings)
            .Select(r => SharpConsoleUI.Parsing.MarkupParser.Escape(r.Line))
            .ToList();

    private void BuildProvidersPage(ScrollablePanelControl panel)
    {
        var settings = _session.Working;
        var rows = ProviderCatalogEditor.DescribeRows(settings);

        for (int i = 0; i < rows.Count; i++)
        {
            var name = rows[i].Name;
            var label = SharpConsoleUI.Parsing.MarkupParser.Escape(rows[i].Line);
            var btn = Ctl.Button(label).WithMargin(1, 0, 1, 0).Build();
            btn.Click += (_, _) => _ = HandleEditInstanceAsync(name);
            panel.AddControl(btn);
        }

        var addBtn = Ctl.Button(SharpConsoleUI.Parsing.MarkupParser.Escape("[ Add provider… ]"))
            .WithMargin(1, 0, 1, 0).Build();
        addBtn.Click += (_, _) => _ = HandleAddProviderAsync();
        panel.AddControl(addBtn);

        if (settings.Providers.Count > 0)
        {
            var defaultBtn = Ctl.Button(SharpConsoleUI.Parsing.MarkupParser.Escape("[ Set default… ]"))
                .WithMargin(1, 0, 1, 0).Build();
            defaultBtn.Click += (_, _) => _ = HandleSetDefaultAsync();
            panel.AddControl(defaultBtn);
        }
    }

    /// <summary>Prompts + a checkbox over <c>session.Working.Orchestrator</c> — cxfiles'
    /// OptionsModal field pattern (<c>Controls.Prompt().WithInput(...).OnInputChanged(parse-and-store)</c>):
    /// every accepted keystroke composes a full <see cref="OrchestratorSettings"/> and pushes it
    /// through <see cref="SettingsSession.UpdateOrchestrator"/>, which is itself a no-op (no Dirty)
    /// when the composed value doesn't actually differ from what's already there.
    ///
    /// MaxTokensPerCall/GoalTokenBudget/ContextCompressThreshold: blank is a valid, meaningful
    /// state (unbounded / unbounded / derive-from-context-window — see the record's own doc
    /// comment), so an empty box parses to null rather than being rejected.
    ///
    /// MaxConsults/MaxEditsPerJob/MaxWorkerTurns: never-null real defaults (ProviderConfig.cs:29-38)
    /// — a blank or non-positive entry is rejected outright (the box's last-accepted value is kept)
    /// rather than silently coerced to some other number, so a mistyped field can't reopen the
    /// runaway loop these caps exist to stop.</summary>
    private void BuildOrchestratorPage(ScrollablePanelControl panel)
    {
        var o = _session.Working.Orchestrator;

        panel.AddControl(Ctl.Markup().AddLine("[dim]Blank = unbounded for the two token fields below.[/]").Build());
        AddNullableIntPrompt(panel, "Max tokens/call", o.MaxTokensPerCall,
            v => _session.UpdateOrchestrator(_session.Working.Orchestrator with { MaxTokensPerCall = v }));
        AddNullableIntPrompt(panel, "Goal token budget", o.GoalTokenBudget,
            v => _session.UpdateOrchestrator(_session.Working.Orchestrator with { GoalTokenBudget = v }));
        AddNullableIntPrompt(panel, "Compress threshold (blank = derive from context window)", o.ContextCompressThreshold,
            v => _session.UpdateOrchestrator(_session.Working.Orchestrator with { ContextCompressThreshold = v }));

        panel.AddControl(Ctl.Markup().AddLine("[dim]The three below always need a real value (>= 1).[/]").Build());
        AddPositiveIntPrompt(panel, "Max consults", o.MaxConsults,
            v => _session.UpdateOrchestrator(_session.Working.Orchestrator with { MaxConsults = v }));
        AddPositiveIntPrompt(panel, "Max edits/job", o.MaxEditsPerJob,
            v => _session.UpdateOrchestrator(_session.Working.Orchestrator with { MaxEditsPerJob = v }));
        AddPositiveIntPrompt(panel, "Max worker turns", o.MaxWorkerTurns,
            v => _session.UpdateOrchestrator(_session.Working.Orchestrator with { MaxWorkerTurns = v }));

        var copilot = Ctl.Checkbox("Copilot (goals start as a draft for approval)")
            .Checked(o.Copilot)
            .WithMargin(1, 0, 1, 0)
            .Build();
        copilot.CheckedChanged += (_, isChecked) =>
            _session.UpdateOrchestrator(_session.Working.Orchestrator with { Copilot = isChecked });
        panel.AddControl(copilot);
    }

    /// <summary>A prompt whose blank input means "unconfigured" (null) rather than being rejected —
    /// for MaxTokensPerCall/GoalTokenBudget/ContextCompressThreshold, where null is a real,
    /// meaningful state. A non-blank, non-numeric entry is ignored (last-accepted value stands);
    /// there is no error UI in this dialog to surface a rejection.</summary>
    private static void AddNullableIntPrompt(ScrollablePanelControl panel, string label, int? initial, Action<int?> onAccepted)
    {
        var prompt = Ctl.Prompt(label)
            .WithInput(initial?.ToString() ?? string.Empty)
            .WithMargin(1, 0, 1, 0)
            .Build();
        prompt.InputChanged += (_, text) =>
        {
            if (string.IsNullOrWhiteSpace(text)) { onAccepted(null); return; }
            if (int.TryParse(text.Trim(), out var parsed)) onAccepted(parsed);
        };
        panel.AddControl(prompt);
    }

    /// <summary>A prompt for a field that must always hold a real value >= 1 (MaxConsults/
    /// MaxEditsPerJob/MaxWorkerTurns — never-null real defaults, ProviderConfig.cs:29-38). Blank or
    /// non-positive input is rejected — the session keeps whatever value it already had — rather
    /// than silently substituting some other number, since these caps exist specifically to stop a
    /// runaway consult/edit/tool loop.</summary>
    private static void AddPositiveIntPrompt(ScrollablePanelControl panel, string label, int initial, Action<int> onAccepted)
    {
        var prompt = Ctl.Prompt(label)
            .WithInput(initial.ToString())
            .WithMargin(1, 0, 1, 0)
            .Build();
        prompt.InputChanged += (_, text) =>
        {
            if (int.TryParse(text.Trim(), out var parsed) && parsed >= 1) onAccepted(parsed);
        };
        panel.AddControl(prompt);
    }

    /// <summary>Read-only (spec Decision 7): renders <see cref="PermissionsPageText.Build"/>'s
    /// escaped lines and nothing else. Never calls <c>Add</c>/<c>SetTrust</c> — Save must never
    /// touch permissions.json. Revoke needs store methods a separate plan (P13) owns; the page says
    /// so on screen (see PermissionsPageText's own doc comment for the full reasoning).</summary>
    private void BuildPermissionsPage(ScrollablePanelControl panel)
    {
        var markup = Ctl.Markup();
        foreach (var line in PermissionsPageText.Build(_rules, _workingDir))
            markup.AddLine(line);
        panel.AddControl(markup.WithMargin(1, 0, 1, 0).Build());
    }

    /// <summary>Re-applies the Providers page content for the current session state — the refresh
    /// idiom every provider/role sub-flow uses after a change (NavigationView.Items.cs:526-541).</summary>
    private void RefreshProvidersPage()
    {
        if (_nav is null) return;
        var item = _nav.Items.FirstOrDefault(i => i.Text == nameof(SettingsPage.Providers));
        if (item is not null) _nav.SetItemContent(item, BuildProvidersPage);
    }

    private async Task HandleEditInstanceAsync(string name)
    {
        var updated = await ProviderCatalogEditor.EditInstanceAsync(_system, _window, _session.Working, name, CancellationToken.None);
        if (updated is not null)
        {
            _session.UpdateCatalog(updated);
            RefreshProvidersPage();
        }
    }

    private async Task HandleAddProviderAsync()
    {
        // deferredSave: true — the wizard's result must land on THIS session's working copy, not on
        // disk (SettingsSession.AbsorbWizardResult; pinned by SettingsSessionTests'
        // WizardMerge_LandsOnUnsavedEdits_NotOnDisk). Snapshot(), not Working, as the baseline: it
        // folds in any unsaved role edits so the wizard's merge does not silently revert them.
        var merged = await SetupWizard.RunAsync(_system, _window, CancellationToken.None, _session.Snapshot(), deferredSave: true);
        if (merged is not null)
        {
            _session.AbsorbWizardResult(merged);
            RefreshProvidersPage();
        }
    }

    private async Task HandleSetDefaultAsync()
    {
        var chosen = await FlowDialogs.ChooseAsync(
            _system, _window, "Default provider", _session.Working.Providers.Keys.ToList(), CancellationToken.None);
        if (chosen is not null && chosen != _session.Working.DefaultProvider)
        {
            _session.UpdateCatalog(ProviderCatalogEditor.SetDefault(_session.Working, chosen));
            RefreshProvidersPage();
        }
    }

}

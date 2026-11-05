using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI.Config;

/// <summary>
/// The Project tab: the repository this tab works in, whether it is trusted, and what it brings —
/// instruction files, skills, commands, plugins, hooks.
///
/// <para>READ-ONLY BUT FOR TRUST. Each of the things listed is edited where it lives (a file, a folder, the
/// Hooks tab); this is the one place they are seen together, and trust is here because it decides what of
/// the list takes effect.</para>
/// </summary>
public sealed class ProjectTab
{
    private readonly ConfigDialogHost _host;
    private readonly Action _openHooks;
    private readonly ScrollablePanelControl _root;

    public ProjectTab(ConfigDialogHost host, Action openHooks)
    {
        _host = host;
        _openHooks = openHooks;
        _root = new ScrollablePanelControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill,
            Margin = new Margin(1, 0, 1, 0),
        };
        Refresh();
    }

    public IWindowControl Content => _root;

    /// <summary>Nothing to save: the one setting here, trust, takes effect the moment it is chosen.</summary>
    public IReadOnlyList<ButtonControl> Actions => [];

    private Session Session => _host.ActiveSession();

    public void Refresh()
    {
        _root.ClearContents();
        var session = Session;
        var policy = session.Policy;
        var summary = ProjectSummary.For(new ProjectFacts(session.WorkingDirectory, policy?.Trust ?? TrustState.Unknown,
            session.Catalog(), session.Hooks?.Snapshot ?? Core.Hooks.HookSnapshot.Empty));
        var muted = ColorScheme.MutedMarkup;

        if (summary.RepoRoot is null)
        {
            _root.AddControl(ListDetail.Empty("No repository here", $"{session.WorkingDirectory} is not inside a git repository, so nothing is read from it as a project."));
            return;
        }

        _root.AddControl(new MarkupControl([
            $"[bold]{MarkupParser.Escape(Path.GetFileName(summary.RepoRoot))}[/]",
            $"[{muted}]{MarkupParser.Escape(summary.RepoRoot)}[/]",
            "",
        ]) { Wrap = true });

        var trust = new FieldGrid();
        var choices = new List<string> { "Trusted", "Not trusted" };
        var box = new DropdownControl("", choices) { HorizontalAlignment = HorizontalAlignment.Left };
        box.SelectedIndex = summary.Trust == TrustState.Trusted ? 0 : 1;
        box.SelectedValueChanged += (_, v) =>
        {
            var want = v == "Trusted";
            if (want == (Session.Policy?.FolderTrusted == true)) return;
            // THE SAME PATH AS /trust, so the answer is stored, announced and applied exactly as there.
            Session.SetTrust(want ? "yes" : "no");
            _host.System.EnqueueOnUIThread(Refresh, "projectTab.Trust");
        };
        trust.Add("Trust", summary.Trust == TrustState.Trusted
            ? "Reads and writes inside it are not asked about, and its hooks run."
            : "Every file operation asks, and its hooks do not run.", box);
        _root.AddControl(ListDetail.Section("This folder", trust.Control));
        _root.AddControl(ListDetail.Gap());

        var brings = new FieldGrid();
        foreach (var item in summary.Items)
        {
            var lines = new List<string>
            {
                item.Count == 0
                    ? $"[{muted}]none[/]"
                    : $"[bold]{item.Count}[/]" + (item.State is { } state
                        ? $"  [{(state.StartsWith("not", StringComparison.Ordinal) ? ColorScheme.CautionMarkup : muted)}]{MarkupParser.Escape(state)}[/]"
                        : ""),
            };
            if (item.Names.Count > 0)
                lines.Add($"[{muted}]{MarkupParser.Escape(string.Join(", ", item.Names.Take(8)))}{(item.Names.Count > 8 ? ", …" : "")}[/]");
            IWindowControl value = new MarkupControl(lines) { Wrap = true };
            if (item.Kind == "Hooks")
            {
                var stack = new FieldGrid();
                stack.AddBare(value);
                stack.AddBare(Controls.Button(" Open Hooks ").OnClick((_, _) => _openHooks()).Build());
                value = stack.Control;
            }
            brings.Add(item.Kind, ListDetail.ElidePath(item.Folder, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 60), value);
        }
        _root.AddControl(ListDetail.Section("What it brings", brings.Control));
    }
}

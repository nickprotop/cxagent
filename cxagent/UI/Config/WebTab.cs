using CxAgent.Core.Config;
using CxAgent.Core.Web;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI.Config;

/// <summary>
/// The Web tab: config.json's <c>web</c> block — where the browser front end listens, who may log in, and which
/// folders a browser may open.
///
/// <para>ONE PAGE, LIKE GENERAL: nothing to choose between, so it is the detail pane alone, in the panels the other tabs
/// use, each choice with what it costs under it.</para>
///
/// <para>READ WHEN THE SERVER STARTS. A save changes what the next <c>/web</c> or <c>cxagent web</c> uses; a server
/// already running keeps its settings, and the status line at the top says which one this is.</para>
///
/// <para>PASSWORDS GO ONE WAY. They are typed into masked fields, stored as a hash, and never shown — a user is listed by
/// name, marked when config.json still holds its password in plain text.</para>
/// </summary>
public sealed class WebTab
{
    private readonly ConfigDialogHost _host;
    private readonly Func<Window?> _window;
    private readonly GridControl _root;
    private readonly ScrollablePanelControl _detail;
    private readonly MarkupControl _status;
    private readonly ButtonControl _revert;
    private readonly ButtonControl _save;
    private WebDraft? _draft;
    private bool _saving;
    private Action? _redrawUsers;

    public WebTab(ConfigDialogHost host, Func<Window?> window)
    {
        _host = host;
        _window = window;

        _root = new GridControl { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill };
        _root.ColumnDefinitions.Add(GridLength.Star(1));
        _root.RowDefinitions.Add(GridLength.Star(1));
        _root.RowDefinitions.Add(GridLength.Auto());
        _detail = new ScrollablePanelControl { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill, Margin = ListDetail.DetailInset };
        _root.Place(_detail, 0, 0);
        _status = new MarkupControl([""]) { Wrap = true, Margin = ListDetail.DetailInset };
        _root.Place(_status, 1, 0);

        // REVERT AND SAVE BELONG TO THE TAB, SHOWN IN THE DIALOG'S FOOTER while this tab is in front.
        _revert = Controls.Button(" Revert ").OnClick((_, _) => Reload()).Build();
        _save = DialogLook.MainButton(Controls.Button(" Save ").OnClick((_, _) => _ = SaveAsync()).Build(), main: false);

        Reload();
    }

    public IWindowControl Content => _root;

    /// <summary>Revert and Save, for the dialog's footer to host while this tab is in front.</summary>
    public IReadOnlyList<ButtonControl> Actions => [_revert, _save];

    /// <summary>The accent on Save only when there is something to save.</summary>
    public void MarkSave() => DialogLook.MainButton(_save, _draft?.IsDirty == true);

    public bool HasUnsaved => _draft?.IsDirty == true;
    public void SaveFromKey() => _ = SaveAsync();

    /// <summary>The pane rebuilt from config.json as it is now — what Revert and a save both come back to.</summary>
    public void Reload()
    {
        var read = ProvidersFile.Read(_host.Apply.Paths, _host.Apply.Env);
        _detail.ClearContents();
        if (read.Settings is not { } settings)
        {
            _draft = null;
            SetStatus(string.Join("\n", read.Errors.Select(e => $"[{ColorScheme.DangerMarkup}]✖ {MarkupParser.Escape(e)}[/]")));
            MarkSave();
            return;
        }
        _draft = WebDraft.From(settings);
        Fill(_draft);
        SetStatus("");
        MarkSave();
    }

    private int PaneWidth() => (_window()?.Width ?? Console.WindowWidth) - 2 - 4 - 1 - 1;

    private void Fill(WebDraft draft)
    {
        var muted = ColorScheme.MutedMarkup;
        _detail.AddControl(ListDetail.Header("Web", "Your sessions in a browser — saved to config.json, read when /web or cxagent web starts", PaneWidth()).Control);

        MarkupControl Help(string text) => new([$"[{muted}]    {MarkupParser.Escape(text)}[/]"]) { Wrap = true };
        MarkupControl Label(string text) => new([$"[bold]{MarkupParser.Escape(text)}[/]"]);
        MarkupControl Blank() => new([""]);

        // ---- serving ------------------------------------------------------------------------------------------
        var serving = new FieldGrid();
        serving.AddBare(new MarkupControl([ServingLine()]) { Wrap = true });
        serving.AddBare(Blank());
        var port = ListDetail.Input(new PromptControl { Input = draft.PortText, InputWidth = 6 });
        port.InputChanged += (_, t) => { draft.PortText = t; MarkSave(); };
        serving.Add("Port", "5180 unless something else uses it.", port);
        var bind = ListDetail.Input(new PromptControl { Input = draft.Bind, InputWidth = 16 });
        bind.InputChanged += (_, t) => { draft.Bind = t; MarkSave(); };
        serving.Add("Listen on", "127.0.0.1 is this machine only; 0.0.0.0 is every interface, your LAN included.", bind);
        serving.AddBare(Blank());
        var tls = new RadioGroup<bool>();
        serving.AddBare(Label("Transport"));
        serving.AddBare(new RadioControl<bool>(tls, false, "Plain HTTP"));
        serving.AddBare(Help("Fine on this machine. Beyond it, passwords and replies travel unencrypted — put nginx, Caddy or Tailscale in front."));
        serving.AddBare(Blank());
        serving.AddBare(new RadioControl<bool>(tls, true, "HTTPS, self-signed"));
        serving.AddBare(Help("A certificate made once and kept in the config folder; a browser warns once, then remembers."));
        tls.SelectedValue = draft.SelfSigned;
        tls.SelectionChanged += (_, on) => { draft.SelfSigned = on; MarkSave(); };
        _detail.AddControl(ListDetail.Section("Serving", serving.Control));
        _detail.AddControl(ListDetail.Gap());

        // ---- login --------------------------------------------------------------------------------------------
        var login = new FieldGrid();
        login.AddBare(new MarkupControl([$"[{ColorScheme.CautionMarkup}]A login is a shell on this machine: whoever gets in drives an agent that runs commands here.[/]"]) { Wrap = true });
        login.AddBare(Blank());
        var mode = new RadioGroup<LoginMode>();
        login.AddBare(Label("Who must log in"));
        login.AddBare(new RadioControl<LoginMode>(mode, LoginMode.Auto, "Anyone not on this machine"));
        login.AddBare(Help("No login on 127.0.0.1; required as soon as it listens anywhere else. Behind a reverse proxy, choose Everyone — the proxy connects from this machine."));
        login.AddBare(Blank());
        login.AddBare(new RadioControl<LoginMode>(mode, LoginMode.Always, "Everyone"));
        login.AddBare(Help("Required on this machine too — for a machine others log in to, or a proxy in front."));
        login.AddBare(Blank());
        login.AddBare(new RadioControl<LoginMode>(mode, LoginMode.Off, "No one"));
        login.AddBare(Help("Anyone who reaches the port gets in. Said on the console and on every page, every time."));
        mode.SelectedValue = draft.Login;
        mode.SelectionChanged += (_, m) => { draft.Login = m; MarkSave(); };
        login.AddBare(Blank());
        login.AddBare(Label("Users"));
        login.AddBare(UsersEditor(draft, Help));
        _detail.AddControl(ListDetail.Section("Login", login.Control));
        _detail.AddControl(ListDetail.Gap());

        // ---- folders ------------------------------------------------------------------------------------------
        var folders = new FieldGrid();
        var roots = new MultilineEditControl(viewportHeight: 3) { HorizontalAlignment = HorizontalAlignment.Stretch };
        roots.SetContent(draft.RootsText);
        roots.ContentChanged += (_, _) => { draft.RootsText = roots.GetContent(); MarkSave(); };
        folders.Add("Open sessions under", "Folders, one per line, starting with / or ~. Empty: the folder the server starts in. Enter to type, Esc when done.", roots);
        folders.AddBare(Blank());
        var idle = ListDetail.Input(new PromptControl { Input = draft.IdleText, InputWidth = 6 });
        idle.InputChanged += (_, t) => { draft.IdleText = t; MarkSave(); };
        folders.Add("Unload after (minutes)", "A session no tab has open, with no turn running and nothing waiting on you. 0 keeps it. cxagent web only; the terminal's own sessions never unload.", idle);
        _detail.AddControl(ListDetail.Section("Sessions", folders.Control));
        _detail.AddControl(ListDetail.Gap());

        // ---- browser access -------------------------------------------------------------------------------------
        // HERE AND ONLY HERE: a browser shows these read-only and can never change them, even with config on, so a
        // login cannot widen its own access.
        var access = new FieldGrid();
        access.AddBare(new MarkupControl([$"[{ColorScheme.MutedMarkup}]What a browser may do beyond the conversation. A browser shows these but can never change them.[/]"]) { Wrap = true });
        access.AddBare(Blank());
        void Switch(string label, string help, bool on, Action<bool> set)
        {
            var box = new CheckboxControl(label, on);
            box.CheckedChanged += (_, value) => { set(value); MarkSave(); };
            access.AddBare(box);
            access.AddBare(Help(help));
            access.AddBare(Blank());
        }
        Switch("Plugins", "The marketplace: browse, install, load. Each plugin still asks before it loads, showing what it would run.",
            draft.AllowPlugins, v => draft.AllowPlugins = v);
        Switch("Open files anywhere", "The editor beyond the folders above: any file you can read and write.",
            draft.AllowOpenAnywhere, v => draft.AllowOpenAnywhere = v);
        Switch("Shell", "A real terminal in a browser tab. It runs as you, outside the sandbox, with no prompts. With Configuration on, it also lets a browser change hook and MCP server commands.",
            draft.AllowShell, v => draft.AllowShell = v);
        Switch("Configuration", "Providers, MCP servers, agent types, hooks and settings — not this tab. Hook and MCP server commands need Shell as well.",
            draft.AllowConfig, v => draft.AllowConfig = v);
        _detail.AddControl(ListDetail.Section("Browser access", access.Control));
    }

    /// <summary>Where the browser front end is right now — so a save is read against what is running.</summary>
    private string ServingLine() => _host.WebUrl?.Invoke() is { } url
        ? $"[{ColorScheme.AffirmativeMarkup}]● Serving this terminal's sessions at {MarkupParser.Escape(url.ToString())}[/] [{ColorScheme.MutedMarkup}]— a save applies after /web off and /web[/]"
        : $"[{ColorScheme.MutedMarkup}]○ Not serving. /web shares this terminal's sessions; cxagent web serves without a terminal.[/]";

    /// <summary>
    /// The users by name, each with Set password and Remove, and Add below — the password typed twice into masked
    /// fields that appear only when asked for, the way the Providers tab takes a key.
    /// </summary>
    private IWindowControl UsersEditor(WebDraft draft, Func<string, MarkupControl> help)
    {
        var stack = new FieldGrid();
        var list = new FieldGrid();
        var note = new MarkupControl([""]) { Wrap = true };

        // THE ENTRY ROW, shared by Add and Set password: a name (Add only), the password, and the same again.
        string? target = null;
        var name = ListDetail.Input(new PromptControl { Prompt = "Name: ", Visible = false, HorizontalAlignment = HorizontalAlignment.Stretch });
        var first = ListDetail.Input(new PromptControl { Prompt = "Password: ", MaskCharacter = '•', Visible = false, HorizontalAlignment = HorizontalAlignment.Stretch });
        var again = ListDetail.Input(new PromptControl { Prompt = "Again: ", MaskCharacter = '•', Visible = false, HorizontalAlignment = HorizontalAlignment.Stretch });
        var buttons = new ToolbarControl { ItemSpacing = 2, HorizontalAlignment = HorizontalAlignment.Left, Visible = false };
        buttons.AddItem(Controls.Button(" Done ").OnClick((_, _) => Finish()).Build());
        buttons.AddItem(Controls.Button(" Cancel ").OnClick((_, _) => Hide()).Build());

        void Show(string? who)
        {
            target = who;
            name.Visible = who is null;
            name.Input = "";
            first.Input = "";
            again.Input = "";
            first.Visible = again.Visible = buttons.Visible = true;
            note.SetContent([who is null ? $"[{ColorScheme.MutedMarkup}]A new user: a name, and the password twice.[/]" : $"[{ColorScheme.MutedMarkup}]A new password for {MarkupParser.Escape(who)}, twice.[/]"]);
        }

        void Hide()
        {
            name.Visible = first.Visible = again.Visible = buttons.Visible = false;
            first.Input = again.Input = "";
            note.SetContent([""]);
        }

        void Finish()
        {
            if (first.Input != again.Input)
            {
                note.SetContent([$"[{ColorScheme.CautionMarkup}]The two passwords differ. Nothing was changed.[/]"]);
                return;
            }
            var problem = target is null ? draft.AddUser(name.Input, first.Input) : draft.SetPassword(target, first.Input);
            if (problem is not null)
            {
                note.SetContent([$"[{ColorScheme.CautionMarkup}]{MarkupParser.Escape(problem)}[/]"]);
                return;
            }
            Hide();
            note.SetContent([$"[{ColorScheme.AffirmativeMarkup}]✔ {(target is null ? "Added" : "Changed")} — stored as a hash with Save.[/]"]);
            Draw();
            MarkSave();
        }

        void Draw()
        {
            list.Clear();
            if (draft.Users.Count == 0)
                list.AddBare(help("No users yet. A login is needed for anything but 127.0.0.1 with “Anyone not on this machine”."));
            foreach (var user in draft.Users)
            {
                var row = new ToolbarControl { ItemSpacing = 3, HorizontalAlignment = HorizontalAlignment.Left };
                row.AddItem(new MarkupControl([$"{MarkupParser.Escape(user.Name)}{(user.Plain ? $" [{ColorScheme.CautionMarkup}](plain password in config.json)[/]" : "")}"]));
                var who = user.Name;
                row.AddItem(Controls.Button(" Set password… ").OnClick((_, _) => Show(who)).Build());
                row.AddItem(Controls.Button(" Remove ").OnClick((_, _) => { draft.RemoveUser(who); Draw(); MarkSave(); }).Build());
                list.AddBare(row);
            }
        }

        Draw();

        // AFTER A SAVE the list is redrawn and the "stored as a hash with Save" note goes: it is stored now.
        _redrawUsers = () => { Draw(); note.SetContent([""]); };
        stack.AddBare(list.Control);
        stack.AddBare(Controls.Button(" Add user… ").OnClick((_, _) => Show(null)).Build());
        stack.AddBare(name);
        stack.AddBare(first);
        stack.AddBare(again);
        stack.AddBare(buttons);
        stack.AddBare(note);
        return stack.Control;
    }

    private async Task SaveAsync()
    {
        if (_saving || _draft is not { } draft) return;
        if (!draft.IsDirty) { SetStatus($"[{ColorScheme.MutedMarkup}]Nothing to save.[/]"); return; }
        if (draft.Problem is { } problem) { SetStatus($"[{ColorScheme.CautionMarkup}]{MarkupParser.Escape(problem)}[/]"); return; }
        _saving = true;
        try
        {
            var prepared = await Task.Run(() => ConfigApply.Prepare(_host.Apply, draft.Apply));
            _host.System.EnqueueOnUIThread(() =>
            {
                if (!prepared.Write.Saved)
                {
                    SetStatus(string.Join("\n", prepared.Write.Errors.Select(e => $"[{ColorScheme.DangerMarkup}]✖ {MarkupParser.Escape(e)}[/]")));
                    return;
                }
                // CLEAN WITHOUT A REBUILD: rebuilding would replace the field Ctrl+S was pressed in, and a replaced field
                // is left out of the Tab order.
                draft.Saved();
                _redrawUsers?.Invoke();
                MarkSave();
                if (prepared.Fresh is null)
                {
                    SetStatus(string.Join("\n", prepared.Write.Errors.Select(e => $"[{ColorScheme.CautionMarkup}]Saved, not live: {MarkupParser.Escape(e)}[/]")));
                    return;
                }
                // MADE CURRENT, AND NO RUNNING TAB MOVED: the web block reaches only the next server started.
                ConfigApply.Commit(_host.Apply, prepared,
                    new ConfigChange(new HashSet<string>(), new Dictionary<string, string>(), false, "web settings saved"),
                    CacheChoice.ApplyNow);
                SetStatus(_host.WebUrl?.Invoke() is not null
                    ? $"[{ColorScheme.AffirmativeMarkup}]✔ Saved.[/] [{ColorScheme.MutedMarkup}]The server running now keeps its settings: /web off, then /web, to use these.[/]"
                    : $"[{ColorScheme.AffirmativeMarkup}]✔ Saved.[/] [{ColorScheme.MutedMarkup}]Used the next time /web or cxagent web starts.[/]");
            }, "webTab.Save");
        }
        finally { _saving = false; }
    }

    private void SetStatus(string markup) => _status.SetContent([markup]);
}

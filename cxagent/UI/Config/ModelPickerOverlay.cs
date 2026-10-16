using CxAgent.Core.Llm;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI.Config;

/// <summary>
/// Picks a model for an instance: a filter on top, the endpoint's models below with what it says about
/// each, the current one marked. Enter picks; Esc leaves the model as it was.
///
/// <para>THE FILTER IS ALSO THE TYPED ID. An endpoint that lists nothing — a gateway, a server that is
/// down, a model it does not advertise — must not leave the user unable to name the model, so Enter in
/// the filter with nothing matching takes the text as it stands.</para>
///
/// <para>A WINDOW OVER THE DIALOG, not a list inside it: the list wants height the detail pane does not
/// have, and it is open only while choosing.</para>
/// </summary>
public static class ModelPickerOverlay
{
    private static Window? _window;
    private static TaskCompletionSource<string?>? _result;

    /// <summary>Whether the picker is up — Escape closes it before anything under it.</summary>
    public static bool IsOpen => _window is not null;

    /// <summary>Closes the picker without a choice. Returns whether it was open.</summary>
    public static bool CloseIfOpen()
    {
        if (_window is not { } window) return false;
        _result?.TrySetResult(null);
        window.Close();
        return true;
    }

    /// <summary>Shows the picker and waits for the choice: a model id, or null when Esc kept the current one.</summary>
    /// <param name="system">The window system.</param>
    /// <param name="name">The instance, for the title.</param>
    /// <param name="cfg">The instance as edited — the endpoint and key the list is asked with.</param>
    /// <param name="current">The model in use now, marked in the list.</param>
    public static Task<string?> PickAsync(ConsoleWindowSystem system, string name, ProviderInstanceConfig cfg, string current)
    {
        if (_window is not null) return Task.FromResult<string?>(null);
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _result = result;

        var width = Math.Min((int)(Console.WindowWidth * 0.7), 100);
        var height = Math.Min((int)(Console.WindowHeight * 0.7), 28);
        var window = new WindowBuilder(system)
            .WithTitle($"Pick a model — {name}")
            .WithSize(width, height)
            .Centered()
            .AsModal()
            .WithBorderStyle(BorderStyle.Single)
            .Resizable(true)
            .Movable(true)
            .Minimizable(false)
            .Build();

        var filter = new PromptControl { Prompt = "Filter or id: ", HorizontalAlignment = HorizontalAlignment.Stretch };
        filter.StickyPosition = StickyPosition.Top;
        window.AddControl(filter);
        window.AddControl(new RuleControl { StickyPosition = StickyPosition.Top, HorizontalAlignment = HorizontalAlignment.Stretch });

        var list = new ListControl { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill };
        window.AddControl(list);

        var status = new MarkupControl([$"[{ColorScheme.MutedMarkup}]Asking the endpoint…[/]"]) { Wrap = false };
        status.StickyPosition = StickyPosition.Bottom;
        window.AddControl(status);

        IReadOnlyList<ModelInfo> all = [];
        IReadOnlyList<ModelInfo> shown = [];

        void Fill()
        {
            var ids = ModelFilter.Apply([.. all.Select(m => m.Id)], filter.Input, limit: 400);
            shown = [.. ids.Select(id => all.First(m => m.Id == id))];
            list.ClearItems();
            foreach (var m in shown) list.AddItem(new ListItem(RowText(m, current)) { Tag = m.Id });
            var at = shown.ToList().FindIndex(m => m.Id == current);
            list.SelectedIndex = at >= 0 ? at : shown.Count > 0 ? 0 : -1;
            if (all.Count > 0)
                status.SetContent([$"[{ColorScheme.MutedMarkup}]{shown.Count} of {all.Count} · Enter picks · Esc keeps {MarkupParser.Escape(current)}[/]"]);
        }

        void Pick(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            result.TrySetResult(id.Trim());
            window.Close();
        }

        filter.InputChanged += (_, _) => { if (all.Count > 0) Fill(); };
        // ENTER IN THE FILTER: the highlighted match when there is one, the typed text when there is not.
        filter.Entered += (_, text) => Pick(list.Items.ElementAtOrDefault(list.SelectedIndex)?.Tag as string ?? text);
        list.ItemActivated += (_, item) => Pick(item?.Tag as string);

        // BOUNDED, AND ENDED WITH THE PICKER: a server that accepts the connection and never answers
        // would otherwise hold a request open for as long as the process runs, with no one to read it.
        var listing = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        window.OnClosed += (_, _) =>
        {
            listing.Cancel();
            listing.Dispose();
            _result?.TrySetResult(null);
            _result = null;
            _window = null;
        };

        _window = window;
        system.AddWindow(window);
        system.SetActiveWindow(window);
        window.FocusManager.SetFocus(filter, FocusReason.Programmatic);

        var token = listing.Token;
        _ = Task.Run(() => ModelCatalogQuery.ListAsync(name, cfg, token)).ContinueWith(t =>
            system.EnqueueOnUIThread(() =>
            {
                if (_window != window) return;
                var listed = t.IsCompletedSuccessfully ? t.Result : new ModelCatalogResult([], "could not list models");
                if (listed.Models.Count == 0)
                {
                    status.SetContent([$"[{ColorScheme.CautionMarkup}]{MarkupParser.Escape(listed.Error ?? "the endpoint listed no models")}[/] [{ColorScheme.MutedMarkup}]— type the id and press Enter.[/]"]);
                    return;
                }
                all = [.. listed.Models.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)];
                Fill();
            }, "modelPicker.Fill"), TaskScheduler.Default);

        return result.Task;
    }

    /// <summary>"id · 212k · $3/$15 per M  ← current" — what the endpoint said, and which one is in use.</summary>
    private static string RowText(ModelInfo m, string current)
    {
        var facts = string.Join(" · ", new[] { m.ContextWindow is { } w ? Window(w) : null, m.Price }.Where(f => f is not null));
        var mark = m.Id == current ? $"  [{ColorScheme.AccentMarkup}]← current[/]" : "";
        return $"{MarkupParser.Escape(m.Id)}{(facts.Length > 0 ? $"  [{ColorScheme.MutedMarkup}]{MarkupParser.Escape(facts)}[/]" : "")}{mark}";
    }

    private static string Window(int tokens) =>
        tokens >= 1_000_000 && tokens % 1_000_000 == 0 ? $"{tokens / 1_000_000}M" : $"{Math.Round(tokens / 1000.0)}k";
}

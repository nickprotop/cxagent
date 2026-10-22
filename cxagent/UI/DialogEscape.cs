using CxAgent.UI.Config;

namespace CxAgent.UI;

/// <summary>
/// The open dialog Escape closes, asked topmost first.
///
/// <para>ORDER IS STACKING. A question (<see cref="ConfirmDialog"/>) opens over the plugin manager, the
/// configuration dialog or export; Apply-to-tabs and the model picker open over configuration. Asking
/// a dialog before what sits on it closes it out from under the question, and the question's answer
/// then acts on a dialog that is gone.</para>
/// </summary>
public static class DialogEscape
{
    private static readonly (string Name, Func<bool> Close)[] Closers =
    [
        ("confirm", ConfirmDialog.CloseIfOpen),
        ("apply-to-tabs", ApplyToTabsDialog.CloseIfOpen),
        ("model-picker", ModelPickerOverlay.CloseIfOpen),
        ("quit", QuitDialog.CloseIfOpen),
        ("export", ExportDialog.CloseIfOpen),
        ("plugins", PluginManagerDialog.CloseIfOpen),
        ("config", ConfigDialog.CloseIfOpen),
    ];

    /// <summary>The order dialogs are asked in, by name — for a test.</summary>
    public static IEnumerable<string> Order => Closers.Select(c => c.Name);

    /// <summary>Closes the topmost open dialog. Returns whether one was open.</summary>
    public static bool CloseTopmost() => Closers.Any(c => c.Close());
}

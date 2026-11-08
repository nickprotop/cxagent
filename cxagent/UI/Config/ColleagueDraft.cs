using CxAgent.Core.Colleagues;
using CxAgent.Core.Llm;

namespace CxAgent.UI.Config;

/// <summary>A provider entry as a colleague's model pickers offer it.</summary>
/// <param name="Value">The entry's name — what <c>colleague.json</c> stores.</param>
/// <param name="Label">The name, its model and its window, and why it cannot be chosen when it cannot.</param>
public sealed record ColleagueModelOption(string Value, string Label);

/// <summary>
/// One colleague's settings being edited — where it runs and where it distils, the two things about it that are the
/// user's to choose. Built here, UI-free, for F8's Colleagues tab and the browser's page alike.
///
/// <para>IN THE LIBRARY, NOT config.json: a colleague is a record in <c>colleagues/&lt;name&gt;/colleague.json</c>, and
/// its models travel with it. A save there reaches its next bring; an incarnation already working keeps the model it was
/// brought on.</para>
///
/// <para>EACH MODEL IS INHERITED OR EXPLICIT. Null runs it on the session's model and distils it on its own model;
/// an entry's name pins it. An entry whose window is set under 128k is refused here, as a bring would refuse it; one
/// with no window set is allowed, since its endpoint may report one, and the bring decides.</para>
///
/// <para>NO DESCRIPTION FIELD: every distilling rewrites the description from what the colleague has learned, so an edit
/// would last until its next task.</para>
/// </summary>
public sealed class ColleagueDraft
{
    private ColleagueDraft(ColleagueRecord original)
    {
        Original = original;
        Model = original.Instance;
        DistillModel = original.DistillInstance;
    }

    /// <summary>The draft for a colleague as the library has it.</summary>
    public static ColleagueDraft For(ColleagueRecord record) => new(record);

    public ColleagueRecord Original { get; }

    public string Name => Original.Name;

    /// <summary>The entry it runs on, or null for the session's model.</summary>
    public string? Model { get; set; }

    /// <summary>The entry its rounds are distilled on, or null for <see cref="Model"/> (and, without one, the session's).</summary>
    public string? DistillModel { get; set; }

    public bool IsDirty => Blank(Model) != Original.Instance || Blank(DistillModel) != Original.DistillInstance;

    /// <summary>Why this cannot be saved as it stands, against the entries configured now, or null.</summary>
    public string? Problem(ProviderSettings settings) =>
        TooSmall(settings, Blank(Model)) ?? TooSmall(settings, Blank(DistillModel));

    private static string? TooSmall(ProviderSettings settings, string? entry) =>
        entry is not null && settings.Providers.TryGetValue(entry, out var cfg) && cfg.ContextWindow is { } window
            ? ExperienceBudget.WindowRefusal(window, entry)
            : null;

    /// <summary>Writes both models to the colleague's record; the rest of it stays as the library has it now.</summary>
    public void Apply(ColleagueStore library)
    {
        var model = Blank(Model);
        var distil = Blank(DistillModel);
        library.Update(Name, r => r with { Instance = model, DistillInstance = distil });
    }

    /// <summary>
    /// The entries a picker offers, by name — each with its model and window, and "too small" for one set under the floor.
    /// With <paramref name="draft"/>, an entry it names that is no longer configured is offered too, marked, so what it
    /// holds can be seen and changed rather than silently shown as something else.
    /// </summary>
    public static IReadOnlyList<ColleagueModelOption> Options(ProviderSettings settings, ColleagueDraft? draft = null)
    {
        var options = settings.Providers.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
        {
            var window = p.Value.ContextWindow switch
            {
                null => "window not set",
                < ExperienceBudget.MinimumWindow and var w => $"{w / 1000}k, too small",
                var w => $"{w / 1000}k",
            };
            return new ColleagueModelOption(p.Key, $"{p.Key} — {p.Value.Model} · {window}");
        }).ToList();
        foreach (var held in new[] { draft?.Original.Instance, draft?.Original.DistillInstance })
            if (held is not null && !settings.Providers.ContainsKey(held) && options.All(o => o.Value != held))
                options.Add(new ColleagueModelOption(held, $"{held} — not configured"));
        return options;
    }

    /// <summary>What the inherited choice of each picker is called.</summary>
    public const string SessionModel = "The session's model";
    public const string OwnModel = "Its own model";

    public const string ModelHelp = "Where it works when brought in. A bring that names a model overrides this.";
    public const string DistillHelp = "Where what it did is distilled into experience, after each task.";

    /// <summary>
    /// The rules every colleague is held to, shown on the settings page. FIXED, NOT SETTINGS: the floor and the share of
    /// the window are what keep a colleague's experience from crowding out the task it was brought for.
    /// </summary>
    public static IReadOnlyList<string> Rules =>
    [
        $"A colleague runs and distils only on a model with a window of at least {ExperienceBudget.MinimumWindow / 1000}k.",
        $"A quarter of that window, at most {ExperienceBudget.Cap / 1000}k, is loaded as its experience when it is brought in.",
        "A round refused three times in a row is stepped past, kept in its past, and you are told once.",
    ];

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

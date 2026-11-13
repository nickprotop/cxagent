using CxAgent.Core.Agents;
using CxAgent.Core.Colleagues;
using CxAgent.Core.Llm;

namespace CxAgent.UI.Config;

/// <summary>A provider entry as a colleague's model pickers offer it.</summary>
/// <param name="Value">The entry's name — what <c>colleague.json</c> stores.</param>
/// <param name="Label">The name, its model and its window, and why it cannot be chosen when it cannot.</param>
public sealed record ColleagueModelOption(string Value, string Label);

/// <summary>
/// One colleague's settings being edited — what kind of worker it is, where it runs and where it distils, the things
/// about it that are the user's to choose. Built here, UI-free, for F8's Colleagues tab and the browser's page alike.
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
        Type = original.Type;
        Frozen = original.Frozen;
    }

    /// <summary>The draft for a colleague as the library has it.</summary>
    public static ColleagueDraft For(ColleagueRecord record) => new(record);

    public ColleagueRecord Original { get; }

    public string Name => Original.Name;

    /// <summary>The entry it runs on, or null for the session's model.</summary>
    public string? Model { get; set; }

    /// <summary>The entry its rounds are distilled on, or null for <see cref="Model"/> (and, without one, the session's).</summary>
    public string? DistillModel { get; set; }

    /// <summary>
    /// The agent type it is brought in as: the type's briefing and tools, never its provider list — a bring names the
    /// colleague's own model, which wins over the type's. Kept from the worker it was kept from until changed here.
    /// </summary>
    public string Type { get; set; }

    /// <summary>
    /// Frozen: it works and its rounds are kept, but it learns nothing. Applied through the store's Freeze and Unfreeze,
    /// never written onto the record — unfreezing records the stretch lived frozen, so it is never learned from.
    /// </summary>
    public bool Frozen { get; set; }

    /// <summary>The name it is to have, or null to keep its own. Its old name stays one it is found by.</summary>
    public string? NewName { get; set; }

    private bool Renaming => Blank(NewName?.Trim()) is { } to && !string.Equals(to, Original.Name, StringComparison.Ordinal);

    public bool IsDirty => Blank(Model) != Original.Instance || Blank(DistillModel) != Original.DistillInstance || Frozen != Original.Frozen
                           || SavedType != Original.Type || Renaming;

    /// <summary>The type it is saved with: a blank choice keeps the one it has.</summary>
    private string SavedType => Blank(Type) ?? Original.Type;

    /// <summary>Why this cannot be saved as it stands, against the entries configured now, or null.</summary>
    public string? Problem(ProviderSettings settings) =>
        UnofferedType(settings)
        ?? Unoffered(settings, Blank(Model)) ?? Unoffered(settings, Blank(DistillModel))
        ?? TooSmall(settings, Blank(Model)) ?? TooSmall(settings, Blank(DistillModel));

    /// <summary>
    /// Why <paramref name="entry"/> is not one a picker offers — a configured entry, or one this colleague already holds —
    /// or null. F8's dropdown cannot write anything else; a browser's form could, so the refusal lives here, for both.
    /// </summary>
    private string? Unoffered(ProviderSettings settings, string? entry) =>
        entry is null || settings.Providers.ContainsKey(entry) || entry == Original.Instance || entry == Original.DistillInstance
            ? null
            : $"'{entry}' is not a provider entry in config.json.";

    /// <summary>
    /// Why the type is not one the picker offers, or null. A bring of a colleague whose type is gone is refused as an
    /// unknown type, so a name nothing here defines is not written; the one it already holds stays allowed, as a model's
    /// does, so a save of its other settings is not blocked by a type removed from config.json.
    /// </summary>
    private string? UnofferedType(ProviderSettings settings) =>
        TypeOptions(settings, this).Any(o => o.Value == SavedType && (o.Value == Original.Type || Known(settings, o.Value)))
            ? null
            : $"'{SavedType}' is not an agent type here. Types: {string.Join(", ", TypeOptions(settings).Select(o => o.Value))}.";

    private static bool Known(ProviderSettings settings, string type) =>
        type == AgentTypeCatalog.DefaultTypeName || BuiltinAgentTypes.IsBuiltin(type) || settings.AgentTypes.ContainsKey(type);

    private static string? TooSmall(ProviderSettings settings, string? entry) =>
        entry is not null && settings.Providers.TryGetValue(entry, out var cfg) && cfg.ContextWindow is { } window
            ? ExperienceBudget.WindowRefusal(window, entry)
            : null;

    /// <summary>
    /// Why the new name cannot be taken, or null — the same checks the save makes, asked first so the page says so beside
    /// the field rather than after the rest was looked at.
    /// </summary>
    public string? RenameProblem(ColleagueStore library, Core.Storage.FolderSessionStore? sessions) =>
        Renaming ? ColleagueRenaming.Refusal(library, sessions, new RenameOrder(Name, NewName!)) : null;

    /// <summary>
    /// What a save of this draft did, in words — ONE TEXT FOR F8 AND THE BROWSER, read before the save, since it compares
    /// the draft with the record it was made from. Each change says how it takes effect: a freeze at once, a model at the
    /// next bring.
    /// </summary>
    public string SavedLine
    {
        get
        {
            var said = new List<string>();
            if (Renaming) said.Add($"{Original.Name} is now called {SavedName}; its old names still find it.");
            if (Blank(Model) != Original.Instance || Blank(DistillModel) != Original.DistillInstance)
                said.Add("Its models are used at its next bring; one working now keeps its own.");
            if (SavedType != Original.Type)
                said.Add($"It is brought in as {SavedType} from its next bring; one working now stays {Original.Type}.");
            if (Frozen != Original.Frozen)
                said.Add(Frozen ? ColleagueDesk.FrozenLine(SavedName) : ColleagueDesk.UnfrozenLine(SavedName));
            return string.Join(" ", said);
        }
    }

    /// <summary>The name it has once saved — the new one when it is renamed.</summary>
    public string SavedName => Renaming ? NewName!.Trim() : Name;

    /// <summary>
    /// Writes the draft to the colleague's record — the rest of it stays as the library has it now — and answers why it
    /// could not, or null.
    ///
    /// <para>THE RENAME FIRST, through the one operation every way in uses (<see cref="ColleagueRenaming"/>): refused, it
    /// has written nothing, so a save is never half made. The one-to-one check needs <paramref name="sessions"/>.</para>
    /// </summary>
    public string? Apply(ColleagueStore library, Core.Storage.FolderSessionStore? sessions)
    {
        if (Renaming && ColleagueRenaming.Rename(library, sessions, new RenameOrder(Name, NewName!)) is { } refused) return refused;
        var name = SavedName;
        var model = Blank(Model);
        var distil = Blank(DistillModel);
        var type = SavedType;
        library.Update(name, r => r with { Instance = model, DistillInstance = distil, Type = type });
        if (Frozen != Original.Frozen)
        {
            if (Frozen) library.Freeze(name);
            else library.Unfreeze(name);
        }
        return null;
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

    /// <summary>
    /// The agent types a colleague can be brought in as, in the catalog's order — <c>general</c> first, then the rest by
    /// name — each with what it is for. With <paramref name="draft"/>, a type it holds that is no longer defined is
    /// offered too, marked, as a model picker offers an entry no longer configured.
    /// </summary>
    public static IReadOnlyList<ColleagueModelOption> TypeOptions(ProviderSettings settings, ColleagueDraft? draft = null)
    {
        var names = BuiltinAgentTypes.All.Select(t => t.Name).Concat(settings.AgentTypes.Keys)
            .Where(n => n != AgentTypeCatalog.DefaultTypeName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var options = names.Prepend(AgentTypeCatalog.DefaultTypeName).Select(n => new ColleagueModelOption(n, TypeLabel(settings, n))).ToList();
        if (draft?.Original.Type is { } held && options.All(o => o.Value != held))
            options.Add(new ColleagueModelOption(held, $"{held} — not configured"));
        return options;
    }

    /// <summary>A type's name and the start of what it is for — the shipped text for a built-in, config's for the rest.</summary>
    private static string TypeLabel(ProviderSettings settings, string name)
    {
        var about = BuiltinAgentTypes.Find(name)?.Description
                    ?? (settings.AgentTypes.TryGetValue(name, out var cfg) ? cfg.Description : null)
                    ?? (name == AgentTypeCatalog.DefaultTypeName ? "an ordinary worker, with no briefing of its own" : null);
        return about is { Length: > 0 } ? $"{name} — {Cut(about, 60)}" : name;
    }

    /// <summary>The text up to its first full stop, or cut at a word within <paramref name="most"/> characters with "…" —
    /// a description runs to a paragraph, and a dropdown row has one line.</summary>
    private static string Cut(string text, int most)
    {
        var line = text.Split('.', 2)[0].Trim();
        if (line.Length <= most) return line;
        var space = line.LastIndexOf(' ', most);
        return line[..(space > 0 ? space : most)].TrimEnd(',', ';', ' ') + "…";
    }

    /// <summary>What the inherited choice of each picker is called.</summary>
    public const string SessionModel = "The session's model";
    public const string OwnModel = "Its own model";

    public const string TypeHelp =
        "The kind of worker it is brought in as: that type's briefing and tools. Used at its next bring.";

    public const string ModelHelp = "Where it works when brought in. A bring that names a model overrides this.";
    /// <summary>What renaming does — the words F8's field and the browser's both show.</summary>
    /// <summary>What both front ends say under a colleague's description — one text, so neither words it differently.</summary>
    public const string DescriptionHelp = "Its description is rewritten by every distilling, from what it has learned.";

    public const string NameHelp =
        "Renamed, it is still found by its old names: a session that knew it by one brings it back.";

    /// <summary>What learning means — the words F8's radio and the browser's both show.</summary>
    public const string LearnsHelp = "After each task, what it did is distilled into its experience.";

    /// <summary>What frozen means — see <see cref="LearnsHelp"/>.</summary>
    public const string FrozenHelp =
        "It works as before and its rounds are kept, but its experience stays as it is. Unfrozen, it learns again from "
        + "that moment: what it did while frozen is never learned from.";

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

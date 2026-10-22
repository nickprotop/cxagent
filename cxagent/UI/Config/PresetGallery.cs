using CxAgent.Core.Llm;
using static CxAgent.Core.Llm.ProviderKindCatalog;

namespace CxAgent.UI.Config;

/// <summary>What a gallery card turns into: a new instance with what its preset knows, and a free name.</summary>
public static class PresetGallery
{
    /// <summary>The preset's id, or the id with -2, -3… when that name is taken.</summary>
    public static string SuggestName(IEnumerable<string> taken, ProviderPreset preset)
    {
        var names = taken.ToHashSet(StringComparer.Ordinal);
        if (!names.Contains(preset.Id)) return preset.Id;
        for (var i = 2; ; i++)
            if (!names.Contains($"{preset.Id}-{i}")) return $"{preset.Id}-{i}";
    }

    /// <summary>
    /// A new instance from <paramref name="preset"/>: its kind, endpoint, headers and model hint filled,
    /// and nothing in the file to compare against — so all of it is written on save.
    /// </summary>
    public static InstanceDraft DraftFor(ProviderPreset preset, string name)
    {
        // CACHING STARTS AT ITS DEFAULT, as an absent key loads: then the box shows what will happen, and
        // unticking it differs from the start and is written, rather than loading back as on.
        var empty = new ProviderInstanceConfig(preset.Kind, "", null, null, null,
            CacheControl: ProviderInstanceConfig.DefaultCacheControl(preset.Kind, preset.BaseUrl));
        return new InstanceDraft(name, empty)
        {
            IsNew = true,
            Model = preset.ModelHint,
            BaseUrl = preset.BaseUrl,
            ExtraHeaders = preset.ExtraHeaders,
        };
    }
}

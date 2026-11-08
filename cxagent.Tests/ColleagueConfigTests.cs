using System.Text.Json.Nodes;
using CxAgent.Core.Colleagues;
using CxAgent.Core.Llm;
using CxAgent.UI.Config;
using CxAgent.Web;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The Colleagues tab of F8 and its page in the browser, built from one draft: the switch, the rules that are fixed,
/// and per colleague where it runs and where it distils — inherited or an explicit provider entry, never one under the
/// 128k floor (user, 2026-11-08).
/// </summary>
public sealed class ColleagueConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("colleague-config-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_dir);

    private static ProviderSettings Settings() => ProviderConfigLoader.Parse("""
        { "providers": {
            "big":   { "kind": "ollama", "model": "qwen", "baseUrl": "http://localhost:1/v1", "contextWindow": 200000 },
            "small": { "kind": "ollama", "model": "tiny", "baseUrl": "http://localhost:1/v1", "contextWindow": 32000 },
            "blind": { "kind": "ollama", "model": "what", "baseUrl": "http://localhost:1/v1" } },
          "defaultProvider": "big" }
        """, new Dictionary<string, string>());

    private void Charting(string? instance = "big", string? distil = null) =>
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "explore", Created = DateTimeOffset.UtcNow, Instance = instance, DistillInstance = distil });

    [Fact]
    public void The_draft_reads_both_models_and_writes_only_what_changed()
    {
        Charting();
        var draft = ColleagueDraft.For(Library.Find("Charting")!);
        Assert.Equal("big", draft.Model);
        Assert.Null(draft.DistillModel);
        Assert.False(draft.IsDirty);

        draft.DistillModel = "blind";
        Assert.True(draft.IsDirty);
        Assert.Null(draft.Problem(Settings()));
        draft.Apply(Library);

        var saved = Library.Find("Charting")!;
        Assert.Equal("blind", saved.DistillInstance);
        Assert.Equal("big", saved.Instance);
    }

    [Fact]
    public void Inherit_is_null_the_session_s_model_for_running_its_own_for_distilling()
    {
        Charting(instance: "big", distil: "big");
        var draft = ColleagueDraft.For(Library.Find("Charting")!);

        draft.Model = null;
        draft.DistillModel = null;
        draft.Apply(Library);

        var saved = Library.Find("Charting")!;
        Assert.Null(saved.Instance);
        Assert.Null(saved.DistillInstance);
    }

    [Fact]
    public void An_entry_under_the_floor_is_refused_and_labelled()
    {
        Charting();
        var draft = ColleagueDraft.For(Library.Find("Charting")!);

        draft.Model = "small";

        Assert.Contains("128k", draft.Problem(Settings()));
        var options = ColleagueDraft.Options(Settings());
        Assert.Contains(options, o => o.Value == "small" && o.Label.Contains("too small", StringComparison.Ordinal));
        Assert.Contains(options, o => o.Value == "big" && o.Label.Contains("200k", StringComparison.Ordinal));
        Assert.Contains(options, o => o.Value == "blind" && o.Label.Contains("window not set", StringComparison.Ordinal));
    }

    /// <summary>
    /// Only what the pickers offer can be saved — a configured entry, or the one it already holds: F8's dropdown cannot
    /// write anything else, and a browser's form could, so the rule is the draft's, for both.
    /// </summary>
    [Fact]
    public void A_name_no_picker_offers_is_refused()
    {
        Charting(instance: "gone");
        var draft = ColleagueDraft.For(Library.Find("Charting")!);
        Assert.Null(draft.Problem(Settings()));          // what it already holds stays allowed

        draft.DistillModel = "nonsense";

        Assert.Contains("nonsense", draft.Problem(Settings()));
    }

    [Fact]
    public void An_entry_no_longer_configured_is_still_offered_so_it_can_be_seen_and_changed()
    {
        Charting(instance: "gone");

        var options = ColleagueDraft.Options(Settings(), ColleagueDraft.For(Library.Find("Charting")!));

        Assert.Contains(options, o => o.Value == "gone" && o.Label.Contains("not configured", StringComparison.Ordinal));
    }

    [Fact]
    public void The_browser_has_a_colleagues_tab_after_agent_types()
    {
        var ids = WebConfigForms.Tabs.Select(t => t.Id).ToList();

        Assert.Equal(ids.IndexOf("agents") + 1, ids.IndexOf("colleagues"));
        Assert.Equal(ConfigTab.Agents + 1, ConfigTab.Colleagues);
    }

    [Fact]
    public void The_browser_form_lists_settings_then_each_colleague()
    {
        Charting();

        var form = WebConfigForms.Colleagues(Settings(), Library, null);
        var items = form["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()).ToList();
        var json = form.ToJsonString();

        Assert.Equal([WebConfigForms.ColleagueSettingsItem, "Charting"], items);
        Assert.Contains("\"colleagues\"", json);       // the switch
        Assert.Contains("128k", json);                  // the fixed rules, shown
    }

    [Fact]
    public void The_browser_form_for_one_colleague_sets_the_same_draft()
    {
        Charting();
        var form = WebConfigForms.Colleagues(Settings(), Library, "Charting").ToJsonString();
        Assert.Contains("\"model\"", form);
        Assert.Contains("\"distillModel\"", form);
        Assert.DoesNotContain("explore", form);

        var draft = WebConfigForms.SetColleague(ColleagueDraft.For(Library.Find("Charting")!),
            new JsonObject { ["model"] = "", ["distillModel"] = "blind" });

        Assert.Null(draft.Model);
        Assert.Equal("blind", draft.DistillModel);
    }

    [Fact]
    public void General_no_longer_carries_the_switch()
    {
        var draft = GeneralDraft.From(Settings());

        Assert.DoesNotContain("\"colleagues\"", new JsonObject { ["s"] = WebConfigForms.General(draft) }.ToJsonString());
    }
}

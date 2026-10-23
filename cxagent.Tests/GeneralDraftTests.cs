using System.Text.Json.Nodes;
using CxAgent.Core.Llm;
using CxAgent.UI.Config;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The General tab's settings being edited: each read from config, each written only when it changed.</summary>
public class GeneralDraftTests
{
    private const string Provider = """ "providers": { "local": { "kind": "ollama", "model": "llama3.1" } }, "defaultProvider": "local" """;

    private static ProviderSettings Settings(string extra = "") =>
        ProviderConfigLoader.Parse("{" + Provider + (extra.Length > 0 ? ", " + extra : "") + "}", new Dictionary<string, string>());

    private static JsonObject Root(string extra = "") => JsonNode.Parse("{" + Provider + (extra.Length > 0 ? ", " + extra : "") + "}")!.AsObject();

    [Fact]
    public void AnEmptyConfig_ReadsAsTheDefaults_AndIsClean()
    {
        var draft = GeneralDraft.From(Settings());

        Assert.True(draft.DetachOnTimeout);
        Assert.Equal(TurnCap.Default, draft.Cap);
        Assert.Equal("", draft.CompactText);
        Assert.Equal("inherited", draft.Base);
        Assert.Empty(draft.Removed);
        Assert.False(draft.IsDirty);
        Assert.Null(draft.Problem);
    }

    [Theory]
    [InlineData("""{ "maxTurns": 0 }""", TurnCap.None, "")]
    [InlineData("""{ "maxTurns": 40 }""", TurnCap.Number, "40")]
    [InlineData("""{ }""", TurnCap.Default, "")]
    public void TheTurnCap_ReadsAsItsThreeStates(string orchestrator, TurnCap cap, string number)
    {
        var draft = GeneralDraft.From(Settings($"\"orchestrator\": {orchestrator}"));

        Assert.Equal(cap, draft.Cap);
        if (number.Length > 0) Assert.Equal(number, draft.CapText);
    }

    [Fact]
    public void EveryKey_ReadsBack()
    {
        var draft = GeneralDraft.From(Settings("""
            "shellDetachOnTimeout": false,
            "orchestrator": { "maxTurns": 50, "contextCompressThreshold": 90000 },
            "llmAgent": { "tools": ["inherited", "-run_shell"] }
            """));

        Assert.False(draft.DetachOnTimeout);
        Assert.Equal(TurnCap.Number, draft.Cap);
        Assert.Equal("50", draft.CapText);
        Assert.Equal("90000", draft.CompactText);
        Assert.Equal(["run_shell"], draft.Removed);
    }

    /// <summary>
    /// ONLY WHAT CHANGED IS WRITTEN. A key the user did not touch keeps its exact form in the file — an explicit
    /// default stays explicit — and so does everything else in it.
    /// </summary>
    [Fact]
    public void AnUntouchedKey_IsLeftAsWritten()
    {
        var root = Root("""
            "shellDetachOnTimeout": true,
            "orchestrator": { "maxTurns": 50 },
            "theme": "Ocean"
            """);
        var draft = GeneralDraft.From(Settings("""
            "shellDetachOnTimeout": true,
            "orchestrator": { "maxTurns": 50 }
            """));
        draft.CompactText = "120000";

        draft.Apply(root);

        Assert.True(root["shellDetachOnTimeout"]!.GetValue<bool>());
        Assert.Equal(50, root["orchestrator"]!["maxTurns"]!.GetValue<int>());
        Assert.Equal(120000, root["orchestrator"]!["contextCompressThreshold"]!.GetValue<int>());
        Assert.Equal("Ocean", root["theme"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(TurnCap.None, "", 0)]
    [InlineData(TurnCap.Number, "75", 75)]
    public void ACap_IsWrittenAsItsNumber(TurnCap cap, string text, int written)
    {
        var root = Root();
        var draft = GeneralDraft.From(Settings());
        draft.Cap = cap;
        draft.CapText = text;

        draft.Apply(root);

        Assert.Equal(written, root["orchestrator"]!["maxTurns"]!.GetValue<int>());
    }

    /// <summary>BACK TO A DEFAULT REMOVES THE KEY — and a block left empty goes with it, so the file says nothing it need not.</summary>
    [Fact]
    public void BackToTheDefaults_RemovesTheKeys_AndTheirEmptyBlocks()
    {
        const string written = """
            "shellDetachOnTimeout": false,
            "orchestrator": { "maxTurns": 50, "contextCompressThreshold": 90000 },
            "llmAgent": { "tools": ["inherited", "-run_shell"] }
            """;
        var root = Root(written);
        var draft = GeneralDraft.From(Settings(written));
        draft.DetachOnTimeout = true;
        draft.Cap = TurnCap.Default;
        draft.CompactText = "";
        draft.Removed.Clear();

        draft.Apply(root);

        Assert.Null(root["shellDetachOnTimeout"]);
        Assert.Null(root["orchestrator"]);
        Assert.Null(root["llmAgent"]);
    }

    /// <summary>A BLOCK THAT HOLDS SOMETHING ELSE STAYS — llmAgent carries routing and allowed providers too.</summary>
    [Fact]
    public void ClearingTheTools_KeepsTheRestOfTheirBlock()
    {
        const string written = """ "llmAgent": { "tools": ["inherited", "-run_shell"], "allowedProviders": ["local"] } """;
        var root = Root(written);
        var draft = GeneralDraft.From(Settings(written));
        draft.Removed.Clear();

        draft.Apply(root);

        Assert.Null(root["llmAgent"]!["tools"]);
        Assert.NotNull(root["llmAgent"]!["allowedProviders"]);
    }

    [Fact]
    public void Tools_AreWrittenAsTerms()
    {
        var root = Root();
        var draft = GeneralDraft.From(Settings());
        draft.Removed.Add("run_shell");
        draft.Removed.Add("write_file");

        draft.Apply(root);

        Assert.Equal(["inherited", "-run_shell", "-write_file"],
            root["llmAgent"]!["tools"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("many")]
    [InlineData("")]
    public void ACapThatIsNotAPositiveNumber_IsAProblem(string text)
    {
        var draft = GeneralDraft.From(Settings());
        draft.Cap = TurnCap.Number;
        draft.CapText = text;

        Assert.Contains("turn cap", draft.Problem);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("lots")]
    public void AThresholdThatIsNotAPositiveNumber_IsAProblem(string text)
    {
        var draft = GeneralDraft.From(Settings());
        draft.CompactText = text;

        Assert.Contains("Compact above", draft.Problem);
    }

    /// <summary>A NUMBER TYPED BUT NOT CHOSEN IS NOT A PROBLEM: the cap's field matters only when "Cap at" is picked.</summary>
    [Fact]
    public void ACapNumber_IsIgnored_UnlessCapAtIsChosen()
    {
        var draft = GeneralDraft.From(Settings());
        draft.CapText = "nonsense";

        Assert.Null(draft.Problem);
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void ChangingAnything_MakesItDirty_AndChangingItBack_Clean()
    {
        var draft = GeneralDraft.From(Settings());
        draft.DetachOnTimeout = false;
        Assert.True(draft.IsDirty);
        draft.DetachOnTimeout = true;
        Assert.False(draft.IsDirty);

        draft.CompactText = " 90000 ";
        Assert.True(draft.IsDirty);
        draft.CompactText = "";
        Assert.False(draft.IsDirty);
    }

    /// <summary>
    /// AN EMPTY LIST IS NO TOOLS, NOT THE DEFAULT. It reads as every box unticked — the truth — and stays clean, and
    /// leaving it alone writes nothing.
    /// </summary>
    [Fact]
    public void NoTools_ReadsAsEveryBoxUnticked_AndIsClean()
    {
        const string written = """ "llmAgent": { "tools": [] } """;
        var draft = GeneralDraft.From(Settings(written));

        Assert.Equal(CxAgent.Core.Jobs.Tool.KnownNames.Count(), draft.Removed.Count);
        Assert.False(draft.IsDirty);
    }

    /// <summary>Every box unticked is written as the empty list — no tools — not as sixteen removals.</summary>
    [Fact]
    public void EveryBoxUnticked_IsWrittenAsNoTools()
    {
        var root = Root();
        var draft = GeneralDraft.From(Settings());
        draft.Removed.AddRange(CxAgent.Core.Jobs.Tool.KnownNames);

        draft.Apply(root);

        Assert.Empty(root["llmAgent"]!["tools"]!.AsArray());
    }

    /// <summary>
    /// A LIST WRITTEN ANOTHER WAY IS NOT A CHANGE. With no base, or the base alone, it says what the boxes show; the
    /// tab opens clean, and closing it asks nothing.
    /// </summary>
    [Theory]
    [InlineData("""["-run_shell"]""")]
    [InlineData("""["inherited"]""")]
    [InlineData("""["inherited", "+agent"]""")]
    public void AListWrittenAnotherWay_OpensClean(string tools)
    {
        var draft = GeneralDraft.From(Settings($$""" "llmAgent": { "tools": {{tools}} } """));

        Assert.False(draft.IsDirty);
    }

    /// <summary>THE ORCHESTRATOR BLOCK STAYS while it holds the other key.</summary>
    [Fact]
    public void ClearingTheCap_KeepsTheThreshold()
    {
        const string written = """ "orchestrator": { "maxTurns": 50, "contextCompressThreshold": 90000 } """;
        var root = Root(written);
        var draft = GeneralDraft.From(Settings(written));
        draft.Cap = TurnCap.Default;

        draft.Apply(root);

        Assert.Null(root["orchestrator"]!["maxTurns"]);
        Assert.Equal(90000, root["orchestrator"]!["contextCompressThreshold"]!.GetValue<int>());
    }

    /// <summary>
    /// SAVED IS THE NEW ORIGINAL. After a save the draft is clean without being rebuilt — the tab keeps its controls,
    /// and the keyboard stays where it was.
    /// </summary>
    [Fact]
    public void Saved_IsTheNewOriginal()
    {
        var draft = GeneralDraft.From(Settings());
        draft.DetachOnTimeout = false;
        draft.Cap = TurnCap.Number;
        draft.CapText = "40";
        draft.CompactText = "90000";
        draft.Removed.Add("run_shell");

        draft.Saved();

        Assert.False(draft.IsDirty);
        draft.DetachOnTimeout = true;
        Assert.True(draft.IsDirty);
    }
}

using CxAgent.Core.Config;
using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.UI.Config;
using Xunit;

namespace CxAgent.Tests;

/// <summary>An agent type being edited: a built-in writes only what it may, an own type everything.</summary>
public class AgentDraftTests
{
    [Fact]
    public void ABuiltin_WritesOnlyProviderTurnsAndTools()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("explore")!, null);
        draft.Providers.Add("local");
        draft.MaxTurns = 20;
        var fields = draft.ToFields();
        Assert.Equal("local", fields.Provider);
        Assert.Equal(20, fields.MaxTurns);
        Assert.Null(fields.Briefing);
        Assert.Null(fields.Description);
    }

    [Fact]
    public void ABuiltinWithNoOverride_IsCleanUntilTouched()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("explore")!, null);
        Assert.False(draft.IsDirty);
        draft.Removed.Add("write_file");
        Assert.True(draft.IsDirty);
        Assert.Equal(["inherited", "-write_file"], draft.ToFields().Tools);
    }

    [Fact]
    public void BackToTheSessionsProvider_ClearsTheKey()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("explore")!, new AgentTypeConfig(Provider: "local"));
        draft.Providers.Clear();
        Assert.Contains("provider", draft.ToFields().Clear);
    }

    [Fact]
    public void ANewOwnType_WritesItsBriefing()
    {
        var draft = AgentDraft.ForNew("researcher");
        draft.Briefing = "You research.";
        draft.Description = "reads outside the repo";
        var fields = draft.ToFields();
        Assert.Equal("You research.", fields.Briefing);
        Assert.Equal("reads outside the repo", fields.Description);
    }

    [Fact]
    public void General_WritesNoEmptyBriefing()
    {
        var draft = AgentDraft.ForGeneral(null);
        draft.Providers.Add("local");
        var fields = draft.ToFields();
        Assert.Equal("local", fields.Provider);
        Assert.Null(fields.Briefing);
        Assert.True(draft.IsGeneral);
    }

    [Fact]
    public void AList_IsEditedInOrder_AndWrittenAsAnArray()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("planner")!, null);
        draft.Providers.AddRange(["local", "deepseek"]);

        var fields = draft.ToFields();
        Assert.Equal(["local", "deepseek"], fields.Providers);
        Assert.True(draft.IsDirty);
    }

    [Fact]
    public void AListOfOne_IsWrittenAsAString()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("planner")!, null);
        draft.Providers.Add("local");

        Assert.Equal("local", draft.ToFields().Provider);
        Assert.Null(draft.ToFields().Providers);
    }

    [Fact]
    public void AnUntouchedList_IsClean_AndWritesNothing()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("planner")!,
            new AgentTypeConfig(Provider: "local") { Providers = ["local", "deepseek"], Written = ["local", "deepseek:deepseek/deepseek-v3.2"] });

        Assert.Equal(["local", "deepseek:deepseek/deepseek-v3.2"], draft.Providers);   // as written
        Assert.False(draft.IsDirty);
        Assert.Null(draft.ToFields().Providers);
        Assert.Null(draft.ToFields().Provider);
    }

    [Fact]
    public void EmptyingTheList_ClearsTheKey()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("planner")!,
            new AgentTypeConfig(Provider: "local") { Providers = ["local", "deepseek"], Written = ["local", "deepseek"] });
        draft.Providers.Clear();

        Assert.Contains("provider", draft.ToFields().Clear);
    }

    [Fact]
    public void ADisabledTypesList_IsShownAsWritten()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("planner")!,
            new AgentTypeConfig() { Written = ["local", "ghost"], Disabled = "provider 'ghost' is not a configured provider" });

        Assert.Equal(["local", "ghost"], draft.Providers);     // so the user can remove the bad one
    }

    [Fact]
    public void AReorder_KeepsAPinnedEntryAsWritten()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("planner")!,
            new AgentTypeConfig(Provider: "local") { Providers = ["local", "opus-quick"], Written = ["local", "opus-quick:claude-opus-5-5"] });
        draft.Providers.Reverse();

        Assert.Equal(["opus-quick:claude-opus-5-5", "local"], draft.ToFields().Providers);
    }

    [Fact]
    public void AnEntryAlreadyListed_IsListedByName_InAnyForm()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("planner")!,
            new AgentTypeConfig(Provider: "local") { Providers = ["local"], Written = ["local:qwen"] });

        Assert.True(draft.ListsProvider("local"));
        Assert.False(draft.ListsProvider("deepseek"));
    }

    [Fact]
    public void ABadlyShapedProvider_CanBeCleared()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("planner")!,
            new AgentTypeConfig() { Disabled = "provider must be a provider name or a list of them" });
        Assert.Empty(draft.Providers);
        Assert.False(draft.IsDirty);

        draft.ClearBadProvider = true;

        Assert.True(draft.IsDirty);
        Assert.Contains("provider", draft.ToFields().Clear);
    }

    [Fact]
    public void TickingAnyProvider_WritesAll()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("planner")!, new AgentTypeConfig(Provider: "local") { Providers = ["local"], Written = ["local"] });
        draft.AnyProvider = true;

        Assert.True(draft.IsDirty);
        Assert.Equal("all", draft.ToFields().Provider);
    }

    [Fact]
    public void TickingAnyProvider_TheWayTheCheckboxDoes_KeepsAll()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("planner")!, new AgentTypeConfig(Provider: "local") { Providers = ["local"], Written = ["local"] });
        draft.AnyProvider = true;                                  // what the checkbox handler does

        var type = new System.Text.Json.Nodes.JsonObject { ["provider"] = "local" };
        var root = new System.Text.Json.Nodes.JsonObject { ["agents"] = new System.Text.Json.Nodes.JsonObject { ["planner"] = type } };
        AgentEdits.SetOverride(root, "planner", draft.ToFields());
        Assert.Equal("all", (string?)root["agents"]!["planner"]!["provider"]);
    }

    /// <summary>Ticking keeps the list underneath, so unticking puts it back: a tick and untick by
    /// mistake must not save a type with no provider, which would run on the session's model.</summary>
    [Fact]
    public void TickThenUntick_RestoresTheList_AndChangesNothing()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("planner")!,
            new AgentTypeConfig(Provider: "local") { Providers = ["local", "deepseek"], Written = ["local", "deepseek"] });
        draft.AnyProvider = true;
        Assert.True(draft.IsDirty);

        draft.AnyProvider = false;

        Assert.Equal(["local", "deepseek"], draft.Providers);
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void AnAllType_OpensTicked_AndUntickingClearsIt()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("planner")!, new AgentTypeConfig() { AnyProvider = true, Written = ["all"] });
        Assert.True(draft.AnyProvider);
        Assert.Empty(draft.Providers);
        Assert.False(draft.IsDirty);

        draft.AnyProvider = false;
        Assert.Contains("provider", draft.ToFields().Clear);
    }

    [Fact]
    public void AMixedListsAll_IsARowYouCanRemove()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("planner")!,
            new AgentTypeConfig() { Written = ["local", "all"], Disabled = "provider 'all' stands alone: list providers by name, or write \"all\" by itself" });
        Assert.False(draft.AnyProvider);
        Assert.Equal(["local", "all"], draft.Providers);
    }

    /// <summary>A bare "all" in a type's provider means every provider, so an entry that is itself
    /// named all is added by its full name — added alone it would silently become any-provider, and
    /// beside another it would disable the type.</summary>
    [Theory]
    [InlineData("all")]
    [InlineData("ALL")]
    public void AddingAnEntryNamedAll_WritesItsModelToo(string name)
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("planner")!, null);
        draft.AddProvider("local", "qwen");
        draft.AddProvider(name, "m1");

        Assert.Equal(["local", $"{name}:m1"], draft.Providers);
        Assert.True(draft.ListsProvider(name));
    }

    // ---- Runs on: the session's model, a list, or any provider ----------------------------------

    [Fact]
    public void RunsOn_StartsWhereConfigLeftIt()
    {
        Assert.Equal(RunsOn.Session, AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("explore")!, null).Mode);
        Assert.Equal(RunsOn.List, AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("explore")!, new AgentTypeConfig(Provider: "local")).Mode);
        Assert.Equal(RunsOn.Any, AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("explore")!, new AgentTypeConfig(Provider: "all") { AnyProvider = true }).Mode);
    }

    [Fact]
    public void ChoosingTheSessionsModel_ClearsTheProviderKey()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("explore")!, new AgentTypeConfig(Provider: "local"));
        draft.Choose(RunsOn.Session);
        Assert.Empty(draft.Providers);
        Assert.Contains("provider", draft.ToFields().Clear);
        Assert.Null(draft.ProviderProblem);
    }

    [Fact]
    public void BackToTheList_RestoresWhatItHeld()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("explore")!, new AgentTypeConfig(Provider: "local"));
        draft.Choose(RunsOn.Session);
        draft.Choose(RunsOn.List);
        Assert.Equal(["local"], draft.Providers);
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void AnyProvider_WritesAll()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("explore")!, null);
        draft.Choose(RunsOn.Any);
        Assert.True(draft.AnyProvider);
        Assert.Equal(AgentTypeConfig.AnyProviderWord, draft.ToFields().Provider);
    }

    /// <summary>AN EMPTY LIST IS NOT A CHOICE: it would save as the session's model, which is a different
    /// option the user did not pick.</summary>
    [Fact]
    public void AnEmptyList_IsAProblem_UntilAProviderIsAdded()
    {
        var draft = AgentDraft.ForBuiltin(BuiltinAgentTypes.Find("explore")!, null);
        draft.Choose(RunsOn.List);
        Assert.NotNull(draft.ProviderProblem);
        draft.AddProvider("local", "qwen");
        Assert.Null(draft.ProviderProblem);
    }
}

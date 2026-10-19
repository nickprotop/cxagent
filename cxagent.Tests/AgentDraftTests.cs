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

        Assert.Equal(["local", "deepseek"], draft.Providers);
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
}

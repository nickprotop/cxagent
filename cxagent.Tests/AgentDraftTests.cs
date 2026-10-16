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
        draft.Provider = "local";
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
        draft.Provider = null;
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
        draft.Provider = "local";
        var fields = draft.ToFields();
        Assert.Equal("local", fields.Provider);
        Assert.Null(fields.Briefing);
        Assert.True(draft.IsGeneral);
    }
}

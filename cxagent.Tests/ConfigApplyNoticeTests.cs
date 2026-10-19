using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>A save that disables an agent type as a side effect says so, rather than leaving only the model told.</summary>
public class ConfigApplyNoticeTests
{
    [Fact]
    public void OnlyTypesNewlyDisabled_AreReported()
    {
        string[] before = ["agents.old.provider 'x' is not a configured provider; old is disabled.", "mcp.y has no command; skipped."];
        string[] after =
        [
            "agents.old.provider 'x' is not a configured provider; old is disabled.",
            "agents.planner.provider 'opus-quick:claude-opus-5-5': 'opus-quick' runs claude-x, not 'claude-opus-5-5'; planner is disabled.",
            "mcp.y has no command; skipped.",
        ];

        Assert.Equal([after[1]], ConfigApply.NewlyDisabledTypes(before, after));
    }
}

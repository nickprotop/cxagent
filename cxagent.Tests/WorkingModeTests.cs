using CxAgent.Core.Agent;
using Xunit;

namespace CxAgent.Tests;

public class WorkingModeTests
{
    /// <summary>
    /// THE AXES ARE INDEPENDENT — the whole reason this is a record and not a wider enum. Folding
    /// them into one enum would make "fan-out, always-ask" unrepresentable without a value per
    /// combination, which is the mistake the shape exists to avoid.
    /// </summary>
    [Fact]
    public void TheAxesAreIndependent()
    {
        var mode = new WorkingMode(AgentMode.FanOut, EditMode.AlwaysAsk);

        Assert.True(mode.CanDelegate);
        Assert.Equal(EditMode.AlwaysAsk, mode.Edits);
    }

    /// <summary>
    /// The implicit widening from a bare AgentMode still compiles and still means what it meant. It
    /// exists so two dozen call sites did not change when the record arrived, and this pins that
    /// adding a second axis did not quietly break that promise.
    /// </summary>
    [Fact]
    public void ABareAgentMode_StillWidens_AndKeepsTheDefaultEdits()
    {
        WorkingMode mode = AgentMode.FanOut;

        Assert.True(mode.CanDelegate);

        // The widening carries the STRICT edit mode, which is the point: a call site that says only
        // "fan out" is not also asking for silent writes. It never was asking for them — it just
        // used to get them.
        Assert.Equal(EditMode.AlwaysAsk, mode.Edits);
    }

    /// <summary>Agent first, because it is the coarser fact: whether there is one agent or several
    /// frames everything else, including whose edits are being accepted.</summary>
    [Fact]
    public void ToString_RendersBothAxes_AgentFirst()
    {
        var text = new WorkingMode(AgentMode.FanOut, EditMode.AlwaysAsk).ToString();

        Assert.Contains("fan-out", text, System.StringComparison.Ordinal);
        Assert.Contains("always-ask", text, System.StringComparison.Ordinal);
        Assert.True(text.IndexOf("fan-out", System.StringComparison.Ordinal)
                  < text.IndexOf("always-ask", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// A DEFAULT-CONSTRUCTED STRUCT LANDS ON THE STRICT MODE, and that must stay true.
    ///
    /// <para>WorkingMode is a record struct, so `new WorkingMode()` and `default(WorkingMode)`
    /// zero-initialise and IGNORE the parameter defaults — they do NOT produce WorkingMode.Default.
    /// AlwaysAsk is therefore first in the enum on purpose: the worst a forgotten initialiser can do
    /// is ask too often, never write silently. Reordering EditMode would silently invert that, which
    /// is why it is pinned here rather than left to the comment.</para>
    ///
    /// <para>THE ZERO VALUE AND THE EXPLICIT DEFAULT NOW AGREE, and that is worth pinning too. They
    /// used to differ — the struct zeroed to AlwaysAsk while WorkingMode.Default said AcceptEdits —
    /// so which one a session got depended on whether the code path went through the property or
    /// the constructor. That is exactly the kind of difference nobody notices until it decides
    /// whether a write asked.</para>
    /// </summary>
    [Fact]
    public void ADefaultConstructedMode_FallsToTheStrictEditMode()
    {
        Assert.Equal(EditMode.AlwaysAsk, new WorkingMode().Edits);
        Assert.Equal(EditMode.AlwaysAsk, default(WorkingMode).Edits);
        Assert.Equal(0, (int)EditMode.AlwaysAsk);

        // And the explicit session default agrees with the zero value.
        Assert.Equal(EditMode.AlwaysAsk, WorkingMode.Default.Edits);
    }

    [Theory]
    [InlineData("always-ask", EditMode.AlwaysAsk)]
    [InlineData("alwaysask", EditMode.AlwaysAsk)]
    [InlineData("always_ask", EditMode.AlwaysAsk)]
    [InlineData("accept-edits", EditMode.AcceptEdits)]
    [InlineData("acceptedits", EditMode.AcceptEdits)]
    [InlineData("ACCEPT-EDITS", EditMode.AcceptEdits)]
    [InlineData("  accept-edits  ", EditMode.AcceptEdits)]
    public void Parse_AcceptsTheNearMissesPeopleType(string text, EditMode expected)
    {
        Assert.Equal(expected, EditModes.Parse(text));
    }

    /// <summary>A value that silently defaults is how someone concludes a mode is broken when they
    /// merely misspelled it.</summary>
    [Theory]
    [InlineData("nonsense")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_RejectsAnythingElse(string? text)
    {
        Assert.Null(EditModes.Parse(text));
    }

    /// <summary>
    /// AUTO IS NOT SELECTABLE BY NAME while no classifier is configured. A mode that claims
    /// background review while nothing reviews is worse than no mode, and a CLI flag must not be able
    /// to reach one.
    /// </summary>
    [Fact]
    public void Parse_DoesNotAcceptAuto_WhileNoClassifierIsConfigured()
    {
        Assert.Null(EditModes.Parse("auto"));
        Assert.DoesNotContain("auto", EditModes.Valid, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// One source for the name, so the CLI, /mode and the composer cannot drift apart.
    ///
    /// <para>Auto HAS a name while not being parseable — display and selectability are different
    /// questions. A configured Auto session must still be able to say what mode it is in.</para>
    /// </summary>
    [Theory]
    [InlineData(EditMode.AlwaysAsk, "always-ask")]
    [InlineData(EditMode.AcceptEdits, "accept-edits")]
    [InlineData(EditMode.Auto, "auto")]
    public void Name_IsStableForEveryMode(EditMode mode, string expected)
    {
        Assert.Equal(expected, EditModes.Name(mode));
    }
}

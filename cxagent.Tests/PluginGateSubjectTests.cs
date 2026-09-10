using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The one contract change phase two makes, and the shape that keeps it additive.
/// </summary>
public class PluginGateSubjectTests
{
    /// <summary>
    /// AN INIT-ONLY PROPERTY, NEVER A THIRD POSITIONAL PARAMETER. PluginGate is published: a new
    /// positional parameter changes the record's constructor and its Deconstruct, so every plugin
    /// compiled against the old shape breaks at LOAD with MissingMethodException rather than at
    /// build with an error somebody could read.
    /// </summary>
    [Fact]
    public void A_gate_still_constructs_from_the_two_arguments_it_always_took()
    {
        var gate = new PluginGate("run `gh run watch` in the background", AlwaysAskable: true);

        Assert.Equal("run `gh run watch` in the background", gate.Display);
        Assert.True(gate.AlwaysAskable);
        Assert.Null(gate.Subject);
    }

    [Fact]
    public void A_subject_is_set_by_object_initialiser()
    {
        var gate = new PluginGate("run `gh run watch 12345` until it exits")
        {
            Subject = "gh run watch 12345",
        };

        Assert.Equal("gh run watch 12345", gate.Subject);
    }

    /// <summary>
    /// NULL IS THE HONEST DEFAULT, and PermissionRequest already reads it that way: What is
    /// `Subject ?? Display`, so an old binary that sets nothing keeps the behaviour it had.
    /// </summary>
    [Fact]
    public void An_unset_subject_is_null_rather_than_empty()
    {
        Assert.Null(new PluginGate("something").Subject);
    }
}

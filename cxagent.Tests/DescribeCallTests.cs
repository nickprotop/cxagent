using System.Text.Json;
using CxAgent.Core.Agents;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What a tool call is CALLED on a row. The generic branch renders the raw arguments, which for the
/// tools a reader sees most is JSON scaffolding around the one value that matters — the same
/// complaint the spawn, skill and plan branches were each written to answer.
/// </summary>
public class DescribeCallTests
{
    private static ToolCall Call(string name, string json) =>
        new() { Name = name, Arguments = JsonDocument.Parse(json).RootElement, Id = "id" };

    private const string Root = "/work";

    // A PATH RELATIVE TO THE WORKING DIRECTORY, which is how the model names files in its own calls
    // and what the reader recognises. An absolute path eats the column and repeats a prefix that is
    // the same on every row.
    [Fact]
    public void AReadIsNamedByItsFile()
        => Assert.Equal("read_file · UI/FileTab.cs",
            Agent.DescribeCallForTest(Call("read_file", """{"path":"/work/UI/FileTab.cs"}"""), Root));

    // A FILE OUTSIDE THE WORKING DIRECTORY KEEPS ITS PATH. Relative would climb with ../.., which is
    // longer than the absolute and harder to read.
    [Fact]
    public void AFileOutsideTheRootStaysAbsolute()
        => Assert.Contains("/etc/hosts",
            Agent.DescribeCallForTest(Call("read_file", """{"path":"/etc/hosts"}"""), Root));

    // A GREP IS NAMED BY WHAT IT LOOKED FOR, not where: the pattern is the question being asked.
    [Fact]
    public void AGrepIsNamedByItsPattern()
        => Assert.Equal("""grep · "class Agent" """.TrimEnd(),
            Agent.DescribeCallForTest(
                Call("grep", """{"pattern":"class Agent","path":"/work"}"""), Root));

    [Fact]
    public void AShellCallIsNamedByItsCommand()
        => Assert.Equal("run_shell · dotnet build",
            Agent.DescribeCallForTest(Call("run_shell", """{"command":"dotnet build"}"""), Root));

    [Fact]
    public void AWriteIsNamedByItsFile()
        => Assert.Equal("write_file · UI/FileTab.cs",
            Agent.DescribeCallForTest(
                Call("write_file", """{"path":"/work/UI/FileTab.cs","content":"using System;"}"""),
                Root));

    // A LONG VALUE IS STILL CLIPPED. The spawn branch exists because an unclipped description ate
    // the budget and hid the type; a command longer than the row must lose its tail, not the label.
    [Fact]
    public void ALongCommandIsClipped()
    {
        var label = Agent.DescribeCallForTest(
            Call("run_shell", $$"""{"command":"{{new string('x', 200)}}"}"""), Root);

        Assert.StartsWith("run_shell · ", label);
        Assert.True(label.Length < 90, $"expected a clipped label, got {label.Length} chars");
        Assert.EndsWith("…", label);
    }

    // AN UNKNOWN TOOL KEEPS THE OLD SHAPE. This adds branches for the tools a reader sees most; it
    // does not claim to know every tool a plugin might add.
    [Fact]
    public void AnUnknownToolFallsBackToItsArguments()
        => Assert.Contains("{",
            Agent.DescribeCallForTest(Call("some_plugin_tool", """{"a":"b"}"""), Root));

    // NO WORKING DIRECTORY, NO CRASH — a path is simply shown as it came.
    [Fact]
    public void NoRootStillNamesTheFile()
        => Assert.Contains("FileTab.cs",
            Agent.DescribeCallForTest(Call("read_file", """{"path":"/work/UI/FileTab.cs"}"""), null));
}

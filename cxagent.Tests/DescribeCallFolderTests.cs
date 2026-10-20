using System.Text.Json;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A ROW LABELS A PATH WHERE THE TOOL WILL PUT IT — relative to the agent's folder, which is what the tool
/// resolves it against, not the process directory. With a second session elsewhere, the process-directory
/// reading shortened `../work/b/x` to `x` while the write landed in `work/work/b/x`.
/// </summary>
[Collection("working-directory")]
public sealed class DescribeCallFolderTests : IDisposable
{
    private readonly string _top = Path.Combine(Path.GetTempPath(), "cx-describe-" + Guid.NewGuid().ToString("N"));
    private readonly string _previous = Directory.GetCurrentDirectory();

    public DescribeCallFolderTests()
    {
        Directory.CreateDirectory(Path.Combine(_top, "a"));
        Directory.CreateDirectory(Path.Combine(_top, "work", "b"));
        Directory.SetCurrentDirectory(Path.Combine(_top, "a"));
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_previous);
        Directory.Delete(_top, recursive: true);
    }

    [Fact]
    public void ARelativePath_IsLabelledFromTheAgentsFolder_NotTheProcesses()
    {
        var call = new ToolCall
        {
            Name = "write_file",
            Id = "1",
            Arguments = JsonSerializer.Deserialize<JsonElement>("""{"path":"../work/b/x"}"""),
        };

        var row = Agent.DescribeCall(call, Path.Combine(_top, "work", "b"));

        Assert.Contains("../work/b/x", row);
    }
}

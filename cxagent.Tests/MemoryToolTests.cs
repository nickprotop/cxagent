using System.Text.Json;
using CxAgent.Core.Memory;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The <c>memory</c> tool: list, read, write and delete, and read-only for a sub-agent.</summary>
public sealed class MemoryToolTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("memtool-config-").FullName;
    private readonly string _project = Directory.CreateTempSubdirectory("memtool-proj-").FullName;

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private MemoryStore Store => new(_config, _project);

    private static ToolCall Call(object args, string name = "memory") =>
        new() { Name = name, Id = "c1", Arguments = JsonSerializer.SerializeToElement(args) };

    [Fact]
    public void Write_then_list_read_and_delete()
    {
        var tool = new MemoryTool(Store, readOnly: false);

        var saved = tool.TryInvoke(Call(new { action = "write", scope = "project", name = "test-command",
            description = "How the suite runs", content = "dotnet test, 60s timeout" }));
        Assert.Contains("Saved", saved, StringComparison.Ordinal);

        Assert.Contains("test-command — How the suite runs", tool.TryInvoke(Call(new { action = "list" })), StringComparison.Ordinal);
        Assert.Contains("dotnet test, 60s timeout", tool.TryInvoke(Call(new { action = "read", name = "test-command" })), StringComparison.Ordinal);
        Assert.Contains("Deleted", tool.TryInvoke(Call(new { action = "delete", name = "test-command" })), StringComparison.Ordinal);
        Assert.Empty(Store.List());
    }

    [Fact]
    public void A_write_must_say_which_scope()
    {
        var reply = new MemoryTool(Store, readOnly: false).TryInvoke(Call(new { action = "write", name = "x", description = "d", content = "c" }));
        Assert.Contains("scope", reply, StringComparison.Ordinal);
        Assert.Empty(Store.List());
    }

    [Fact]
    public void A_refused_write_says_why()
    {
        var reply = new MemoryTool(Store, readOnly: false).TryInvoke(Call(new { action = "write", scope = "system", name = "key",
            description = "d", content = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123" }));
        Assert.Contains("secret", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reading_a_note_that_is_not_there_names_the_ones_that_are()
    {
        var store = Store;
        store.Write(new MemoryNote("real-one", "d", MemoryScope.System, "b"));
        var reply = new MemoryTool(store, readOnly: false).TryInvoke(Call(new { action = "read", name = "made-up" }));
        Assert.Contains("real-one", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sub_agent_reads_but_cannot_write_or_delete()
    {
        var store = Store;
        store.Write(new MemoryNote("kept", "d", MemoryScope.System, "b"));
        var tool = new MemoryTool(store, readOnly: true);

        Assert.Contains("b", tool.TryInvoke(Call(new { action = "read", name = "kept" })), StringComparison.Ordinal);
        Assert.Contains("read", tool.TryInvoke(Call(new { action = "write", scope = "system", name = "new", description = "d", content = "c" })), StringComparison.Ordinal);
        Assert.Contains("read", tool.TryInvoke(Call(new { action = "delete", name = "kept" })), StringComparison.Ordinal);
        Assert.Equal(["kept"], store.List().Select(n => n.Name).ToList());
    }

    [Fact]
    public void A_sub_agent_is_offered_only_list_and_read()
    {
        var actions = new MemoryTool(Store, readOnly: true).Definition.InputSchema
            .GetProperty("properties").GetProperty("action").GetProperty("enum")
            .EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Equal(["list", "read"], actions);
    }

    [Fact]
    public void The_scopes_the_model_is_offered_are_system_and_project()
    {
        var scopes = new MemoryTool(Store, readOnly: false).Definition.InputSchema
            .GetProperty("properties").GetProperty("scope").GetProperty("enum")
            .EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Equal(["system", "project"], scopes);
    }

    [Fact]
    public void Another_tools_call_is_not_its_own() =>
        Assert.Null(new MemoryTool(Store, readOnly: false).TryInvoke(Call(new { action = "list" }, "todowrite")));
}

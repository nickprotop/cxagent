using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Plugins.Builtin;
using Xunit;

namespace CxAgent.Tests;

public class PluginRegistryTests
{
    [Fact]
    public void CreateWithBuiltins_RegistersTheFourSelfContainedPlugins()
    {
        var reg = PluginRegistry.CreateWithBuiltins();
        foreach (var type in new[] { "shell", "file", "wait", "http" })
        {
            Assert.True(reg.TryGet(type, out var plugin), $"expected '{type}' registered");
            Assert.Equal(type, plugin!.TypeName);
        }
    }

    [Fact]
    public void TryGet_UnknownType_ReturnsFalse()
    {
        var reg = PluginRegistry.CreateWithBuiltins();
        Assert.False(reg.TryGet("nonexistent", out _));
    }

    [Fact]
    public void Register_DuplicateTypeName_FirstWins_AndRecordsShadowWarning()
    {
        var reg = new PluginRegistry();
        var first = new ShellJobPlugin();
        var second = new AnotherShell();
        reg.Register(first);
        reg.Register(second);

        Assert.True(reg.TryGet("shell", out var got));
        Assert.Same(first, got);                                  // first-registered wins
        Assert.Contains(reg.ShadowWarnings, w => w.Contains("shell"));
    }

    private sealed class AnotherShell : IJobPlugin
    {
        public string TypeName => "shell";
        public string DisplayName => "Impostor Shell";
        public JobSchema GetSchema() => new(TypeName, DisplayName, Array.Empty<JobParamSpec>());
        public JobValidation Validate(JobParameters p) => JobValidation.Valid();
        public Task<JobResult> ExecuteAsync(JobParameters p, IJobContext c, CancellationToken ct)
            => Task.FromResult(new JobResult { Success = true });
    }
}

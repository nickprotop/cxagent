using CxAgent.Core.Models;
using CxAgent.Core.Jobs;
using CxAgent.Core.Jobs.Builtin;
using Xunit;

namespace CxAgent.Tests;

public class JobRegistryTests
{
    [Fact]
    public void CreateWithBuiltins_RegistersTheFourSelfContainedExecutors()
    {
        var reg = JobRegistry.CreateWithBuiltins();
        foreach (var type in new[] { "shell", "file", "wait", "http" })
        {
            Assert.True(reg.TryGet(type, out var executor), $"expected '{type}' registered");
            Assert.Equal(type, executor!.TypeName);
        }
    }

    [Fact]
    public void TryGet_UnknownType_ReturnsFalse()
    {
        var reg = JobRegistry.CreateWithBuiltins();
        Assert.False(reg.TryGet("nonexistent", out _));
    }

    [Fact]
    public void Register_DuplicateTypeName_FirstWins_AndRecordsShadowWarning()
    {
        var reg = new JobRegistry();
        var first = new ShellJobExecutor();
        var second = new AnotherShell();
        reg.Register(first);
        reg.Register(second);

        Assert.True(reg.TryGet("shell", out var got));
        Assert.Same(first, got);                                  // first-registered wins
        Assert.Contains(reg.ShadowWarnings, w => w.Contains("shell"));
    }

    private sealed class AnotherShell : IJobExecutor
    {
        public string TypeName => "shell";
        public string DisplayName => "Impostor Shell";
        public JobSchema GetSchema() => new(TypeName, DisplayName, Array.Empty<JobParamSpec>());
        public JobValidation Validate(JobParameters p) => JobValidation.Valid();
        public Task<JobResult> ExecuteAsync(JobParameters p, IJobContext c, CancellationToken ct)
            => Task.FromResult(new JobResult { Success = true });
    }
}

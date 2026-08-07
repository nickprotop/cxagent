using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

public class AiDiagnosisTests
{
    [Fact]
    public void Empty_Modification_HasNoChanges()
    {
        var m = DagModification.Empty;
        Assert.Empty(m.JobsToAdd);
        Assert.Empty(m.JobIdsToRemove);
        Assert.Empty(m.ParameterChanges);
    }

    [Fact]
    public void Retry_NeedsNoModification()
    {
        var d = new AiDiagnosis("exit 1: missing file", RecoveryAction.Retry, "transient", null);
        Assert.Equal(RecoveryAction.Retry, d.Action);
        Assert.Null(d.Modification);
    }

    [Fact]
    public void ModifyAndRetry_CarriesParameterChanges()
    {
        var p = new JobParameters();
        p.Values["command"] = "echo fixed";
        var mod = new DagModification(
            System.Array.Empty<Job>(),
            System.Array.Empty<string>(),
            new Dictionary<string, JobParameters> { ["j1"] = p });

        var d = new AiDiagnosis("wrong command", RecoveryAction.ModifyAndRetry, "typo in path", mod);

        Assert.Equal("echo fixed", d.Modification!.ParameterChanges["j1"].Get<string>("command"));
    }
}

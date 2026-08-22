using CxAgent.Core.Jobs;
using Xunit;

namespace CxAgent.Tests;

public class JobSchemaTests
{
    [Fact]
    public void Valid_HasNoErrors()
    {
        var v = JobValidation.Valid();
        Assert.True(v.IsValid);
        Assert.Empty(v.Errors);
    }

    [Fact]
    public void Invalid_CarriesErrors()
    {
        var v = JobValidation.Invalid("bad path", "missing dest");
        Assert.False(v.IsValid);
        Assert.Equal(new[] { "bad path", "missing dest" }, v.Errors);
    }

    [Fact]
    public void JobSchema_HoldsParamSpecs()
    {
        var schema = new JobSchema("shell", "Shell Command", new[]
        {
            new JobParamSpec("command", "string", Required: true, "The command to run"),
        });
        Assert.Equal("shell", schema.TypeName);
        var p = Assert.Single(schema.Params);
        Assert.Equal("command", p.Name);
        Assert.True(p.Required);
    }
}

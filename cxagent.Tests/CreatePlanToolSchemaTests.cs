using CxAgent.Core.Plugins;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class CreatePlanToolSchemaTests
{
    private static string SchemaJson()
    {
        var def = CreatePlanTool.BuildDefinition(PluginRegistry.CreateWithBuiltins());
        return def.InputSchema.GetRawText();
    }

    [Fact]
    public void TypeField_EnumeratesTheRegistrysPluginTypes_NotAProseHint()
    {
        var json = SchemaJson();
        // Every built-in must be discoverable by the model as a legal `type` value.
        foreach (var t in new[] { "shell", "file", "http", "wait" })
            Assert.Contains($"\"{t}\"", json);
    }

    [Fact]
    public void ParamsAreDocumented_PerPluginType_WithRequiredFlags()
    {
        var json = SchemaJson();
        // The exact params the plugins declare must reach the model — these are the ones a plan
        // omitted in a live drive, causing a job to fail validation.
        Assert.Contains("command", json);   // shell, required
        Assert.Contains("action", json);    // file, required
        Assert.Contains("seconds", json);   // wait
        Assert.Contains("url", json);       // http
    }

    [Fact]
    public void BuildDefinition_ReflectsTheRegistry_NotAHardcodedList()
    {
        // A registry with ONE plugin must not advertise the other three.
        var only = new PluginRegistry();
        only.Register(new CxAgent.Core.Plugins.Builtin.WaitJobPlugin());

        var json = CreatePlanTool.BuildDefinition(only).InputSchema.GetRawText();

        Assert.Contains("wait", json);
        Assert.DoesNotContain("\"shell\"", json);
    }
}

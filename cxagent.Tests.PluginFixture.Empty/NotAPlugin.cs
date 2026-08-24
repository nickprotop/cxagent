namespace CxAgent.Tests.PluginFixture.Empty;

/// <summary>An ordinary class in an assembly that implements no IPlugin at all — exists only so
/// the assembly is not literally empty.</summary>
public sealed class NotAPlugin
{
    public int Value => 1;
}

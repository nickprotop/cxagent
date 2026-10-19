namespace CxAgent.Plugins.Testing;

/// <summary>
/// One thing wrong with a plugin folder. <see cref="Field"/> names where — a sidecar field
/// (<c>pluginContract</c>, <c>tools[2].name</c>), or <c>sidecar</c>, <c>file</c>, <c>load</c> — so an
/// author goes straight to the line to change.
/// </summary>
public sealed record PluginProblem(string Field, string Message)
{
    public override string ToString() => $"{Field}: {Message}";
}

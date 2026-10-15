using CxAgent.Core.Permissions;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What Escape does on a permission prompt: "no" for the model's own calls, but for a prompt a
/// command's expansion raised, stop the command — the user pressing Escape there means "don't send
/// this", and denying one line would send the rest.
/// </summary>
public class PromptEscapeTests
{
    private static PermissionRequest Shell(bool fromCommand) =>
        new(PermissionKind.Shell, "git diff", null) { RaisedByCommand = fromCommand };

    [Fact]
    public void OnTheModelsOwnCall_EscapeDenies()
    {
        var did = "";
        WindowPermissionPrompt.EscapeAction(Shell(false), () => did = "deny", () => did = "stop")();

        Assert.Equal("deny", did);
    }

    [Fact]
    public void OnACommandsLine_EscapeStopsTheCommand()
    {
        var did = "";
        WindowPermissionPrompt.EscapeAction(Shell(true), () => did = "deny", () => did = "stop")();

        Assert.Equal("stop", did);
    }

    /// <summary>No session to stop — a prompt with nothing to cancel still answers "no".</summary>
    [Fact]
    public void WithNothingToStop_EscapeStillDenies()
    {
        var did = "";
        WindowPermissionPrompt.EscapeAction(Shell(true), () => did = "deny", null)();

        Assert.Equal("deny", did);
    }
}

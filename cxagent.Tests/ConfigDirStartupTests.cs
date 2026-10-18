using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>A config folder cxagent cannot use is a mistake to tell the user about, not a crash.</summary>
public class ConfigDirStartupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("config-dir-").FullName;
    public void Dispose() => TempDirectory.Delete(_dir);

    /// <summary>
    /// A FILE WHERE THE FOLDER SHOULD BE — a mistyped path, or a path that names something else —
    /// used to end in an unhandled IOException and a core dump before anything was drawn. It is the
    /// user's path, so the answer is one line naming it and a failing exit code.
    /// </summary>
    [Fact]
    public void AFileGivenAsTheConfigFolder_IsRefusedInOneLine()
    {
        var file = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(file, "");
        var error = new StringWriter();
        var original = Console.Error;
        int code;
        try
        {
            Console.SetError(error);
            code = AppBootstrap.Run(["--config-dir", file, "--sessions"]);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal(1, code);
        Assert.Contains("cxagent: cannot use", error.ToString());
        Assert.Contains(file, error.ToString());
    }
}

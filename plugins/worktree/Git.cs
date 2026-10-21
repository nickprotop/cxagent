using System.Diagnostics;

namespace CxAgent.Plugins.Worktree;

/// <summary>What one git command did.</summary>
public sealed record GitResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;

    /// <summary>Git's own words for why it failed, for a message the model can act on.</summary>
    public string Why => (Error.Trim().Length > 0 ? Error : Output).Trim();
}

/// <summary>
/// Runs git, and nothing else.
///
/// <para>AN ARGUMENT LIST, NEVER A COMMAND LINE. Every value that reaches git — a worktree's name,
/// a base ref, a commit message — goes in as one argument, so none of them is ever read by a shell
/// and none can become a second command.</para>
///
/// <para>NO PROMPTS. A git that wanted a password or an editor would wait for a terminal that is
/// not there, and the tool call would hang until its timeout. <c>GIT_TERMINAL_PROMPT=0</c> makes it
/// fail instead, and every command that could open an editor is given its message.</para>
/// </summary>
public sealed class Git(string executable, Action<int> onProcess)
{
    /// <summary>Long enough for <c>worktree add</c> to check out a large repository; a git still
    /// running past this is stuck, not slow.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public GitResult Run(string folder, params string[] args)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_EDITOR"] = "true";

        Process? git;
        try
        {
            git = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return new GitResult(-1, "", $"could not run {executable}: {ex.Message}");
        }
        if (git is null) return new GitResult(-1, "", $"could not run {executable}.");

        using (git)
        {
            onProcess(git.Id);
            // DRAINED WHILE IT RUNS: a diff larger than the pipe would block git on its write while
            // this waited for it to exit.
            var output = git.StandardOutput.ReadToEndAsync();
            var error = git.StandardError.ReadToEndAsync();
            if (!git.WaitForExit(Timeout))
            {
                try { git.Kill(entireProcessTree: true); } catch (Exception) { }
                return new GitResult(-1, "", $"git {args.FirstOrDefault()} did not finish within {Timeout.TotalMinutes:0} minutes.");
            }
            return new GitResult(git.ExitCode, output.Result, error.Result);
        }
    }
}

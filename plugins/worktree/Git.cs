using System.Diagnostics;
using System.Text;

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
/// <para>NO TERMINAL. A git that wanted a password, an editor, or a hook that reads input would wait
/// for a terminal that is cxagent's own TUI. <c>GIT_TERMINAL_PROMPT=0</c> makes git fail instead of
/// asking, <c>GIT_EDITOR=true</c> answers any editor at once, and standard input is closed, so a hook
/// that reads it reads nothing.</para>
///
/// <para>THE C LOCALE, so git's words are the ones this plugin looks for — "Fast-forward" is matched
/// in a merge's output — and WITHOUT OPTIONAL LOCKS, so a <c>status</c> here never takes the index lock
/// from a sub-agent running git in the same worktree.</para>
/// </summary>
/// <param name="executable">The git command — <c>git</c>, or the path the settings give.</param>
/// <param name="onProcess">Told each process's id as it starts, so the host can reap it.</param>
public sealed class Git(string executable, Action<int> onProcess)
{
    /// <summary>Long enough for <c>worktree add</c> to check out a large repository; a git still
    /// running past this is stuck, not slow.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long to wait for git's output to close once git has exited. A hook that started a process
    /// in the background hands it git's output pipe, and the pipe stays open as long as that process
    /// runs; what git itself wrote has arrived long before this.
    /// </summary>
    private static readonly TimeSpan Drain = TimeSpan.FromSeconds(5);

    private CancellationToken _cancel;

    /// <summary>
    /// This runner, stopping its git when <paramref name="ct"/> is cancelled — the tool call's token,
    /// so a user's Escape ends a slow checkout instead of waiting out the timeout.
    /// </summary>
    public Git For(CancellationToken ct) => new(executable, onProcess) { _cancel = ct };

    public GitResult Run(string folder, params string[] args) => Run(folder, int.MaxValue, args);

    /// <summary>
    /// Runs git, keeping at most <paramref name="maxOutput"/> characters of what it prints. The rest
    /// is read and dropped, so git is never blocked on a full pipe — and a diff of a whole rewritten
    /// tree is never held in memory only to be cut.
    /// </summary>
    public GitResult Run(string folder, int maxOutput, params string[] args)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_EDITOR"] = "true";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["LC_ALL"] = "C";

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
            // ONLY A PROCESS STILL RUNNING IS REGISTERED. Most git commands here finish in
            // milliseconds, often before this line; one already gone has nothing to reap, and a
            // host older than 0.9.44 puts "could not be recorded" in the user's transcript for it.
            if (!git.HasExited)
            {
                // A HOST OLDER THAN 0.9.44 THROWS when the process exits in the instant it is being
                // recorded. The process is gone, so there is nothing left to record, and the command
                // itself has done its work.
                try { onProcess(git.Id); }
                catch (System.ComponentModel.Win32Exception) { }
            }
            git.StandardInput.Close();

            // DRAINED WHILE IT RUNS: output larger than the pipe would block git on its write while
            // this waited for it to exit.
            var output = Read(git.StandardOutput, maxOutput);
            var error = Read(git.StandardError, int.MaxValue);

            var clock = Stopwatch.StartNew();
            while (!git.WaitForExit(200))
            {
                if (_cancel.IsCancellationRequested || clock.Elapsed > Timeout)
                {
                    try { git.Kill(entireProcessTree: true); } catch (Exception) { }
                    return new GitResult(-1, "", _cancel.IsCancellationRequested
                        ? $"git {args.FirstOrDefault()} was stopped."
                        : $"git {args.FirstOrDefault()} did not finish within {Timeout.TotalMinutes:0} minutes.");
                }
            }

            Task.WaitAll([output, error], Drain);
            return new GitResult(git.ExitCode,
                output.IsCompletedSuccessfully ? output.Result : "",
                error.IsCompletedSuccessfully ? error.Result : "");
        }
    }

    private static Task<string> Read(StreamReader reader, int max) => Task.Run(() =>
    {
        var kept = new StringBuilder();
        var buffer = new char[8192];
        int n;
        while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
            if (kept.Length < max) kept.Append(buffer, 0, Math.Min(n, max - kept.Length));
        return kept.ToString();
    });
}

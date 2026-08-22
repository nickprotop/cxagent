using CxAgent.UI;

// LAST-RESORT CRASH LOG. Without this, a crash in a background task makes the process VANISH with
// nothing written down: no goal-terminal line, no error, tokens frozen, the TUI frame still painted
// on a terminal whose owner is gone. Observed live, it reads as a hang — twenty minutes before a
// process check showed there was nothing left to hang.
//
// Nothing else catches this. An exception escaping a background task — the orchestrator loop, a
// scheduler continuation, a provider call — terminates the runtime, and the TUI owns the screen, so
// whatever the runtime prints is overwritten by the next frame. The crash is invisible BOTH ways:
// no log on disk, and nothing readable on screen.
//
// Written to a file rather than the console for exactly that reason, and appended so a crash loop
// leaves a history instead of each run erasing its predecessor's evidence.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    try
    {
        var dir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
            ? Path.Combine(x, "cxagent")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                           ".config", "cxagent");
        Directory.CreateDirectory(dir);
        File.AppendAllText(Path.Combine(dir, "crash.log"),
            $"=== {DateTimeOffset.Now:O} (terminating={e.IsTerminating}) ==={Environment.NewLine}"
            + $"{e.ExceptionObject}{Environment.NewLine}{Environment.NewLine}");
    }
    catch
    {
        // The handler must never throw: it runs while the runtime is already tearing down, and an
        // exception here would replace a recorded crash with an unrecorded one.
    }
};

// An async void or un-awaited Task that faults reaches HERE, not the handler above — and by default
// that no longer kills the process, so it would otherwise be swallowed in total silence. Same
// destination, marked so the two are distinguishable in the file.
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    try
    {
        var dir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
            ? Path.Combine(x, "cxagent")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                           ".config", "cxagent");
        Directory.CreateDirectory(dir);
        File.AppendAllText(Path.Combine(dir, "crash.log"),
            $"=== {DateTimeOffset.Now:O} UNOBSERVED TASK ==={Environment.NewLine}"
            + $"{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        e.SetObserved();
    }
    catch { }
};

return AppBootstrap.Run(args);

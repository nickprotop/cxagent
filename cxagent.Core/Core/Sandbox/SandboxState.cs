namespace CxAgent.Core.Sandbox;

/// <summary>
/// A session's sandbox: its settings, the machine's folders, and whether bubblewrap works — asked once, on first
/// need, and again when a command's own start fails.
///
/// <para>ON THE PERMISSION POLICY, like the hooks, because the gate and the shell executor both reach the policy
/// and must reach the same answer from it — see <see cref="ShellSandbox"/>. A sub-agent's policy reads its
/// parent's.</para>
/// </summary>
public sealed class SandboxState
{
    private readonly Func<SandboxStatus> _probe;
    private readonly object _gate = new();
    private SandboxStatus? _status;

    /// <param name="settings">The user's settings.</param>
    /// <param name="home">The machine's folders.</param>
    /// <param name="probe">Asks whether bubblewrap works; only called when the settings turn the sandbox on.</param>
    public SandboxState(SandboxSettings settings, SandboxHome home, Func<SandboxStatus> probe)
    {
        Settings = settings;
        Home = home;
        _probe = probe;
    }

    /// <summary>A state for this machine, probing the real bubblewrap.</summary>
    /// <param name="settings">The user's settings.</param>
    /// <param name="configDir">The config folder in use, hidden from commands.</param>
    public static SandboxState ForThisMachine(SandboxSettings settings, string? configDir) =>
        new(settings, SandboxHome.FromEnvironment(configDir), () => SandboxProbe.Run(SandboxProbeRequest.ThisMachine()))
        {
            ConfigKeyVariables = KeyVariablesIn(configDir),
        };

    /// <summary>The variables config.json reads values from, or none when there is no such file to read.</summary>
    private static IReadOnlyList<string> KeyVariablesIn(string? configDir)
    {
        if (configDir is null) return [];
        try
        {
            var file = Path.Combine(configDir, "config.json");
            return File.Exists(file) ? Llm.ConfigVariable.EnvNames(File.ReadAllText(file)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// The variables config.json reads a value from with <c>{env:NAME}</c>. Kept from contained commands whatever
    /// they are called: a key named <c>LOCAL_LLM</c> matches no credential pattern and is still a key.
    /// </summary>
    public IReadOnlyCollection<string> ConfigKeyVariables { get; init; } = [];

    /// <summary>The user's settings.</summary>
    public SandboxSettings Settings { get; }

    /// <summary>The machine's folders.</summary>
    public SandboxHome Home { get; }

    /// <summary>Where the sandbox stands. The first read probes; <see cref="SandboxStatusKind.Off"/> never does.</summary>
    public SandboxStatus Status
    {
        get
        {
            if (!Settings.Enabled) return SandboxStatus.Off;
            lock (_gate) return _status ??= _probe();
        }
    }

    /// <summary>The status if it has been asked already, without probing — for a screen that must not wait.</summary>
    public SandboxStatus? StatusIfKnown
    {
        get
        {
            if (!Settings.Enabled) return SandboxStatus.Off;
            lock (_gate) return _status;
        }
    }

    /// <summary>Forgets the answer, so the next read asks again — after a command's own start failed.</summary>
    public void Reprobe()
    {
        lock (_gate) _status = null;
    }

    /// <summary>Set once the session has said that commands are not contained, so it says it once.</summary>
    public bool FallbackNoticeShown { get; set; }
}

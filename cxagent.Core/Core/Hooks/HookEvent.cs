namespace CxAgent.Core.Hooks;

/// <summary>Where a hook was configured. The order is the order rewrites apply in.</summary>
public enum HookSource
{
    /// <summary>The <c>hooks</c> block of the user's <c>config.json</c>.</summary>
    User,
    /// <summary>The repository's shared <c>.cxagent/hooks.json</c>.</summary>
    Project,
    /// <summary>The repository's <c>.cxagent/hooks.local.json</c>, kept on one machine.</summary>
    Local,
    /// <summary>An embedding app's <c>AgentConfig.Hooks</c>, as JSON or as handlers.</summary>
    Library,
}

/// <summary>
/// The event names of the hook contract, and which of them cxagent fires.
///
/// <para>ALL OF THEM ARE KNOWN, not only the ones that fire. A hooks file written for the whole contract
/// names events cxagent has nothing to fire on yet; knowing the name lets that hook load and show as
/// "not supported here" rather than as a typo, and it starts working the day its event is wired.</para>
/// </summary>
public static class HookEvents
{
    public const string SessionStart = "SessionStart";
    public const string SessionEnd = "SessionEnd";
    public const string UserPromptSubmit = "UserPromptSubmit";
    public const string PreToolUse = "PreToolUse";
    public const string PermissionRequest = "PermissionRequest";
    public const string PostToolUse = "PostToolUse";
    public const string PostToolUseFailure = "PostToolUseFailure";
    public const string Stop = "Stop";
    public const string StopFailure = "StopFailure";
    public const string Notification = "Notification";
    public const string UserPromptExpansion = "UserPromptExpansion";
    public const string PermissionDenied = "PermissionDenied";
    public const string PostToolBatch = "PostToolBatch";
    public const string MessageDisplay = "MessageDisplay";
    public const string SubagentStart = "SubagentStart";
    public const string SubagentStop = "SubagentStop";
    public const string TaskCreated = "TaskCreated";
    public const string TaskCompleted = "TaskCompleted";
    public const string InstructionsLoaded = "InstructionsLoaded";
    public const string ConfigChange = "ConfigChange";
    public const string CwdChanged = "CwdChanged";
    public const string PreCompact = "PreCompact";
    public const string PostCompact = "PostCompact";
    public const string PreModelSwitch = "PreModelSwitch";
    public const string PostModelSwitch = "PostModelSwitch";

    /// <summary>Every event name in the contract, in its reference order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        SessionStart, "Setup", UserPromptSubmit, "UserPromptExpansion", PreToolUse, PermissionRequest,
        "PermissionDenied", PostToolUse, PostToolUseFailure, "PostToolBatch", Notification, "MessageDisplay",
        "SubagentStart", "SubagentStop", "TaskCreated", "TaskCompleted", Stop, StopFailure, "TeammateIdle",
        "InstructionsLoaded", "ConfigChange", "CwdChanged", "DirectoryAdded", "FileChanged", "WorktreeCreate",
        "WorktreeRemove", "PreCompact", "PostCompact", "PreModelSwitch", "PostModelSwitch", "Elicitation",
        "ElicitationResult", SessionEnd,
    ];

    /// <summary>
    /// The events cxagent has nothing to fire on: no init mode, no agent teams, no MCP elicitation, no
    /// <c>/add-dir</c>, no file watch list, and no worktree creation of its own (a worktree plugin makes them).
    /// A hook on one loads and shows as not supported here.
    /// </summary>
    private static readonly HashSet<string> NotFired = new(StringComparer.Ordinal)
    {
        "Setup", "TeammateIdle", "Elicitation", "ElicitationResult", "DirectoryAdded", "FileChanged",
        "WorktreeCreate", "WorktreeRemove",
    };

    private static readonly HashSet<string> Firing = new(All.Where(e => !NotFired.Contains(e)), StringComparer.Ordinal);

    /// <summary>Whether cxagent fires this event today.</summary>
    public static bool Fires(string name) => Firing.Contains(name);

    /// <summary>Whether the name is one of the contract's events at all.</summary>
    public static bool Known(string name) => All.Contains(name, StringComparer.Ordinal);

    /// <summary>The events whose hooks may stop what is happening — where <c>onFailure: "block"</c> means something.</summary>
    public static bool CanBlock(string name) => name is UserPromptSubmit or PreToolUse or PermissionRequest or Stop
        or SubagentStop or UserPromptExpansion or PostToolBatch or TaskCreated or TaskCompleted or ConfigChange
        or PreCompact or PreModelSwitch;

    /// <summary>The events matched against a tool name.</summary>
    public static bool IsToolEvent(string name) => name is PreToolUse or PermissionRequest or PostToolUse or PostToolUseFailure
        or PermissionDenied;
}

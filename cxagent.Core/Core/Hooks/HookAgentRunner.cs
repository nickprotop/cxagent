using CxAgent.Core.Agents;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;

namespace CxAgent.Core.Hooks;

/// <summary>
/// Runs an <c>agent</c> hook's verifier: a short-lived agent that may read the project before it answers.
///
/// <para>READ-ONLY, AND ASKS NOBODY. It is offered <c>read_file</c>, <c>glob</c> and <c>grep</c> and nothing
/// else, so it can look at test output or a file the event names but cannot change anything — which is why it
/// needs no permission gate, and why it never interrupts the user with a prompt while it decides.</para>
///
/// <para>NO HOOKS OF ITS OWN. It is not wired to the session's hooks: a PreToolUse agent hook that fired on its
/// own verifier's reads would never finish.</para>
/// </summary>
public static class HookAgentRunner
{
    /// <summary>The verifier's turn cap, the contract's.</summary>
    public const int MaxTurns = 50;

    private static readonly ToolSelection ReadOnly = new([Tool.ReadFile, Tool.Glob, Tool.Grep]);

    /// <summary>Runs the verifier and returns its final text, where its <c>{ok, reason}</c> answer is.</summary>
    public static async Task<string> RunAsync(HookAgentRequest request, CancellationToken ct)
    {
        var agent = new Agent(new AgentSetup
        {
            // ITS SPEND IS THE SESSION'S: every model reply the verifier gets is recorded where the hook's other
            // costs are, so an agent hook on every tool call is not a bill nobody sees.
            Model = new AgentModel(request.Spend is { } spend ? new Metered(request.Model, spend) : request.Model, new TokenLedger()),
            Outputs = new AgentOutputs(new BufferedChatSink(), new BufferedJobPanel()),
            Identity = new AgentIdentity { WorkingDir = request.ProjectDir, IsSubAgent = true, Label = "hook verifier" },
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins(null, new ProjectOnly(request.ProjectDir))) { Selection = ReadOnly },
            Guards = new AgentGuards(),
            Brief = new AgentBrief(),
            Limits = new AgentLimits(MaxTurns),
            Conversation = null,
        });
        var result = await agent.SendAsync(request.Prompt, ct);
        return result.Text;
    }

    /// <summary>A provider that reports each reply's usage — the verifier's model, metered for the session.</summary>
    private sealed class Metered(ILlmProvider inner, Action<LlmUsage> spend) : ILlmProvider
    {
        public string ProviderId => inner.ProviderId;
        public string ModelId => inner.ModelId;
        public string DisplayName => inner.DisplayName;
        public bool SupportsToolCalling => inner.SupportsToolCalling;
        public bool SupportsStreaming => inner.SupportsStreaming;

        public async Task<LlmResponse> ChatAsync(List<Models.ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
        {
            var response = await inner.ChatAsync(messages, tools, ct);
            if (response.Usage is { } usage) spend(usage);
            return response;
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<Models.ChatMessage> messages, List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var chunk in inner.ChatStreamAsync(messages, tools, ct))
            {
                if (chunk.Usage is { } usage) spend(usage);
                yield return chunk;
            }
        }
    }

    /// <summary>
    /// The verifier's gate: a read inside the project is allowed, and everything else is refused without asking.
    ///
    /// <para>INSIDE THE PROJECT ONLY. A repository's agent hook writes the verifier's instructions, and a verifier
    /// that could read <c>~/.aws/credentials</c> would hand them to a refusal reason the model reads and any later
    /// hook can post on. It asks nobody, because nobody is watching it decide.</para>
    /// </summary>
    private sealed class ProjectOnly(string projectDir) : Permissions.IPermissionGate
    {
        private readonly string _root = Permissions.PathResolver.RealFolder(projectDir);

        public Task<Permissions.PermissionOutcome> RequestAsync(Permissions.PermissionRequest request, CancellationToken ct)
        {
            var inside = request.Kind == Permissions.PermissionKind.FileRead
                         && Permissions.PathResolver.Contains(_root, RealPath(request.What));
            return Task.FromResult(inside ? Permissions.PermissionOutcome.Allow
                : new Permissions.PermissionOutcome(false, "hook", "a hook's verifier reads only inside the project"));
        }

        private static string RealPath(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var target = File.Exists(full) ? new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName : null;
                return target ?? full;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return path; }
        }
    }
}

using CxAgent.Core.Models;
using CxAgent.Core.Permissions;

namespace CxAgent.Core.Plugins;

/// <summary>
/// Whether a call to a plugin's tool asks the user, and with what words — the ONE decision, made for
/// a session's plugin tool and for the plugin harness alike, so what `cxagent plugin call` prints is
/// what a user would be asked rather than a second reading of the same rules.
///
/// <para>"ALWAYS" IS OFFERED, AND THE USER OWNS THAT DECISION. Withholding it would not make a plugin
/// safer: the binary was already approved at load, against a hash of its whole load set, which is the
/// boundary that actually decides whether this code runs at all. What withholding DOES do is make a
/// trusted plugin's every call a question, and a tool that interrupts on all of them is one users
/// route around by disabling gating wholesale — a worse outcome than the standing grant it was
/// avoiding.</para>
///
/// <para>THE RULE NAMES THE PLUGIN, NOT ONLY THE TOOL. A bare "tool lsp_definition" would survive
/// uninstalling this plugin and installing a different one that happens to declare the same name,
/// handing the newcomer a grant the user gave someone else. Built-in tools can use the bare form
/// (GatedAgentTool) because nothing else can ever claim their names.</para>
/// </summary>
internal static class PluginGateQuestion
{
    /// <summary>The tool a gate is for, and the two strings every branch below shares.</summary>
    private sealed record GateTarget(PluginManifest Manifest, PluginToolManifest Tool)
    {
        public string Rule => $"plugin {Manifest.Name} tool {Tool.Name}";
        public string Plain => $"run '{Tool.Name}' from the '{Manifest.Name}' plugin";
    }

    public static PermissionRequest? For(PluginManifest manifest, PluginToolManifest tool, IPlugin instance, JobParameters call)
    {
        var target = new GateTarget(manifest, tool);
        return tool.Gated switch
        {
            PluginGating.Never => null,
            PluginGating.Always => Ask(target, target.Plain, tool.AlwaysAskable),
            _ => Dynamic(target, instance, call),
        };
    }

    /// <summary>
    /// The per-call decision, asked of the plugin itself.
    ///
    /// <para>FAILURE ASKS AND WITHHOLDS "ALWAYS". A gate that throws has told us nothing about this
    /// call, so the safe reading is "ask" — and a broken gate must not be able to earn a standing grant
    /// while broken, which is the one case where a manifest saying alwaysAskable:true is overruled.</para>
    /// </summary>
    private static PermissionRequest? Dynamic(GateTarget target, IPlugin instance, JobParameters call)
    {
        // NOT A CAST. ManagedPluginLoader refuses a plugin that declares "dynamic" without a gate, so
        // reaching here without one is a host that skipped that check — an embedder wiring a plugin by
        // hand, or a loader yet to grow the same refusal. Asking is the only safe reading: the
        // manifest said this call might need permission and nothing can now say it does not.
        if (instance is not IPluginGateSource source)
            return Ask(target, target.Plain + " (it declares a per-call permission check but provides none)",
                alwaysAskable: false);

        PluginGate? gate;
        try
        {
            gate = source.Gate(target.Tool.Name, call);
        }
        catch (Exception)
        {
            return Ask(target, target.Plain + " (its permission check failed)", alwaysAskable: false);
        }

        if (gate is null) return null;

        // THE PLUGIN'S WORDING, NEVER ITS SCOPE. Display is the plugin's — it saw the arguments and
        // can name the file. Kind and AlwaysRule are built here, so no plugin can turn its own prompt
        // into a grant over shell, files or anything else it does not own.
        //
        // ALWAYSASKABLE IS AN AND: the manifest is a floor. A sidecar that withheld "Always" is a
        // promise the user read before approving the load, and a runtime call does not get to hand it
        // back.
        return Ask(target, string.IsNullOrWhiteSpace(gate.Display) ? target.Plain : gate.Display,
            gate.AlwaysAskable && target.Tool.AlwaysAskable,
            // THE COMMAND, NOT THE SENTENCE AROUND IT. ActionClassifier reads PermissionRequest.What,
            // which is Subject ?? Display — so without this a plugin's annotation is judged on prose
            // the plugin wrote to explain itself rather than on the thing that will actually run.
            gate.Subject);
    }

    /// <summary>
    /// A NULL AlwaysRule IS HOW "no Always button" IS EXPRESSED — see PermissionRequest's own doc. The
    /// plugin declaring alwaysAskable:false is marking its own sharp edge: the tool it believes should
    /// never hold a standing grant, which is a judgement only its author can make.
    /// </summary>
    private static PermissionRequest Ask(GateTarget target, string display, bool alwaysAskable, string? subject = null) =>
        new(PermissionKind.Tool, display, AlwaysRule: alwaysAskable ? target.Rule : null, Subject: subject);
}

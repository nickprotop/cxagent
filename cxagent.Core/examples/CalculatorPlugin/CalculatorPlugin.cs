using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CalculatorPlugin;

/// <summary>
/// A plugin small enough to read in one sitting, showing every part of the contract.
///
/// <para>THE ARITHMETIC IS NOT THE POINT — the model can already add. What a calculator gives an
/// example is that nothing about the DOMAIN competes for your attention: every line you read here is
/// plugin machinery, because there is no other kind of line in the file.</para>
///
/// <para>ADDING TWO NUMBERS ASKS THE USER FOR PERMISSION. That is ridiculous on purpose. A gate on
/// something genuinely dangerous teaches you what the danger was; a gate on <c>2 + 2</c> can only
/// teach you the mechanism, which is the part that transfers to the plugin you actually write.
/// <c>calc_multiply</c> sits beside it ungated, so one run shows you both paths.</para>
///
/// <para>The same calculator exists as a C ABI plugin in <c>../CalculatorAbiPlugin</c>. Reading the
/// two together shows what the out-of-process boundary costs, since nothing else differs.</para>
/// </summary>
public sealed class CalculatorPlugin : IPlugin
{
    private IPluginContext _context = null!;

    /// <summary>How many decimal places results are rounded to, from <c>settings.precision</c>.</summary>
    private int _precision = DefaultPrecision;

    private const int DefaultPrecision = 2;

    /// <summary>
    /// Runs once, before <see cref="Start"/>. Returns what this plugin offers.
    ///
    /// <para>THE MANIFEST IS READ FROM THE SIDECAR RATHER THAN RESTATED HERE. <c>ManagedPluginLoader</c>
    /// reads <c>calculator.plugin.json</c> before calling this and refuses the load if the two
    /// disagree — so writing the manifest twice means writing the same JSON twice and keeping both
    /// true forever. Parsing the file you already ship makes drift impossible instead of detectable.</para>
    ///
    /// <para>BESIDE THIS ASSEMBLY, NOT BESIDE THE HOST EXECUTABLE. <c>AppContext.BaseDirectory</c> is
    /// the running process's own folder, which is where cxagent lives, not where your plugin was
    /// found. They coincide only when a plugin sits in the app's output directory — which is what a
    /// unit test does and what production never does.</para>
    /// </summary>
    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct)
    {
        _context = context;

        var here = Path.GetDirectoryName(typeof(CalculatorPlugin).Assembly.Location)!;
        var parsed = PluginManifest.Parse(File.ReadAllText(Path.Combine(here, "calculator.plugin.json")));
        if (!parsed.IsSuccess || parsed.Manifest is null)
            throw new InvalidOperationException(
                $"calculator's own sidecar failed to parse: {string.Join("; ", parsed.Errors)}");

        // SETTINGS ARE THIS PLUGIN'S OWN CONFIG BLOCK, VERBATIM — whatever the user wrote under
        // `"settings"` for this plugin in config.json, handed over unread by cxagent. Reading your
        // behaviour from here rather than hardcoding it is what lets one binary serve two
        // configurations: the LSP plugin that ships with cxagent drives two different language
        // servers this way, with no branch on which one it is talking to.
        //
        // MISSING IS NOT AN ERROR. A plugin configured with no settings block at all gets an empty
        // object, so every read needs a default — and a default that works is the difference
        // between a plugin someone can try and one they must configure before it will start.
        _precision = ReadPrecision(context.Settings);

        // The logger reaches the user's transcript. Say what a person would want to know at load.
        _context.Logger.Log(
            $"calculator ready, rooted at {_context.WorkingDirectory}, precision {_precision}");

        return Task.FromResult(parsed.Manifest);
    }

    /// <summary>
    /// Runs after <see cref="Load"/>, before any tool is called — where a plugin spawns its
    /// processes, opens its connections, builds its index.
    ///
    /// <para>NOTHING TO DO HERE, AND THE METHOD IS STILL REQUIRED. A calculator holds no state, so
    /// this is one line; the shape matters because a plugin that DOES start something must do it
    /// here rather than lazily on first call. The tool list is fixed once a request begins, and a
    /// backend that comes up mid-turn would be a tool that worked on its second call and not its
    /// first.</para>
    ///
    /// <para>A PLUGIN THAT SPAWNS MUST REGISTER: <c>_context.RegisterChildProcess(pid)</c>, as soon
    /// as the process exists and BEFORE any handshake with it. cxagent records the pid and reaps it
    /// at the next startup if this session dies without reaching <see cref="Stop"/> — but only for
    /// what it was told about. Register late and a crash in between leaks the process forever.</para>
    /// </summary>
    public Task Start(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// One entry point for every tool, dispatching on <paramref name="toolName"/>.
    ///
    /// <para>THE PERMISSION PROMPT DOES NOT HAPPEN HERE. By the time this runs the user has already
    /// answered: <c>"gated": true</c> in the sidecar is what makes cxagent ask, and it asks on EVERY
    /// call — there is no "always allow" for a plugin tool, deliberately, because a stored rule would
    /// be a standing grant to code cxagent did not write. Your job is the work, not the asking.</para>
    ///
    /// <para>AN UNKNOWN NAME IS THIS PLUGIN'S OWN BUG. cxagent dispatches from the manifest this
    /// plugin returned, so a name that was never declared cannot arrive — throwing says so, where
    /// returning a plausible zero would hide it.</para>
    /// </summary>
    public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context,
        CancellationToken ct)
    {
        var a = call.Get<double>("a");
        var b = call.Get<double>("b");

        var answer = toolName switch
        {
            "calc_add" => a + b,
            "calc_multiply" => a * b,
            _ => throw new ArgumentException(
                $"calculator was asked for '{toolName}', which its own manifest does not declare."),
        };

        answer = Math.Round(answer, _precision);

        return Task.FromResult(new JobResult
        {
            Success = true,
            Output =
            {
                // content IS WHAT THE MODEL READS. A result carrying only structured keys reaches
                // the model as an empty string — not "no answer", nothing at all — and it explains
                // the silence rather than reporting it. Put the human-readable answer here and the
                // typed one beside it.
                // "F<precision>" RATHER THAN "G": settings.precision is meant to be VISIBLE in the
                // answer, and a general format would print 3 for both 2 and 4 decimal places,
                // leaving the setting read but with nothing to show for it.
                ["content"] = answer.ToString($"F{_precision}"),
                ["answer"] = answer,
            },
        });
    }

    /// <summary>
    /// Runs when the session ends or the plugin is unwired — close what <see cref="Start"/> opened.
    ///
    /// <para>MUST TOLERATE A BACKEND THAT IS ALREADY GONE. Stop is called on the way down, including
    /// after the thing it would close has crashed; throwing here turns a clean shutdown into a
    /// failed one. A calculator has nothing to close, so this is the trivial case of a method that
    /// is usually the fiddliest one in a real plugin.</para>
    /// </summary>
    public Task Stop(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Reads <c>settings.precision</c>, falling back to <see cref="DefaultPrecision"/>.
    ///
    /// <para>DEFENSIVE ABOUT SHAPE, NOT JUST ABSENCE. <see cref="IPluginContext.Settings"/> is
    /// whatever JSON the user typed — cxagent validates that it parses and nothing more, because it
    /// has no idea what your plugin expects. A settings block saying <c>"precision": "four"</c> is a
    /// typo, not an attack, and it should produce a working plugin at the default rather than a
    /// stack trace at load.</para>
    /// </summary>
    private static int ReadPrecision(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object ||
            !settings.TryGetProperty("precision", out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var precision))
            return DefaultPrecision;

        // Math.Round throws outside 0..15, and a plugin must not turn a config typo into a crash.
        return Math.Clamp(precision, 0, 15);
    }
}

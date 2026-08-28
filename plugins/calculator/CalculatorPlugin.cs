using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Plugins.Calculator;

/// <summary>
/// Arithmetic a model can trust.
///
/// <para>WHY THIS IS A PLUGIN AT ALL. A model approximates arithmetic — it does not compute it — so
/// a long calculation spread over several turns is confidently wrong somewhere in the middle. A CPU
/// does the same work exactly, instantly, for a fraction of the energy.</para>
///
/// <para>ONE TOOL TAKING A WHOLE EXPRESSION, because the failure is not 2+2: it is
/// (1847 * 0.0325) / 12 done as three calls with reasoning between them.</para>
/// </summary>
public sealed class CalculatorPlugin : IPlugin
{
    private PluginManifest? _manifest;

    /// <summary>
    /// THE SIDECAR IS THE MANIFEST, parsed and returned rather than restated. One JSON, true by
    /// construction — the host refuses a plugin whose code disagrees with the file it read before
    /// loading, and two hand-maintained copies is how that disagreement arrives.
    /// </summary>
    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct)
    {
        // BESIDE THIS ASSEMBLY, not AppContext.BaseDirectory: that is the host's folder, and a
        // plugin is loaded from wherever it was installed.
        var here = Path.GetDirectoryName(typeof(CalculatorPlugin).Assembly.Location)!;
        var sidecar = Path.Combine(here, "calculator.plugin.json");

        var parsed = PluginManifest.Parse(File.ReadAllText(sidecar));
        _manifest = parsed.Manifest
            ?? throw new InvalidOperationException(
                $"calculator.plugin.json could not be read: {string.Join("; ", parsed.Errors)}");

        return Task.FromResult(_manifest);
    }

    /// <summary>Nothing to start: the evaluator is a function, not a service.</summary>
    public Task Start(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Nothing to stop, for the same reason.</summary>
    public Task Stop(CancellationToken ct) => Task.CompletedTask;

    public Task<JobResult> Invoke(
        string toolName, JobParameters call, IJobContext context, CancellationToken ct)
    {
        // A FAILED CALL RATHER THAN A THROW, which differs from csharp-lsp — it raises
        // InvalidOperationException here, on IPlugin's note that the registry never dispatches a
        // name the manifest did not declare. Both are defensible; a failed call is chosen because
        // this plugin has exactly one tool, so the only way to reach this line is a caller that is
        // not the registry, and telling it what happened beats an exception it did not expect.
        if (!string.Equals(toolName, "calc_eval", StringComparison.Ordinal))
            return Task.FromResult(new JobResult
            {
                Success = false,
                ErrorMessage = $"this plugin has no tool named '{toolName}'.",
            });

        // A MISSING ARGUMENT IS A FAILED CALL, NOT AN EXCEPTION. JobParameters.Get throws
        // KeyNotFoundException on an absent key, and a model that omitted the argument can fix that
        // if told — where an unhandled exception just reports that the tool broke. csharp-lsp
        // catches the same thing for the same reason (CxagentLspPlugin.cs:155-166).
        string expression;
        try
        {
            expression = Argument(call, "expression");
        }
        catch (KeyNotFoundException)
        {
            return Task.FromResult(new JobResult
            {
                Success = false,
                ErrorMessage = "calc_eval needs an 'expression' argument — the calculation to do, "
                             + "like (2 + 3) * 4.",
            });
        }

        // A REFUSAL IS A FAILED CALL, not a success whose text describes a problem. A model reads a
        // successful result as an answer, and "∞" or a parse complaint presented as one is worse
        // than no answer at all.
        return Task.FromResult(Evaluator.Evaluate(expression) switch
        {
            EvalResult.Value(var text) => new JobResult
            {
                Success = true,
                Output = new Dictionary<string, object?> { ["result"] = text },
            },
            EvalResult.Refused(var reason) => new JobResult { Success = false, ErrorMessage = reason },
            _ => new JobResult { Success = false, ErrorMessage = "the calculator returned nothing." },
        });
    }

    /// <summary>The converting accessor named in JobResult's own comment, not a blind cast: a value
    /// that survived a JSON round-trip arrives as a JsonElement, not a string.</summary>
    private static string Argument(JobParameters call, string name) => call.Get<string>(name);
}

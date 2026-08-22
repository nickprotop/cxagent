namespace CxAgent.Core.Jobs;

/// <summary>The parameter schema an executor advertises (used later to build the LLM's
/// create_plan type enum and per-executor param docs).</summary>
public record JobSchema(string TypeName, string DisplayName, IReadOnlyList<JobParamSpec> Params);

public record JobParamSpec(string Name, string Type, bool Required, string? Description = null, object? Default = null);

/// <summary>Result of an executor's parameter validation.</summary>
public record JobValidation(bool IsValid, IReadOnlyList<string> Errors)
{
    public static JobValidation Valid() => new(true, Array.Empty<string>());
    public static JobValidation Invalid(params string[] errors) => new(false, errors);
}

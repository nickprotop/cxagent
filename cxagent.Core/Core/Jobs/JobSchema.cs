namespace CxAgent.Core.Jobs;

/// <summary>The parameter schema an executor advertises (used later to build the LLM's
/// create_plan type enum and per-executor param docs).</summary>
public record JobSchema(string TypeName, string DisplayName, IReadOnlyList<JobParamSpec> Params);

public record JobParamSpec(string Name, string Type, bool Required, string? Description = null, object? Default = null)
{
    /// <summary>
    /// For a <c>Type</c> of <c>"array"</c>: each item is an object with these fields, and those marked
    /// <c>Required</c> are required in it.
    ///
    /// <para>INIT-ONLY, NOT POSITIONAL, so every existing <c>new JobParamSpec(...)</c> and every
    /// deconstruction of one is untouched. A list of edits is the first parameter that needed a shape
    /// beyond a scalar; a model told only "array" invents the item shape, and each invention is a
    /// refused call.</para>
    /// </summary>
    public IReadOnlyList<JobParamSpec>? ItemFields { get; init; }

    /// <summary>
    /// The only values this parameter accepts, emitted as the schema's <c>enum</c>. A closed set the
    /// model can see is cheaper than one it discovers by being refused.
    /// </summary>
    public IReadOnlyList<string>? AllowedValues { get; init; }
}

/// <summary>Result of an executor's parameter validation.</summary>
public record JobValidation(bool IsValid, IReadOnlyList<string> Errors)
{
    public static JobValidation Valid() => new(true, Array.Empty<string>());
    public static JobValidation Invalid(params string[] errors) => new(false, errors);
}

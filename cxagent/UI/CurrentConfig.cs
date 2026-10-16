using CxAgent.Core.Llm;

namespace CxAgent.UI;

/// <summary>
/// The config the process runs on NOW. New tabs, a re-wire's fallback and the dialog read it; a saved
/// edit replaces it. A holder rather than a captured value, because a closure keeps the value it
/// captured for as long as it lives — and these closures live as long as the process.
/// </summary>
public sealed class CurrentConfig(ResolvedConfig initial)
{
    private ResolvedConfig _value = initial;

    public ResolvedConfig Value => Volatile.Read(ref _value);

    public event Action<ResolvedConfig>? Replaced;

    public void Replace(ResolvedConfig next)
    {
        Volatile.Write(ref _value, next);
        Replaced?.Invoke(next);
    }
}

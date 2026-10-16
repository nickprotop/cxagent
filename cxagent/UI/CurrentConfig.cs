using CxAgent.Core.Llm;

namespace CxAgent.UI;

/// <summary>
/// The config the process runs on NOW. New tabs, a re-wire's fallback and the dialog read it; a saved
/// edit replaces it. One holder rather than a local captured at startup, because every closure that
/// captured the startup value kept it for the life of the process.
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

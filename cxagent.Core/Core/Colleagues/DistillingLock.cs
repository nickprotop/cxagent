namespace CxAgent.Core.Colleagues;

/// <summary>
/// The one distiller across every process: whoever holds <c>distilling.lock</c> distils every colleague's waiting
/// rounds; whoever finds it held does nothing, because the holder drains all of them before letting go.
///
/// <para>PARALLEL LIVING, SERIAL LEARNING. Two sessions finishing rounds together must not each rewrite the same
/// experience from the same starting text — the second would erase what the first learned.</para>
///
/// <para>A DEAD HOLDER NEVER BLOCKS: the hold is an exclusive open of the file, which the operating system releases
/// when its process ends, however it ends. The pid written inside is for a person looking, not for the check.</para>
/// </summary>
public sealed class DistillingLock : IDisposable
{
    private readonly FileStream _held;

    private DistillingLock(FileStream held) => _held = held;

    /// <summary>The lock, or null when another distiller holds it now.</summary>
    public static DistillingLock? TryTake(string root)
    {
        Directory.CreateDirectory(root);
        try
        {
            var held = new FileStream(Path.Combine(root, "distilling.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            held.SetLength(0);
            var pid = System.Text.Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            held.Write(pid);
            held.Flush();
            return new DistillingLock(held);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The lock, waited for a moment when another holds it — long enough for a drain that finds nothing to do to let go,
    /// never as long as one at work: a rename or a forget would then hang on a model's answer. Null when still held.
    /// </summary>
    public static DistillingLock? TakeWithin(string root, TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            if (TryTake(root) is { } held) return held;
            if (DateTime.UtcNow >= until) return null;
            Thread.Sleep(50);
        }
    }

    /// <summary>How long a rename or a forget waits for the lock before saying try again.</summary>
    public static readonly TimeSpan BriefWait = TimeSpan.FromSeconds(3);

    public void Dispose() => _held.Dispose();
}

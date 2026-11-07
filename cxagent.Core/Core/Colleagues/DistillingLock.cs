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

    public void Dispose() => _held.Dispose();
}

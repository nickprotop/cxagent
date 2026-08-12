using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Serialises every test class that calls <c>Directory.SetCurrentDirectory</c>, against each other
/// only.
///
/// <para>WHY: the working directory is PROCESS-GLOBAL, and xunit runs test classes in parallel. A
/// test that switches to a temp directory and restores it in a <c>finally</c> is still unsafe — for
/// as long as it holds the switch, EVERY other test in the process resolves relative paths against
/// somewhere it did not choose. Two such tests interleaving is worse: the second captures the first's
/// temp directory as "previous" and restores that, so the process is left pointing at a directory
/// that is about to be deleted.</para>
///
/// <para>SEEN, not theorised: a file in the repository root went missing during a full-suite run
/// while two classes (agent instruction re-reading, and skill discovery) both held cwd switches. It
/// never entered a commit, but a relative-path cleanup that resolves against the wrong root will
/// delete whatever it finds there.</para>
///
/// <para>Same shape as <see cref="HttpListenerCollection"/> and for the same class of reason: a
/// process-global resource that a parallel runner has no idea is shared. The classes involved run
/// one at a time; everything else stays parallel.</para>
///
/// <para>The better fix is to not touch the process at all — take the directory as a parameter. That
/// is available for discovery, which already does; it is not available for the agent, which reads
/// <c>Directory.GetCurrentDirectory()</c> deep inside its prompt build because that is genuinely
/// what it means by "where am I working".</para>
/// </summary>
[CollectionDefinition("working-directory")]
public sealed class WorkingDirectoryCollection
{
}

using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The tests that derive the palette run one at a time, and never beside another class.
///
/// <para>THE PALETTE IS PROCESS-WIDE: <c>ColorScheme.DeriveFrom</c> sets the colours every control
/// reads, <c>DialogLook.Palette</c> among them, because the app has one theme at a time. Two classes
/// deriving different themes in parallel would each assert against the other's colours — an
/// order-dependent failure that says nothing about the code under test.</para>
/// </summary>
[CollectionDefinition("palette", DisableParallelization = true)]
public sealed class PaletteCollection { }

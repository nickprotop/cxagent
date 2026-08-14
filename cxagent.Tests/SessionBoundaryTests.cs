using System.Reflection;
using CxAgent.Core.Agent;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// THE BOUNDARY, ASSERTED RATHER THAN INTENDED. A session reports facts; presentation lives one layer
/// up. Both properties below held by convention before this test existed, which is exactly how a
/// boundary erodes — one reasonable-looking call at a time.
/// </summary>
public class SessionBoundaryTests
{
    /// <summary>
    /// THE SESSION'S PORT IS NOT A MESSAGE BUS. ShowSystemMessage had 26 call sites and Core called it
    /// none of them: the UI was printing to its own transcript through the session's observer for want
    /// of a writer of its own.
    /// </summary>
    [Fact]
    public void TheObserver_HasNoGeneralPurposeMessageMethod()
    {
        var names = typeof(IChatSink).GetMethods().Select(m => m.Name).ToList();

        Assert.DoesNotContain("ShowSystemMessage", names);
    }

    /// <summary>
    /// CORE DOES NOT REFERENCE THE UI. The one direction that must never reverse — the UI implements
    /// Core's interfaces, never the other way round.
    /// </summary>
    [Fact]
    public void Core_DoesNotDependOnTheUiNamespace()
    {
        var coreTypes = typeof(IChatSink).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("CxAgent.Core", StringComparison.Ordinal) == true);

        foreach (var type in coreTypes)
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                                                   | BindingFlags.Instance | BindingFlags.Static
                                                   | BindingFlags.DeclaredOnly))
            {
                var signature = member switch
                {
                    MethodInfo m => m.ReturnType.FullName + string.Concat(m.GetParameters().Select(p => p.ParameterType.FullName)),
                    PropertyInfo p => p.PropertyType.FullName,
                    FieldInfo f => f.FieldType.FullName,
                    _ => null,
                };

                Assert.DoesNotContain("CxAgent.UI", signature ?? "", StringComparison.Ordinal);
            }
        }
    }
}

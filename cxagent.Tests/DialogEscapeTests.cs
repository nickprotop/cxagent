using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Which open dialog Escape closes: the topmost. A question opened over a dialog — install where,
/// uninstall what, unsaved changes — is asked before the dialog under it, or Escape closes the dialog
/// out from under the question.
/// </summary>
public class DialogEscapeTests
{
    [Fact]
    public void AQuestion_IsClosedBeforeTheDialogsItOpensOver()
    {
        var order = DialogEscape.Order.ToList();
        var question = order.IndexOf("confirm");
        foreach (var under in new[] { "plugins", "config", "export" })
            Assert.True(question < order.IndexOf(under), $"confirm must come before {under}");
    }

    [Fact]
    public void ThePickersOverConfiguration_AreClosedBeforeIt()
    {
        var order = DialogEscape.Order.ToList();
        Assert.True(order.IndexOf("apply-to-tabs") < order.IndexOf("config"));
        Assert.True(order.IndexOf("model-picker") < order.IndexOf("config"));
    }
}

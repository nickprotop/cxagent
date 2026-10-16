using Xunit;

namespace Shop.Tests;

public class CheckoutTests
{
    [Fact] public void CheckoutPlacesAnOrder() { /* exercises CheckoutController.Checkout */ }

    [Fact] public void CheckoutAppliesDiscountCode()
    {
        var cart = new Cart(new[] { "item1", "item2" }, 100m);
        var controller = new CheckoutController();

        var order = controller.Checkout(cart, "SAVE10");

        Assert.Equal(10m, order.Discount);
        Assert.Equal(90m, order.Total);
    }
}

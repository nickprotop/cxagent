namespace Shop;

public class CheckoutController
{
    private readonly OrderService _orders = new();

    public IActionResult Checkout(Cart cart, string? code = null) =>
        new IActionResult(_orders.PlaceOrder(cart, code));
}

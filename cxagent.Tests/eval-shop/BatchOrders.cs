namespace Shop;

public class BatchOrders
{
    private readonly OrderService _orders = new();

    // line 5
    // line 6
    // line 7
    // line 8
    // line 9
    // line 10
    // line 11
    // line 12
    // line 13
    // line 14
    // line 15
    // line 16
    public void Run(IEnumerable<Cart> carts) { foreach (var c in carts) _orders.PlaceOrder(c); }
}

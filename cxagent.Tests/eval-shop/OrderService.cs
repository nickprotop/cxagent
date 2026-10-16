namespace Shop;

public class OrderService
{
    private readonly DiscountService _discounts = new();

    public Order PlaceOrder(Cart cart, string? code = null)
    {
        var discount = _discounts.CalculateDiscount(code, cart.Total);
        var finalTotal = cart.Total - discount;
        var order = new Order(cart.Items, finalTotal, code, discount);
        Repository.Save(order);
        return order;
    }
}

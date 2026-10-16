namespace Shop;

public class IActionResult
{
    public Order? Order { get; }
    public IActionResult(Order? order) => Order = order;
    public decimal Discount => Order?.Discount ?? 0;
    public decimal Total => Order?.Total ?? 0;
}

public static class OkResultExtensions
{
    public static IActionResult Ok(this Order order) => new IActionResult(order);
}

public static class Ok
{
    public static IActionResult Of(Order order) => new IActionResult(order);
}

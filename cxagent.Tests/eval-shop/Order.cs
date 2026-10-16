namespace Shop;

public record Order(
    IEnumerable<string> Items,
    decimal Total,
    string? DiscountCode = null,
    decimal Discount = 0
);

namespace Shop;

public record Cart(
    IEnumerable<string> Items,
    decimal Total
);

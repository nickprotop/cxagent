namespace Shop;

public class DiscountService
{
    private static readonly Dictionary<string, decimal> _codes = new()
    {
        { "SAVE10", 0.10m },
        { "SAVE20", 0.20m },
    };

    public decimal CalculateDiscount(string? code, decimal subtotal)
    {
        if (string.IsNullOrEmpty(code) || !_codes.TryGetValue(code, out var rate))
            return 0m;

        return subtotal * rate;
    }
}

# Discount Code Plan

## Overview
Add support for discount codes at checkout. A discount code like "SAVE10" (10% off) or "SAVE20" (20% off) can be passed to the checkout endpoint. If the code is known, the cart total is recalculated with the discount applied. If unknown or empty, the original total is used. The applied code is stored on the resulting Order.

## Step-by-Step Plan

### Step 1: Create a DiscountService
**File: `DiscountService.cs`**
- Create a new class `DiscountService` in namespace `Shop`.
- Add a private readonly dictionary mapping code strings to decimal discount rates: `{"SAVE10": 0.10m, "SAVE20": 0.20m}`.
- Add method `decimal CalculateDiscount(string? code, decimal subtotal)` that:
  - Returns `0m` if code is null/empty or not in the dictionary.
  - Returns `subtotal * rate` for valid codes.
- Add method `string? GetCode(string? code)` that returns the code if valid, null otherwise (for validation).

### Step 2: Add a `Discount` property to Order
**File: `Order.cs`**
- Add a public property: `public decimal Discount { get; set; }`
- Update the constructor to accept a `discount` parameter and assign it.
- The constructor should become: `public Order(IEnumerable<string> items, decimal total, decimal discount = 0)` to avoid breaking existing callers (e.g., batch job).

### Step 3: Update OrderService.PlaceOrder
**File: `OrderService.cs`**
- Change `PlaceOrder` signature to accept `Cart cart, string? code = null`.
- Inject `DiscountService _discounts` via constructor.
- In `PlaceOrder`:
  - Calculate `var discount = _discounts.CalculateDiscount(code, cart.Total);`
  - Calculate final total: `var finalTotal = cart.Total - discount;`
  - Pass both to `new Order(cart.Items, finalTotal, discount)`.

### Step 4: Update CheckoutController
**File: `CheckoutController.cs`**
- Change the `Checkout` method to accept `string? code = null` as a second parameter.
- Pass the code through to `_orders.PlaceOrder(cart, code)`.

### Step 5: Leave BatchOrders unchanged (no discount codes)
**File: `BatchOrders.cs`**
- No changes needed — it calls `PlaceOrder(cart)` which uses the default `code = null`.

### Step 6: Add a test for discount codes
**File: `Tests/CheckoutTests.cs`**
- Add a new test `CheckoutAppliesDiscountCode()` that:
  - Creates a cart with items totaling 100.
  - Calls `Checkout(cart, "SAVE10")`.
  - Asserts the returned order has `Discount == 10` and the order total is `90`.

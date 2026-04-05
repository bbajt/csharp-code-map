namespace SampleFSharpApp.Patterns

open SampleFSharpApp.Models
open SampleFSharpApp.Services

/// <summary>Pattern matching and pipeline examples.</summary>
module OrderPipeline =
    /// <summary>Get display text for order status.</summary>
    let statusText (status: OrderStatus) =
        match status with
        | Pending -> "Awaiting processing"
        | Processing -> "Being prepared"
        | Shipped tracking -> $"Shipped (tracking: {tracking})"
        | Delivered date ->
            let fmt = date.ToString("yyyy-MM-dd")
            $"Delivered on {fmt}"
        | Cancelled reason -> $"Cancelled: {reason}"

    /// <summary>Check if an order can be cancelled.</summary>
    let canCancel (order: Order) =
        match order.Status with
        | Pending | Processing -> true
        | Shipped _ | Delivered _ | Cancelled _ -> false

    /// <summary>Apply discount to order total using pipe.</summary>
    let applyDiscount (rate: decimal) (order: Order) =
        let discounted = order.Total.Amount * (1m - rate)
        { order with Total = { order.Total with Amount = discounted } }

    /// <summary>Full order processing pipeline using pipes.</summary>
    let processAndShip (customer: Customer) (items: OrderItem list) (tracking: string) =
        items
        |> OrderService.createOrder customer
        |> applyDiscount 0.1m
        |> OrderService.shipOrder tracking

    /// <summary>Summarize an order in one line.</summary>
    let summarize (order: Order) =
        let count = OrderService.totalItemCount order
        let status = statusText order.Status
        let name = StringHelpers.capitalize order.Customer.Name
        $"Order #{order.Id} for {name}: {count} items, {order.Total.Amount} {order.Total.Currency} — {status}"

/// <summary>Active pattern examples.</summary>
module ActivePatterns =
    /// <summary>Classify a number as positive, negative, or zero.</summary>
    let (|Positive|Negative|Zero|) (n: int) =
        if n > 0 then Positive
        elif n < 0 then Negative
        else Zero

    /// <summary>Partial active pattern for even numbers.</summary>
    let (|Even|_|) (n: int) =
        if n % 2 = 0 then Some(n / 2) else None

    /// <summary>Describe a number using active patterns.</summary>
    let describe (n: int) =
        match n with
        | Positive & Even half -> $"{n} is positive and even (half = {half})"
        | Positive -> $"{n} is positive and odd"
        | Negative -> $"{n} is negative"
        | Zero -> "zero"

/// <summary>Generic utility functions.</summary>
module GenericHelpers =
    /// <summary>Apply a function twice.</summary>
    let applyTwice (f: 'a -> 'a) (x: 'a) = f (f x)

    /// <summary>Swap tuple elements.</summary>
    let swap (a, b) = (b, a)

    /// <summary>Memoize a function (simple dictionary-based).</summary>
    let memoize (f: 'a -> 'b) =
        let cache = System.Collections.Generic.Dictionary<'a, 'b>()
        fun x ->
            match cache.TryGetValue(x) with
            | true, v -> v
            | false, _ ->
                let v = f x
                cache.[x] <- v
                v

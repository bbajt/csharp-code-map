namespace SampleFSharpApp.Services

open SampleFSharpApp.Models
open SampleFSharpApp.Interfaces

/// <summary>Pure math utilities.</summary>
module Calculator =
    /// <summary>Add two integers.</summary>
    let add (a: int) (b: int) = a + b

    /// <summary>Multiply two integers.</summary>
    let multiply (a: int) (b: int) = a * b

    /// <summary>Compute factorial (recursive).</summary>
    let factorial (n: int) =
        let rec loop acc i =
            if i <= 1 then acc
            else loop (acc * i) (i - 1)
        loop 1 n

    /// <summary>Sum a list of integers.</summary>
    let sumList (items: int list) =
        items |> List.fold add 0

/// <summary>String manipulation utilities.</summary>
module StringHelpers =
    /// <summary>Capitalize the first letter.</summary>
    let capitalize (s: string) =
        if System.String.IsNullOrEmpty(s) then s
        else s.[0..0].ToUpperInvariant() + s.[1..]

    /// <summary>Reverse a string.</summary>
    let reverse (s: string) =
        s |> Seq.rev |> Seq.toArray |> System.String

    /// <summary>Count words in a string.</summary>
    let wordCount (s: string) =
        s.Split([| ' '; '\t'; '\n' |], System.StringSplitOptions.RemoveEmptyEntries).Length

/// <summary>Order processing logic.</summary>
module OrderService =
    /// <summary>Create a new order for a customer.</summary>
    let createOrder (customer: Customer) (items: OrderItem list) =
        let total = {
            Amount = items |> List.sumBy (fun i -> i.UnitPrice.Amount * decimal i.Quantity)
            Currency = "USD"
        }
        {
            Id = System.Random.Shared.Next(1000, 9999)
            Customer = customer
            Items = items
            Status = Pending
            Total = total
        }

    /// <summary>Ship an order with a tracking number.</summary>
    let shipOrder (tracking: string) (order: Order) =
        { order with Status = Shipped tracking }

    /// <summary>Cancel an order with a reason.</summary>
    let cancelOrder (reason: string) (order: Order) =
        { order with Status = Cancelled reason }

    /// <summary>Calculate total item count.</summary>
    let totalItemCount (order: Order) =
        order.Items |> List.sumBy (fun i -> i.Quantity)

    /// <summary>Process a full order: create, validate, and log.</summary>
    let processOrder (customer: Customer) (items: OrderItem list) =
        let order = createOrder customer items
        let itemCount = totalItemCount order
        let calcResult = Calculator.add itemCount 0  // cross-module call
        printfn $"Order {order.Id} created: {calcResult} items, total {order.Total.Amount}"
        order

/// <summary>Nested module demonstrating hierarchy.</summary>
module Analytics =
    /// <summary>Revenue calculation helpers.</summary>
    module Revenue =
        /// <summary>Sum order totals.</summary>
        let totalRevenue (orders: Order list) =
            orders |> List.sumBy (fun o -> o.Total.Amount)

        /// <summary>Average order value.</summary>
        let averageOrderValue (orders: Order list) =
            if orders.IsEmpty then 0m
            else totalRevenue orders / decimal orders.Length

    /// <summary>Customer analytics.</summary>
    module Customers =
        /// <summary>Find top customers by order count.</summary>
        let topCustomers (n: int) (orders: Order list) =
            orders
            |> List.groupBy (fun o -> o.Customer.Id)
            |> List.map (fun (id, ords) -> (id, ords.Length))
            |> List.sortByDescending snd
            |> List.truncate n

/// <summary>Module requiring qualified access — tests [RequireQualifiedAccess] indexing.</summary>
[<RequireQualifiedAccess>]
module QualifiedAccess =
    /// <summary>Format a greeting message.</summary>
    let greet (name: string) = $"Hello, {name}!"

    /// <summary>Format an integer as a zero-padded string.</summary>
    let formatNumber (n: int) (width: int) = string n |> fun s -> s.PadLeft(width, '0')

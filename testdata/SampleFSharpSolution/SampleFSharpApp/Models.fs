namespace SampleFSharpApp.Models

open System

/// <summary>Represents the lifecycle state of an order.</summary>
type OrderStatus =
    | Pending
    | Processing
    | Shipped of trackingNumber: string
    | Delivered of deliveredAt: DateTime
    | Cancelled of reason: string

/// <summary>Represents a monetary amount with currency.</summary>
type Money = {
    Amount: decimal
    Currency: string
}

/// <summary>A customer record.</summary>
type Customer = {
    Id: int
    Name: string
    Email: string
}

/// <summary>An order with line items.</summary>
type Order = {
    Id: int
    Customer: Customer
    Items: OrderItem list
    Status: OrderStatus
    Total: Money
}

/// <summary>A single line item in an order.</summary>
and OrderItem = {
    ProductName: string
    Quantity: int
    UnitPrice: Money
}

/// <summary>Priority levels for task scheduling.</summary>
type Priority =
    | Low = 0
    | Medium = 1
    | High = 2
    | Critical = 3

/// <summary>Result type for operations that can fail.</summary>
type OperationResult<'T> =
    | Success of value: 'T
    | Failure of error: string * code: int

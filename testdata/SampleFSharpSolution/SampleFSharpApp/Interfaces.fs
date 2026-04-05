namespace SampleFSharpApp.Interfaces

open SampleFSharpApp.Models

/// <summary>Service for greeting users.</summary>
type IGreeter =
    /// <summary>Produce a greeting for the given name.</summary>
    abstract member Greet: name: string -> string

/// <summary>Repository for order persistence.</summary>
type IOrderRepository =
    abstract member GetById: id: int -> Order option
    abstract member Save: order: Order -> unit
    abstract member Delete: id: int -> bool

/// <summary>Service for processing payments.</summary>
type IPaymentService =
    abstract member Charge: customer: Customer -> amount: Money -> OperationResult<string>

/// <summary>Simple greeter implementation.</summary>
type SimpleGreeter() =
    interface IGreeter with
        member _.Greet(name) = $"Hello, {name}!"

    /// <summary>Greet loudly (uppercase).</summary>
    member this.GreetLoud(name: string) =
        let greeting = (this :> IGreeter).Greet(name)
        greeting.ToUpperInvariant()

/// <summary>Formal greeter with configurable title.</summary>
type FormalGreeter(title: string) =
    let mutable greetCount = 0

    interface IGreeter with
        member _.Greet(name) =
            greetCount <- greetCount + 1
            $"{title} {name}, welcome. (Greeting #{greetCount})"

    member _.GreetCount = greetCount

/// <summary>In-memory order repository for testing.</summary>
type InMemoryOrderRepository() =
    let mutable orders = Map.empty<int, Order>

    interface IOrderRepository with
        member _.GetById(id) = orders |> Map.tryFind id

        member _.Save(order) =
            orders <- orders |> Map.add order.Id order

        member _.Delete(id) =
            if orders |> Map.containsKey id then
                orders <- orders |> Map.remove id
                true
            else
                false

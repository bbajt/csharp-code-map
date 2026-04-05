namespace SampleFSharp

open System

type IGreeter =
    abstract member Greet: name: string -> string

type Greeter() =
    interface IGreeter with
        member _.Greet(name) = $"Hello, {name}!"

    member this.GreetLoud(name: string) =
        let greeting = (this :> IGreeter).Greet(name)
        greeting.ToUpperInvariant()

module Calculator =
    let add (a: int) (b: int) = a + b
    let multiply (a: int) (b: int) = a * b

    let factorial (n: int) =
        let rec loop acc i =
            if i <= 1 then acc
            else loop (acc * i) (i - 1)
        loop 1 n

type OrderStatus =
    | Pending
    | Shipped of trackingNumber: string
    | Delivered of DateTime
    | Cancelled of reason: string

type Order = {
    Id: int
    Customer: string
    Status: OrderStatus
    Total: decimal
}

module OrderService =
    let createOrder (customer: string) (total: decimal) =
        { Id = 1; Customer = customer; Status = Pending; Total = total }

    let shipOrder (order: Order) (tracking: string) =
        { order with Status = Shipped tracking }

    let processOrder (customer: string) (amount: decimal) =
        let order = createOrder customer amount
        let result = Calculator.add 1 2  // cross-module call
        printfn $"Order created with calc result: {result}"
        order

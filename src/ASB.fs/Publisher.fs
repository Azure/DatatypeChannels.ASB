[<RequireQualifiedAccessAttribute>]
module ASB.Publisher
open Azure.Messaging.ServiceBus
open FSharp.Control.Tasks.Builders

let internal mkNew retries (ToSend toSend) withSender =
    let send msg =
        fun (sender:ServiceBusSender) -> sender.SendMessageAsync msg |> Task.ignore
        |> withSender
    toSend >> Task.withRetries retries send |> Publisher

/// Disassemble into primitives and send
let publish (msg: 'msg) (Publisher publisher) = publisher msg

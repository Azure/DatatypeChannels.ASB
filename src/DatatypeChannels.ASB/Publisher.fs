[<RequireQualifiedAccessAttribute>]
module DatatypeChannels.ASB.Publisher
open Azure.Messaging.ServiceBus

let internal mkNew (ToSend toSend) withSender =
    let send msg =
        fun (sender:ServiceBusSender) -> sender.SendMessageAsync msg |> Task.ignore
        |> withSender
    toSend >> send |> Publisher

/// Disassemble into primitives and send
let publish (msg: 'msg) (Publisher publisher) = publisher msg

namespace ASB

open System
open System.Threading.Tasks
open Azure.Messaging.ServiceBus
open Azure.Messaging.ServiceBus.Administration
open FSharp.Control.Tasks.Builders

[<RequireQualifiedAccessAttribute>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Response =
    let value (r: Azure.Response<_>) = r.Value

[<RequireQualifiedAccessAttribute>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CorrelationRuleFilter =
    let withProp name value (filter:CorrelationRuleFilter) =
        filter.ApplicationProperties.Add(name, value)
        filter

/// Subscription management
[<RequireQualifiedAccessAttribute>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module internal Subscription =
    module RuleProperties =
        let updateFrom (options:CreateRuleOptions) (properties:RuleProperties) =
            properties.Action <- options.Action
            properties.Filter <- options.Filter
            properties

    module SubscriptionProperties =
        let updateFrom (options:CreateSubscriptionOptions) (properties:SubscriptionProperties) =
            properties.LockDuration <- options.LockDuration
            properties.RequiresSession <- options.RequiresSession
            properties.DefaultMessageTimeToLive <- options.DefaultMessageTimeToLive
            properties.AutoDeleteOnIdle <- options.AutoDeleteOnIdle
            properties.DeadLetteringOnMessageExpiration <- options.DeadLetteringOnMessageExpiration
            properties.EnableDeadLetteringOnFilterEvaluationExceptions <- options.EnableDeadLetteringOnFilterEvaluationExceptions
            properties.MaxDeliveryCount <- options.MaxDeliveryCount
            properties.EnableBatchedOperations <- options.EnableBatchedOperations
            properties.Status <- options.Status
            properties.ForwardTo <- options.ForwardTo
            properties.ForwardDeadLetteredMessagesTo <- options.ForwardDeadLetteredMessagesTo
            if not (isNull options.UserMetadata) then properties.UserMetadata <- options.UserMetadata
            properties

    let createOrUpdate (Log log) (client:ServiceBusAdministrationClient) binding =
        task {
            try
                let! subscription =
                    client.GetSubscriptionAsync(binding.Subscription.TopicName, binding.Subscription.SubscriptionName) |> Task.map Response.value
                if binding.Subscription.ForwardTo <> subscription.ForwardTo then
                    log("Updating subscription: {Subscription} on {Topic}", [| subscription.SubscriptionName; subscription.TopicName |])
                    do! client.UpdateSubscriptionAsync (subscription |> SubscriptionProperties.updateFrom binding.Subscription)
                        |> Task.map ignore

                match binding.Rule with
                | None -> ()
                | Some rule ->
                    let! existingRule =
                        client.GetRuleAsync(binding.Subscription.TopicName, binding.Subscription.SubscriptionName, rule.Name) |> Task.map Response.value
                    if rule.Name = existingRule.Name && (rule.Filter <> existingRule.Filter || rule.Action <> existingRule.Action) then
                        log("Updating subscription rule: {Rule} ({ExistingFilter} -> {Filter})", [| rule.Name; existingRule.Filter.ToString(); rule.Filter.ToString() |])
                        do! client.UpdateRuleAsync(binding.Subscription.TopicName, binding.Subscription.SubscriptionName, existingRule |> RuleProperties.updateFrom rule)
                            |> Task.map ignore
                return subscription
            with :? ServiceBusException as ex when ex.Reason = ServiceBusFailureReason.MessagingEntityNotFound ->
                try
                    log("Creating subscription: {Subscription} on {Topic}",
                        [| binding.Subscription.SubscriptionName; binding.Subscription.TopicName |])
                    return!
                        match binding.Rule with
                        | None -> client.CreateSubscriptionAsync binding.Subscription
                        | Some rule -> client.CreateSubscriptionAsync(binding.Subscription, rule)
                        |> Task.map Response.value
                with :? ServiceBusException as ex when ex.Reason = ServiceBusFailureReason.MessagingEntityAlreadyExists ->
                    return! client.GetSubscriptionAsync(binding.Subscription.TopicName, binding.Subscription.SubscriptionName) |> Task.map Response.value
        }
        |> Task.map (fun s -> if String.IsNullOrEmpty s.ForwardTo then log("Subscribed: {Subscription} ({Topic})", [| s.SubscriptionName; s.TopicName |])
                              else log("Subscribed: {Subscription} ({Topic} -> {ForwardTo})", [| s.SubscriptionName; s.TopicName; s.ForwardTo |]))

    let withBinding log (withClient: (ServiceBusAdministrationClient -> _) -> _) (binding: Binding) =
        let bound = ref None
        fun cont ->
            fun client -> task {
                match !bound with
                | Some name -> return name
                | _ ->
                    do! createOrUpdate log client binding
                    bound := Some binding.Subscription.SubscriptionName
                    return sprintf "%s/Subscriptions/%s" binding.Subscription.TopicName binding.Subscription.SubscriptionName
            }
            |> withClient
            |> cont

/// Queue management
[<RequireQualifiedAccessAttribute>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module internal Queue =
    module QueueProperties =
        let updateFrom (options:CreateQueueOptions) (properties:QueueProperties) =
            properties.LockDuration <- options.LockDuration
            properties.DefaultMessageTimeToLive <- options.DefaultMessageTimeToLive
            properties.AutoDeleteOnIdle <- options.AutoDeleteOnIdle
            properties.DeadLetteringOnMessageExpiration <- options.DeadLetteringOnMessageExpiration
            properties.MaxDeliveryCount <- options.MaxDeliveryCount
            properties.EnableBatchedOperations <- options.EnableBatchedOperations
            properties.Status <- options.Status
            properties.ForwardTo <- options.ForwardTo
            properties.ForwardDeadLetteredMessagesTo <- options.ForwardDeadLetteredMessagesTo
            if not (isNull options.UserMetadata) then properties.UserMetadata <- options.UserMetadata
            properties

    let internal createOrUpdate (Log log) (client:ServiceBusAdministrationClient) (options: CreateQueueOptions) =
        task {
            try
                let! queue = client.GetQueueAsync options.Name |> Task.map Response.value
                if queue.RequiresDuplicateDetection <> options.RequiresDuplicateDetection ||
                   queue.RequiresSession <> options.RequiresSession then
                   log("Recreating queue: {Queue}", [| queue.Name |])
                   do! client.DeleteQueueAsync options.Name |> Task.map ignore
                   return! client.CreateQueueAsync options |> Task.map Response.value
                else if queue.LockDuration <> options.LockDuration ||
                        queue.DefaultMessageTimeToLive <> options.DefaultMessageTimeToLive ||
                        queue.AutoDeleteOnIdle <> options.AutoDeleteOnIdle ||
                        queue.DeadLetteringOnMessageExpiration <> options.DeadLetteringOnMessageExpiration ||
                        queue.MaxDeliveryCount <> options.MaxDeliveryCount ||
                        queue.EnableBatchedOperations <> options.EnableBatchedOperations ||
                        queue.Status <> options.Status ||
                        queue.ForwardTo <> options.ForwardTo ||
                        queue.ForwardDeadLetteredMessagesTo <> options.ForwardDeadLetteredMessagesTo then
                    log("Updating queue: {Queue}", [| queue.Name |])
                    return! queue |> QueueProperties.updateFrom options |> client.UpdateQueueAsync |> Task.map Response.value
                else
                    return queue
            with :? ServiceBusException as ex when ex.Reason = ServiceBusFailureReason.MessagingEntityNotFound ->
                try
                    log("Creating queue: {Queue}", [| options.Name |])
                    return! client.CreateQueueAsync options |> Task.map Response.value
                with :? ServiceBusException as ex when ex.Reason = ServiceBusFailureReason.MessagingEntityAlreadyExists ->
                    return! client.GetQueueAsync options.Name |> Task.map Response.value
        }
        |> Task.map (fun q -> log("Queue {Queue} is setup", [| q.Name |]))

    let withBindings log (withClient: (ServiceBusAdministrationClient -> _) -> _) (queueOptions: CreateQueueOptions) (bindings: #seq<Binding>) =
        let bound = ref None
        fun cont ->
            fun client -> task {
                match !bound with
                | Some name -> return name
                | _ ->
                    do! createOrUpdate log client queueOptions
                    for binding in bindings do
                        binding.Subscription.ForwardTo <- queueOptions.Name
                        do! Subscription.createOrUpdate log client binding
                    bound := Some queueOptions.Name
                    return queueOptions.Name
            }
            |> withClient
            |> cont

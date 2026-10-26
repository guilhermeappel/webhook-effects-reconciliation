namespace WebhookInbox;

internal interface ISubscriptionEventHandler
{
    Task<InboxStatus> HandleAsync(InboxEvent evt, CancellationToken ct);
}

internal sealed class EventDispatcher(
    PaymentEventHandler payments, ISubscriptionEventHandler subscriptions, ILogger<EventDispatcher> logger)
{
    public Task<InboxStatus> HandleAsync(InboxEvent evt, CancellationToken ct) => evt.EventType switch
    {
        var t when t.StartsWith("PAYMENT_", StringComparison.Ordinal) => payments.HandleAsync(evt, ct),
        var t when t.StartsWith("SUBSCRIPTION_", StringComparison.Ordinal) => subscriptions.HandleAsync(evt, ct),
        _ => Unhandled(evt)
    };

    // A type nobody handles is not an event the platform looked at and found nothing to change. The status stays NoOp
    // so that the enum does not grow; the warning is what tells a new provider event type apart.
    private Task<InboxStatus> Unhandled(InboxEvent evt)
    {
        logger.LogWarning("Inbox event {EventId} has type {EventType}, which no handler takes; recorded as NoOp",
            LogText.Safe(evt.ProviderEventId), LogText.Safe(evt.EventType));
        return Task.FromResult(InboxStatus.NoOp);
    }
}

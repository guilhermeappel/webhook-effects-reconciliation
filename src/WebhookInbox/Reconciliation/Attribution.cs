namespace WebhookInbox;

internal enum Layer { ProviderDelivery, ProcessingFailed, ProcessedWithoutEffect }

internal sealed record ProviderCharge(string Id, ChargeStatus Status, bool Deleted);

// attribute a stable divergence to the layer where the effect was lost; null is "unattributed".
// "history" holds the inbox rows for this provider id. The rule is the one in sql/attribution.sql:
// layer 3 needs an event of the expected type that was processed, layer 2 needs every one of them
// to have failed for good. Any other history with an event of the expected type is not attributed: one with only
// waiting events, and also one that mixes dead letters with waiting events (here waiting is Pending, or Failed, which
// the processor still retries).
internal static class Attribution
{
    public static Layer? Attribute(ProviderCharge remote, IReadOnlyList<InboxEvent> history, bool hasLocalRecord = true)
    {
        var expected = ExpectedEventType(remote);
        if (expected is null || !hasLocalRecord)
        {
            return null;                            // status we do not attribute, or nothing to compare with
        }
        var matching = history.Where(e => e.EventType == expected).ToList();

        if (matching.Count == 0)
        {
            return Layer.ProviderDelivery;          // consistent with a delivery gap; not proof
        }
        if (matching.Any(e => IsProcessed(e.Status)))
        {
            return Layer.ProcessedWithoutEffect;    // the inbox said yes, the state says no
        }
        if (matching.All(e => IsFailed(e.Status)))
        {
            return Layer.ProcessingFailed;
        }
        return null;                                // some expected-type event is still waiting or being retried
    }

    // The platform's old inbox had PROCESSED, FAILED, PENDING and IGNORED, and its FAILED was never retried.
    // Here "processed" covers the statuses that finished without an error (including a skip), and only a dead
    // letter is "failed and never retried": Failed means the processor will still retry it, so it counts as waiting.
    private static bool IsProcessed(InboxStatus s) =>
        s is InboxStatus.Applied or InboxStatus.NoOp or InboxStatus.SkippedNotFound;

    private static bool IsFailed(InboxStatus s) =>
        s is InboxStatus.DeadLettered;

    private static string? ExpectedEventType(ProviderCharge remote) => remote switch
    {
        { Deleted: true } => "PAYMENT_DELETED",
        { Status: ChargeStatus.Received or ChargeStatus.ReceivedInCash } => "PAYMENT_RECEIVED",
        { Status: ChargeStatus.Confirmed } => "PAYMENT_CONFIRMED",
        { Status: ChargeStatus.Overdue } => "PAYMENT_OVERDUE",
        { Status: ChargeStatus.Refunded } => "PAYMENT_REFUNDED",
        _ => null
    };
}

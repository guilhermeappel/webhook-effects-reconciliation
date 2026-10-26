namespace WebhookInbox;

internal enum PlanEffect { None, Reevaluate }

internal sealed record Transition(ChargeStatus? NewStatus, PlanEffect PlanEffect, string Reason);

// the transition rules, as a pure function that tests can call directly
internal static class ChargeRules
{
    public static bool IsSettled(this ChargeStatus s) =>
        s is ChargeStatus.Confirmed or ChargeStatus.Received or ChargeStatus.ReceivedInCash;

    // Paid through the gateway (card, bank slip, Pix), as opposed to a manual cash entry.
    public static bool IsGatewaySettled(this ChargeStatus s) =>
        s is ChargeStatus.Confirmed or ChargeStatus.Received;

    public static Transition Decide(Charge local, PaymentPayload incoming, string eventType)
    {
        // In the events we observed, a deletion keeps the previous status and adds
        // deleted: true, so the flag is checked before the status means anything.
        // A refund in the payload is never stale: the money went back, so it is recorded.
        if (incoming.Deleted && incoming.Status != ChargeStatus.Refunded)
        {
            if (local.Status == ChargeStatus.Cancelled)
            {
                return new(null, PlanEffect.None, "already cancelled");
            }
            if (local.Status == ChargeStatus.Refunded)
            {
                return new(null, PlanEffect.None, "refunded charge stays refunded");
            }
            var paidByGateway = local.Status.IsGatewaySettled() || incoming.Status?.IsGatewaySettled() == true;
            if (!paidByGateway)
            {
                return new(ChargeStatus.Cancelled, PlanEffect.None, "deleted at provider");
            }
            if (local.Status.IsSettled())
            {
                return new(null, PlanEffect.None, "settled payment: deletion does not revoke access");
            }
            // An open charge that the provider shows as paid: the flag is stale, the status below wins.
        }

        if (incoming.Status is not { } status)
        {
            return new(null, PlanEffect.None, "status not modeled");
        }

        if (local.Status == ChargeStatus.Refunded)
        {
            return new(null, PlanEffect.None, "refunded charge stays refunded");
        }

        if (local.Status == ChargeStatus.Cancelled && status != ChargeStatus.Refunded)
        {
            // Only a restore brings a cancelled charge back. A settled status alone does not: a
            // plan switch cancels like a deletion, and a late payment must not revive a replaced charge.
            if (eventType != "PAYMENT_RESTORED" || local.CancelledByStaff)
            {
                return new(null, PlanEffect.None, "cancelled charge stays cancelled");
            }
        }

        return status == local.Status
            ? new(null, PlanEffect.None, "no change")
            : new(status, PlanEffect.Reevaluate, $"{local.Status} -> {status}");
    }
}

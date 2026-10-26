using Microsoft.EntityFrameworkCore;

namespace WebhookInbox;

internal interface IPlanService
{
    Task ReevaluateAsync(long planId, CancellationToken ct);
}

internal sealed class PaymentEventHandler(PaymentsDbContext db, IPlanService plans, ILogger<PaymentEventHandler> logger)
{
    public async Task<InboxStatus> HandleAsync(InboxEvent evt, CancellationToken ct)
    {
        var payload = PaymentPayload.Parse(evt.Payload);
        var charge = await db.Charges.SingleOrDefaultAsync(c => c.ProviderId == payload.Id, ct);

        // Creating charges for known customers is omitted here. What matters is that
        // "not found" gets its own status instead of being reported as processed.
        if (charge is null)
        {
            return InboxStatus.SkippedNotFound;
        }

        var transition = ChargeRules.Decide(charge, payload, evt.EventType);
        if (transition.NewStatus is not { } newStatus)
        {
            WarnIfAPaymentWasLeftUnapplied(evt, charge, payload, transition);
            return InboxStatus.NoOp;
        }

        charge.Status = newStatus;
        if (transition.PlanEffect == PlanEffect.Reevaluate && charge.PlanId is { } planId)
        {
            await plans.ReevaluateAsync(planId, ct);
        }

        return InboxStatus.Applied;
    }

    // The event says the money arrived and the rules leave a closed charge as it is (a plan switch cancels a charge
    // like a deletion, and a refund is final). That can be right, but it is where "processed" and "applied" part ways,
    // and the status alone (NoOp) cannot tell it from a repeated event.
    private void WarnIfAPaymentWasLeftUnapplied(InboxEvent evt, Charge charge, PaymentPayload payload, Transition transition)
    {
        if (payload.Status?.IsSettled() == true && !charge.Status.IsSettled())
        {
            logger.LogWarning("Inbox event {EventId} reports {PayloadStatus} but the charge stays {ChargeStatus}: {Reason}",
                LogText.Safe(evt.ProviderEventId), payload.Status, charge.Status, transition.Reason);
        }
    }
}

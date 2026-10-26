namespace WebhookInbox.Tests;

public class ChargeRulesTests
{
    private const string Deleted = "PAYMENT_DELETED";
    private const string Restored = "PAYMENT_RESTORED";
    private const string Updated = "PAYMENT_UPDATED";

    private static Charge Local(ChargeStatus status, bool cancelledByStaff = false) =>
        new() { ProviderId = "pay_1", Status = status, CancelledByStaff = cancelledByStaff };

    private static PaymentPayload Payload(ChargeStatus? status, bool deleted = false) => new("pay_1", status, deleted);

    [Theory]
    [InlineData("Pending")]
    [InlineData("Overdue")]
    public void Decide_DeletedOpenCharge_CancelsWithoutAffectingThePlan(string openStatus)
    {
        var status = Enum.Parse<ChargeStatus>(openStatus);

        var transition = ChargeRules.Decide(Local(status), Payload(status, deleted: true), Deleted);

        Assert.Equal(ChargeStatus.Cancelled, transition.NewStatus);
        Assert.Equal(PlanEffect.None, transition.PlanEffect);
    }

    [Theory]
    [InlineData("Confirmed")]
    [InlineData("Received")]
    public void Decide_DeletedCardOrBankPaymentAlreadySettled_IsIgnored(string settledStatus)
    {
        var status = Enum.Parse<ChargeStatus>(settledStatus);

        var transition = ChargeRules.Decide(Local(status), Payload(status, deleted: true), Deleted);

        Assert.Null(transition.NewStatus);
        Assert.Equal(PlanEffect.None, transition.PlanEffect);
    }

    [Theory]
    [InlineData("Pending", "Received")]
    [InlineData("Overdue", "Confirmed")]
    public void Decide_DeletedFlagOnAChargeTheProviderShowsPaid_AppliesThePaidStatus(string localStatus, string paidStatus)
    {
        var paid = Enum.Parse<ChargeStatus>(paidStatus);

        var transition = ChargeRules.Decide(Local(Enum.Parse<ChargeStatus>(localStatus)), Payload(paid, deleted: true), Deleted);

        Assert.Equal(paid, transition.NewStatus);
        Assert.Equal(PlanEffect.Reevaluate, transition.PlanEffect);
    }

    [Theory]
    [InlineData("Confirmed")]
    [InlineData("Received")]
    public void Decide_DeletedCashEntryForAChargeThePlatformSettledThroughTheGateway_IsIgnored(string gatewayStatus)
    {
        var transition = ChargeRules.Decide(
            Local(Enum.Parse<ChargeStatus>(gatewayStatus)), Payload(ChargeStatus.ReceivedInCash, deleted: true), Deleted);

        Assert.Null(transition.NewStatus);
        Assert.Equal(PlanEffect.None, transition.PlanEffect);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Overdue")]
    public void Decide_DeletedAfterACashReceiptWasUndone_CancelsWithoutAffectingThePlan(string payloadStatus)
    {
        var transition = ChargeRules.Decide(
            Local(ChargeStatus.ReceivedInCash), Payload(Enum.Parse<ChargeStatus>(payloadStatus), deleted: true), Deleted);

        Assert.Equal(ChargeStatus.Cancelled, transition.NewStatus);
        Assert.Equal(PlanEffect.None, transition.PlanEffect);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Overdue")]
    [InlineData("Received")]
    [InlineData("ReceivedInCash")]
    [InlineData("Cancelled")]
    public void Decide_RefundedPayloadWithTheDeletedFlag_RecordsTheRefund(string localStatus)
    {
        var transition = ChargeRules.Decide(
            Local(Enum.Parse<ChargeStatus>(localStatus)), Payload(ChargeStatus.Refunded, deleted: true), Deleted);

        Assert.Equal(ChargeStatus.Refunded, transition.NewStatus);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Overdue")]
    [InlineData("ReceivedInCash")]
    [InlineData("Refunded")]
    public void Decide_DeletedFlagOnARefundedCharge_ChangesNothing(string payloadStatus)
    {
        var transition = ChargeRules.Decide(
            Local(ChargeStatus.Refunded), Payload(Enum.Parse<ChargeStatus>(payloadStatus), deleted: true), Deleted);

        Assert.Null(transition.NewStatus);
        Assert.Equal(PlanEffect.None, transition.PlanEffect);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Overdue")]
    [InlineData("Confirmed")]
    [InlineData("Received")]
    [InlineData("ReceivedInCash")]
    public void Decide_AnyUpdateForARefundedCharge_ChangesNothing(string payloadStatus)
    {
        var transition = ChargeRules.Decide(Local(ChargeStatus.Refunded), Payload(Enum.Parse<ChargeStatus>(payloadStatus)), Updated);

        Assert.Null(transition.NewStatus);
        Assert.Equal(PlanEffect.None, transition.PlanEffect);
    }

    [Theory]
    [InlineData(false, Updated)]
    [InlineData(true, Updated)]
    [InlineData(false, Restored)]
    public void Decide_RefundOfACancelledCharge_IsAppliedWhoeverCancelledIt(bool cancelledByStaff, string eventType)
    {
        var transition = ChargeRules.Decide(Local(ChargeStatus.Cancelled, cancelledByStaff), Payload(ChargeStatus.Refunded), eventType);

        Assert.Equal(ChargeStatus.Refunded, transition.NewStatus);
    }

    [Fact]
    public void Decide_DeletedReceivedInCash_CancelsWithoutAffectingThePlan()
    {
        var transition = ChargeRules.Decide(
            Local(ChargeStatus.ReceivedInCash), Payload(ChargeStatus.ReceivedInCash, deleted: true), Deleted);

        Assert.Equal(ChargeStatus.Cancelled, transition.NewStatus);
        Assert.Equal(PlanEffect.None, transition.PlanEffect);
    }

    [Fact]
    public void Decide_DeletedAlreadyCancelledCharge_StaysCancelled()
    {
        var transition = ChargeRules.Decide(Local(ChargeStatus.Cancelled), Payload(ChargeStatus.Pending, deleted: true), Deleted);

        Assert.Null(transition.NewStatus);
        Assert.Equal(PlanEffect.None, transition.PlanEffect);
    }

    [Fact]
    public void Decide_RestoredChargeCancelledByADeletion_Reopens()
    {
        var transition = ChargeRules.Decide(Local(ChargeStatus.Cancelled), Payload(ChargeStatus.Pending), Restored);

        Assert.Equal(ChargeStatus.Pending, transition.NewStatus);
        Assert.Equal(PlanEffect.Reevaluate, transition.PlanEffect);
    }

    [Fact]
    public void Decide_SettledEventWithoutARestoreForACancelledCharge_StaysCancelled()
    {
        var transition = ChargeRules.Decide(Local(ChargeStatus.Cancelled), Payload(ChargeStatus.Received), Updated);

        Assert.Null(transition.NewStatus);
        Assert.Equal(PlanEffect.None, transition.PlanEffect);
    }

    [Fact]
    public void Decide_RestoreThenSettledEvent_EndsSettled()
    {
        var restored = ChargeRules.Decide(Local(ChargeStatus.Cancelled), Payload(ChargeStatus.Pending), Restored);
        var afterRestore = Local(restored.NewStatus!.Value);

        var received = ChargeRules.Decide(afterRestore, Payload(ChargeStatus.Received), Updated);

        Assert.Equal(ChargeStatus.Pending, restored.NewStatus);
        Assert.Equal(ChargeStatus.Received, received.NewStatus);
    }

    [Theory]
    [InlineData(Restored, "Pending")]
    [InlineData(Updated, "Received")]
    public void Decide_RestoredOrSettledChargeCancelledByStaff_StaysCancelled(string eventType, string payloadStatus)
    {
        var transition = ChargeRules.Decide(
            Local(ChargeStatus.Cancelled, cancelledByStaff: true), Payload(Enum.Parse<ChargeStatus>(payloadStatus)), eventType);

        Assert.Null(transition.NewStatus);
        Assert.Equal(PlanEffect.None, transition.PlanEffect);
    }

    [Fact]
    public void Decide_NonSettlingUpdateForCancelledCharge_StaysCancelled()
    {
        var transition = ChargeRules.Decide(Local(ChargeStatus.Cancelled), Payload(ChargeStatus.Overdue), Updated);

        Assert.Null(transition.NewStatus);
    }

    [Fact]
    public void Decide_UnmodeledStatusWithTheDeletedFlag_StillCancelsAnOpenCharge()
    {
        // The status is not one this example maps, but the deleted flag is read before the status means anything.
        var payload = PaymentPayload.Parse("""{"payment":{"id":"pay_1","status":"CHARGEBACK_REQUESTED","deleted":true}}""");

        var transition = ChargeRules.Decide(Local(ChargeStatus.Pending), payload, Deleted);

        Assert.Null(payload.Status);
        Assert.Equal(ChargeStatus.Cancelled, transition.NewStatus);
        Assert.Equal(PlanEffect.None, transition.PlanEffect);
    }

    [Fact]
    public void Decide_UnmodeledStatusWithoutTheDeletedFlag_ChangesNothing()
    {
        var payload = PaymentPayload.Parse("""{"payment":{"id":"pay_1","status":"CHARGEBACK_REQUESTED"}}""");

        var transition = ChargeRules.Decide(Local(ChargeStatus.Pending), payload, Updated);

        Assert.False(payload.Deleted);
        Assert.Null(transition.NewStatus);
    }

    [Fact]
    public void Decide_OrdinaryStatusChange_IsAppliedAndReevaluatesThePlan()
    {
        var transition = ChargeRules.Decide(Local(ChargeStatus.Pending), Payload(ChargeStatus.Overdue), Updated);

        Assert.Equal(ChargeStatus.Overdue, transition.NewStatus);
        Assert.Equal(PlanEffect.Reevaluate, transition.PlanEffect);
    }

    [Fact]
    public void Decide_SameStatusAsLocal_ChangesNothing()
    {
        var transition = ChargeRules.Decide(Local(ChargeStatus.Pending), Payload(ChargeStatus.Pending), Updated);

        Assert.Null(transition.NewStatus);
        Assert.Equal(PlanEffect.None, transition.PlanEffect);
    }
}

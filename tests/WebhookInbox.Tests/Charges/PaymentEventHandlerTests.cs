using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace WebhookInbox.Tests;

/// <summary>
/// What the handler reports for each outcome (NoOp, Applied, SkippedNotFound) and when it asks for the plan to be
/// re-evaluated. The handler does not save: the processor owns the transaction, so these tests read the tracked
/// entity, except the not-found one, which saves as the processor would and reads the rows back.
/// </summary>
[Collection(SharedPostgres.Name)]
public class PaymentEventHandlerTests(PostgresFixture postgres)
{
    private const string Entity = "pay_1";
    private const long PlanId = 42;

    private static InboxEvent Event(string eventType, string status, bool deleted = false) => new()
    {
        ProviderEventId = "evt-1",
        EventType = eventType,
        EntityKey = Entity,
        Payload = TestDatabase.PaymentBody("evt-1", eventType, Entity, status, deleted)
    };

    private async Task<(InboxStatus Result, Charge Charge, RecordingPlanService Plans)> HandleAsync(
        ChargeStatus localStatus, string eventType, string payloadStatus, bool deleted = false, bool cancelledByStaff = false,
        long? planId = PlanId, ListLogger<PaymentEventHandler>? logger = null)
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await TestDatabase.AddChargeAsync(connectionString, Entity, localStatus, cancelledByStaff, planId);
        await using var db = TestDatabase.Open(connectionString);
        var plans = new RecordingPlanService();

        var result = await new PaymentEventHandler(db, plans, logger ?? new ListLogger<PaymentEventHandler>())
            .HandleAsync(Event(eventType, payloadStatus, deleted), CancellationToken.None);

        return (result, db.Charges.Local.Single(), plans);
    }

    [Theory]
    [InlineData("Cancelled", "RECEIVED", "cancelled charge stays cancelled")]
    [InlineData("Cancelled", "CONFIRMED", "cancelled charge stays cancelled")]
    [InlineData("Refunded", "RECEIVED", "refunded charge stays refunded")]
    public async Task Handle_PaymentArrivesForAChargeTheRuleKeepsClosed_IsNoOpButLogsAWarning(
        string local, string payloadStatus, string reason)
    {
        // The money came in and the charge stays closed on purpose (a plan switch cancels like a deletion). That can be
        // right, but it is the case where "processed" and "applied" part ways, so it has to leave a line to read.
        var logger = new ListLogger<PaymentEventHandler>();

        var localStatus = Enum.Parse<ChargeStatus>(local);

        var (result, charge, _) = await HandleAsync(localStatus, "PAYMENT_UPDATED", payloadStatus, logger: logger);

        Assert.Equal(InboxStatus.NoOp, result);
        Assert.Equal(localStatus, charge.Status);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(reason, warning.Message, StringComparison.Ordinal);
        Assert.Contains("evt-1", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Pending", "PENDING")]
    [InlineData("Received", "RECEIVED")]
    [InlineData("Cancelled", "OVERDUE")]
    public async Task Handle_NoOpThatIsNotAPaymentOnAClosedCharge_LogsNothing(string local, string payloadStatus)
    {
        var logger = new ListLogger<PaymentEventHandler>();

        var (result, _, _) = await HandleAsync(Enum.Parse<ChargeStatus>(local), "PAYMENT_UPDATED", payloadStatus, logger: logger);

        Assert.Equal(InboxStatus.NoOp, result);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Handle_PayloadSaysWhatTheChargeAlreadyIs_IsNoOpAndAsksForNothing()
    {
        var (result, charge, plans) = await HandleAsync(ChargeStatus.Pending, "PAYMENT_UPDATED", "PENDING");

        Assert.Equal(InboxStatus.NoOp, result);
        Assert.Equal(ChargeStatus.Pending, charge.Status);
        Assert.Empty(plans.Reevaluated);
    }

    [Fact]
    public async Task Handle_CancelledChargeReceivesASettledStatusWithoutARestore_IsNoOpAndStaysCancelled()
    {
        var (result, charge, plans) = await HandleAsync(ChargeStatus.Cancelled, "PAYMENT_UPDATED", "RECEIVED");

        Assert.Equal(InboxStatus.NoOp, result);
        Assert.Equal(ChargeStatus.Cancelled, charge.Status);
        Assert.Empty(plans.Reevaluated);
    }

    [Fact]
    public async Task Handle_AChangeThatNeedsTheAccessReviewed_IsAppliedAndReevaluatesThePlanOnce()
    {
        var (result, charge, plans) = await HandleAsync(ChargeStatus.Pending, "PAYMENT_UPDATED", "OVERDUE");

        Assert.Equal(InboxStatus.Applied, result);
        Assert.Equal(ChargeStatus.Overdue, charge.Status);
        Assert.Equal([PlanId], plans.Reevaluated);
    }

    [Fact]
    public async Task Handle_ChangeOnAChargeWithoutAPlan_IsAppliedAndReevaluatesNothing()
    {
        var (result, _, plans) = await HandleAsync(ChargeStatus.Pending, "PAYMENT_UPDATED", "OVERDUE", planId: null);

        Assert.Equal(InboxStatus.Applied, result);
        Assert.Empty(plans.Reevaluated);
    }

    [Fact]
    public async Task Handle_DeletionThatCancelsTheCharge_IsAppliedAndDoesNotReevaluateThePlan()
    {
        // A deleted open charge must not block or suspend anyone: cancelling it leaves the plan alone.
        var (result, charge, plans) = await HandleAsync(ChargeStatus.Pending, "PAYMENT_DELETED", "PENDING", deleted: true);

        Assert.Equal(InboxStatus.Applied, result);
        Assert.Equal(ChargeStatus.Cancelled, charge.Status);
        Assert.Empty(plans.Reevaluated);
    }

    [Fact]
    public async Task Handle_DeletionForAChargeWeDoNotHave_IsSkippedAndChangesNoOtherRecord()
    {
        // A charge we do not have must not be matched to some other row. The charges we do have, open and with a plan,
        // are what a wrong lookup would cancel; saving as the processor would lets their xmin show whether anything
        // wrote to them, and counting them shows whether anything was added.
        const string UnknownCharge = "pay_unknown";
        const string OtherCharge = "pay_other";
        const long OtherPlanId = 43;
        var connectionString = await TestDatabase.CreateAsync(postgres);
        var before = new[]
        {
            await TestDatabase.AddChargeAsync(connectionString, Entity, ChargeStatus.Pending, planId: PlanId),
            await TestDatabase.AddChargeAsync(connectionString, OtherCharge, ChargeStatus.Overdue, planId: OtherPlanId)
        };
        await using var db = TestDatabase.Open(connectionString);
        var plans = new RecordingPlanService();
        var deletion = new InboxEvent
        {
            ProviderEventId = "evt-1",
            EventType = "PAYMENT_DELETED",
            EntityKey = UnknownCharge,
            Payload = TestDatabase.PaymentBody("evt-1", "PAYMENT_DELETED", UnknownCharge, "PENDING", deleted: true)
        };

        var result = await new PaymentEventHandler(db, plans, new ListLogger<PaymentEventHandler>())
            .HandleAsync(deletion, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(InboxStatus.SkippedNotFound, result);
        Assert.Empty(plans.Reevaluated);
        Assert.Equal(before.Length, await db.Charges.CountAsync());
        foreach (var charge in before)
        {
            var after = await TestDatabase.ReadChargeAsync(connectionString, charge.ProviderId);
            Assert.Equal(charge.Status, after.Status);
            Assert.Equal(charge.Version, after.Version);
        }
    }
}

using Microsoft.EntityFrameworkCore;

namespace WebhookInbox.Tests;

/// <summary>
/// The 18 audited cases run through the real handler and a real database. Each case starts from the state the
/// audit found (an open charge the provider had deleted), receives the deletion event, and must end the way the
/// transition rules say: charge cancelled, plan not re-evaluated, charges that still exist untouched.
/// </summary>
[Collection(SharedPostgres.Name)]
public class AuditedCasesTests(PostgresFixture postgres)
{
    private const long PlanId = 1;

    public static TheoryData<int> Cases => new(AuditedCases.CaseNumbers);

    public static TheoryData<string> Customers => new(AuditedCases.Labels);

    [Fact]
    public void AuditedCases_AreTheEighteenFoundByTheAudit()
    {
        Assert.Equal(Enumerable.Range(1, 18), AuditedCases.CaseNumbers);
        Assert.Equal(9, AuditedCases.Customers.Count);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task DeletionEvent_ForAnAuditedCase_CancelsThatChargeAndLeavesThePlanAlone(int caseNumber)
    {
        var customer = AuditedCases.CustomerOf(caseNumber);
        var connectionString = await Seed(customer);
        var plans = new RecordingPlanService();
        var target = customer.Charges.Single(c => c.CaseNumber == caseNumber);

        await Apply(connectionString, plans, target);

        Assert.Equal(ChargeStatus.Cancelled, (await TestDatabase.ReadChargeAsync(connectionString, target.Key)).Status);
        Assert.Empty(plans.Reevaluated);
        foreach (var other in customer.Charges.Where(c => c.Key != target.Key))
        {
            Assert.Equal(other.LocalStatus, (await TestDatabase.ReadChargeAsync(connectionString, other.Key)).Status);
        }
    }

    [Theory]
    [MemberData(nameof(Customers))]
    public async Task DeletionEvents_ForAnAuditedCustomer_LeaveAccessToWhatStillExists(string label)
    {
        var customer = AuditedCases.Customer(label);
        var connectionString = await Seed(customer);
        var plans = new RecordingPlanService();

        foreach (var deleted in customer.Charges.Where(c => c.DeletedAtProvider))
        {
            await Apply(connectionString, plans, deleted);
        }

        await using var db = TestDatabase.Open(connectionString);
        var charges = await db.Charges.AsNoTracking().ToListAsync();
        Assert.All(customer.Charges.Where(c => c.DeletedAtProvider),
            c => Assert.Equal(ChargeStatus.Cancelled, charges.Single(x => x.ProviderId == c.Key).Status));
        Assert.Empty(plans.Reevaluated);
        if (customer.ExpectedBlockedAfterRepair is { } expectedBlocked)
        {
            Assert.Equal(expectedBlocked, AuditedCases.IsBlocked(customer.Charges.Select(c => (c, charges.Single(x => x.ProviderId == c.Key).Status))));
        }
        else
        {
            Assert.False(customer.PlanActive);
        }
    }

    [Fact]
    public async Task Customer05_WasBlockedByAPendingChargeAlreadyPastDueAndIsReleasedOnceItIsCancelled()
    {
        // The deleted charge was still PENDING locally, but 17 days past its due date, which the access gate treats
        // as blocking. After the repair only a charge that is not yet due remains.
        var customer = AuditedCases.Customer("customer-05");
        var connectionString = await Seed(customer);
        var deleted = customer.Charges.Single(c => c.DeletedAtProvider);
        Assert.Equal(ChargeStatus.Pending, deleted.LocalStatus);
        Assert.True(deleted.DueInDays < 0);
        Assert.True(AuditedCases.IsBlocked(customer.Charges.Select(c => (c, c.LocalStatus))));

        await Apply(connectionString, new RecordingPlanService(), deleted);

        await using var db = TestDatabase.Open(connectionString);
        var charges = await db.Charges.AsNoTracking().ToListAsync();
        Assert.False(AuditedCases.IsBlocked(customer.Charges.Select(c => (c, charges.Single(x => x.ProviderId == c.Key).Status))));
    }

    [Fact]
    public void Customer04_StaysBlockedBecauseAnotherOverdueChargeStillExists()
    {
        var customer = AuditedCases.Customer("customer-04");
        var afterRepair = customer.Charges.Select(c => (c, c.DeletedAtProvider ? ChargeStatus.Cancelled : c.LocalStatus));

        Assert.True(AuditedCases.IsBlocked(afterRepair));
    }

    private async Task<string> Seed(AuditedCustomer customer)
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        foreach (var charge in customer.Charges)
        {
            await TestDatabase.AddChargeAsync(connectionString, charge.Key, charge.LocalStatus, planId: PlanId);
        }
        return connectionString;
    }

    // The provider's deletion event keeps the charge's previous status and adds deleted: true.
    private static async Task Apply(string connectionString, RecordingPlanService plans, AuditedCharge charge)
    {
        var status = charge.LocalStatus.ToString().ToUpperInvariant();
        var body = TestDatabase.PaymentBody($"evt-{charge.Key}", "PAYMENT_DELETED", charge.Key, status, deleted: true);
        var evt = await TestDatabase.AddEventAsync(connectionString, $"evt-{charge.Key}", "PAYMENT_DELETED", charge.Key, body);

        await using var db = TestDatabase.Open(connectionString);
        var handler = new PaymentEventHandler(db, plans, new ListLogger<PaymentEventHandler>());
        var inboxEvent = await db.InboxEvents.SingleAsync(e => e.Id == evt.Id);
        inboxEvent.Status = await handler.HandleAsync(inboxEvent, CancellationToken.None);
        await db.SaveChangesAsync();
        Assert.Equal(InboxStatus.Applied, inboxEvent.Status);
    }
}

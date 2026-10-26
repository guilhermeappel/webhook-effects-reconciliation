using Microsoft.EntityFrameworkCore;

namespace WebhookInbox.Tests;

[Collection(SharedPostgres.Name)]
public class RepairDeletedChargesTests(PostgresFixture postgres)
{
    private const string Entity = "pay_1";

    /// <summary>A provider whose answer is fixed, optionally running a side effect first (a concurrent writer).</summary>
    private sealed class FakeProvider(ProviderCharge? answer, Func<Task>? beforeAnswering = null) : IProviderReadClient
    {
        public int Calls { get; private set; }

        public async Task<ProviderCharge?> GetChargeAsync(string providerId, CancellationToken ct)
        {
            Calls++;
            if (beforeAnswering is not null)
            {
                await beforeAnswering();
            }
            return answer;
        }
    }

    private static ProviderCharge ConfirmedDeleted(ChargeStatus status = ChargeStatus.Pending) => new(Entity, status, Deleted: true);

    private static Task<string> RunAsync(string connectionString, IProviderReadClient provider, long chargeId, bool apply) =>
        RunAsync(connectionString, provider, [chargeId], apply);

    private static async Task<string> RunAsync(string connectionString, IProviderReadClient provider, long[] chargeIds, bool apply)
    {
        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            await using var db = TestDatabase.Open(connectionString);
            await new RepairDeletedCharges(db, provider).RunAsync(chargeIds, apply, CancellationToken.None);
        }
        finally
        {
            Console.SetOut(original);
        }
        return output.ToString();
    }

    private async Task<(string ConnectionString, Charge Charge)> Seed(ChargeStatus status = ChargeStatus.Pending)
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        return (connectionString, await TestDatabase.AddChargeAsync(connectionString, Entity, status));
    }

    [Fact]
    public async Task DryRun_WhenTheProviderConfirmsTheDeletion_ReportsTheChangeAndWritesNothing()
    {
        var (connectionString, charge) = await Seed();
        var before = await TestDatabase.ReadChargeAsync(connectionString, Entity);

        var output = await RunAsync(connectionString, new FakeProvider(ConfirmedDeleted()), charge.Id, apply: false);

        var after = await TestDatabase.ReadChargeAsync(connectionString, Entity);
        Assert.Equal(ChargeStatus.Pending, after.Status);
        Assert.Equal(before.Version, after.Version);   // xmin: the row was not written at all
        Assert.Contains("Pending -> Cancelled", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRun_StillAsksTheProvider()
    {
        var (connectionString, charge) = await Seed();
        var provider = new FakeProvider(ConfirmedDeleted());

        await RunAsync(connectionString, provider, charge.Id, apply: false);

        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Apply_WhenTheProviderConfirmsTheDeletion_CancelsTheCharge()
    {
        var (connectionString, charge) = await Seed();

        await RunAsync(connectionString, new FakeProvider(ConfirmedDeleted()), charge.Id, apply: true);

        Assert.Equal(ChargeStatus.Cancelled, (await TestDatabase.ReadChargeAsync(connectionString, Entity)).Status);
    }

    [Fact]
    public async Task Run_AnIdThatDoesNotExist_IsSkippedAndTheRestOfTheBatchStillRuns()
    {
        var (connectionString, charge) = await Seed();
        const long Missing = 987_654;

        var output = await RunAsync(connectionString, new FakeProvider(ConfirmedDeleted()), [Missing, charge.Id], apply: true);

        Assert.Contains($"charge {Missing}: not found, skipped", output, StringComparison.Ordinal);
        Assert.Equal(ChargeStatus.Cancelled, (await TestDatabase.ReadChargeAsync(connectionString, Entity)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_ProviderDoesNotConfirmTheDeletion_SkipsTheCharge(bool providerKnowsTheCharge)
    {
        var (connectionString, charge) = await Seed();
        ProviderCharge? answer = providerKnowsTheCharge ? new ProviderCharge(Entity, ChargeStatus.Pending, Deleted: false) : null;

        var output = await RunAsync(connectionString, new FakeProvider(answer), charge.Id, apply: true);

        Assert.Equal(ChargeStatus.Pending, (await TestDatabase.ReadChargeAsync(connectionString, Entity)).Status);
        Assert.Contains("provider does not confirm deletion", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Failed")]
    public async Task Run_InboxHasAnUnfinishedEventForTheCharge_SkipsItWithoutAskingTheProvider(string inboxStatus)
    {
        var (connectionString, charge) = await Seed();
        await TestDatabase.AddEventAsync(connectionString, "evt-1", "PAYMENT_UPDATED", Entity,
            TestDatabase.PaymentBody("evt-1", "PAYMENT_UPDATED", Entity, "PENDING"), Enum.Parse<InboxStatus>(inboxStatus));
        var provider = new FakeProvider(ConfirmedDeleted());

        var output = await RunAsync(connectionString, provider, charge.Id, apply: true);

        Assert.Equal(0, provider.Calls);
        Assert.Equal(ChargeStatus.Pending, (await TestDatabase.ReadChargeAsync(connectionString, Entity)).Status);
        Assert.Contains("unfinished events", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_InboxHasAnEventWaitingForItsShortRetries_SkipsTheCharge()
    {
        // An old PAYMENT_RESTORED that was skipped as "not found" is still ordered ahead of later events by the
        // processor; the repair must not cancel the charge underneath it and let the retry reopen it afterwards.
        var (connectionString, charge) = await Seed();
        await TestDatabase.AddEventAsync(connectionString, "evt-1", "PAYMENT_RESTORED", Entity,
            TestDatabase.PaymentBody("evt-1", "PAYMENT_RESTORED", Entity, "PENDING"), InboxStatus.SkippedNotFound, notFoundAttempts: 1);
        var provider = new FakeProvider(ConfirmedDeleted());

        var output = await RunAsync(connectionString, provider, charge.Id, apply: true);

        Assert.Equal(0, provider.Calls);
        Assert.Equal(ChargeStatus.Pending, (await TestDatabase.ReadChargeAsync(connectionString, Entity)).Status);
        Assert.Contains("unfinished events", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_InboxEventSkippedAfterItsLastShortRetry_DoesNotSkipTheCharge()
    {
        var (connectionString, charge) = await Seed();
        await TestDatabase.AddEventAsync(connectionString, "evt-1", "PAYMENT_RESTORED", Entity,
            TestDatabase.PaymentBody("evt-1", "PAYMENT_RESTORED", Entity, "PENDING"), InboxStatus.SkippedNotFound,
            notFoundAttempts: InboxRetry.NotFoundAttempts);

        await RunAsync(connectionString, new FakeProvider(ConfirmedDeleted()), charge.Id, apply: true);

        Assert.Equal(ChargeStatus.Cancelled, (await TestDatabase.ReadChargeAsync(connectionString, Entity)).Status);
    }

    [Fact]
    public async Task Run_InboxEventsAreAllFinished_DoesNotSkipTheCharge()
    {
        var (connectionString, charge) = await Seed();
        await TestDatabase.AddEventAsync(connectionString, "evt-1", "PAYMENT_UPDATED", Entity,
            TestDatabase.PaymentBody("evt-1", "PAYMENT_UPDATED", Entity, "PENDING"), InboxStatus.Applied);
        await TestDatabase.AddEventAsync(connectionString, "evt-2", "PAYMENT_UPDATED", Entity, "{}", InboxStatus.DeadLettered);

        await RunAsync(connectionString, new FakeProvider(ConfirmedDeleted()), charge.Id, apply: true);

        Assert.Equal(ChargeStatus.Cancelled, (await TestDatabase.ReadChargeAsync(connectionString, Entity)).Status);
    }

    [Fact]
    public async Task Apply_ChargeChangedByTheProcessorMeanwhile_ReportsTheConflictAndDoesNotOverwriteIt()
    {
        var (connectionString, charge) = await Seed();
        // While the repair is asking the provider, the processor settles the charge.
        var provider = new FakeProvider(ConfirmedDeleted(), async () =>
        {
            await using var concurrent = TestDatabase.Open(connectionString);
            var row = await concurrent.Charges.SingleAsync(c => c.ProviderId == Entity);
            row.Status = ChargeStatus.Received;
            await concurrent.SaveChangesAsync();
        });

        var output = await RunAsync(connectionString, provider, charge.Id, apply: true);

        Assert.Equal(ChargeStatus.Received, (await TestDatabase.ReadChargeAsync(connectionString, Entity)).Status);
        Assert.Contains("changed concurrently", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_ConflictOnTheFirstChargeOfABatch_DoesNotStopTheNextOne()
    {
        // The repair writes one record at a time with the xmin check (docs/design-notes.md, The repair). A conflict
        // on one record must leave the rest of the batch to run, with nothing of the failed write carried over.
        var (connectionString, first) = await Seed();
        var second = await TestDatabase.AddChargeAsync(connectionString, "pay_2", ChargeStatus.Pending);
        var provider = new PerChargeProvider(async providerId =>
        {
            if (providerId == Entity)
            {
                await using var concurrent = TestDatabase.Open(connectionString);
                var row = await concurrent.Charges.SingleAsync(c => c.ProviderId == Entity);
                row.Status = ChargeStatus.Overdue;
                await concurrent.SaveChangesAsync();
            }
            return new ProviderCharge(providerId, ChargeStatus.Pending, Deleted: true);
        });

        var output = await RunAsync(connectionString, provider, [first.Id, second.Id], apply: true);

        Assert.Equal(ChargeStatus.Overdue, (await TestDatabase.ReadChargeAsync(connectionString, Entity)).Status);
        Assert.Equal(ChargeStatus.Cancelled, (await TestDatabase.ReadChargeAsync(connectionString, "pay_2")).Status);
        Assert.Contains($"charge {first.Id}: changed concurrently", output, StringComparison.Ordinal);
        Assert.DoesNotContain($"charge {second.Id}: changed concurrently", output, StringComparison.Ordinal);
    }

    private sealed class PerChargeProvider(Func<string, Task<ProviderCharge?>> answer) : IProviderReadClient
    {
        public Task<ProviderCharge?> GetChargeAsync(string providerId, CancellationToken ct) => answer(providerId);
    }

    [Fact]
    public async Task Apply_AfterAConflict_TheNextRunSucceeds()
    {
        var (connectionString, charge) = await Seed();
        var conflicting = new FakeProvider(ConfirmedDeleted(), async () =>
        {
            await using var concurrent = TestDatabase.Open(connectionString);
            var row = await concurrent.Charges.SingleAsync(c => c.ProviderId == Entity);
            row.Status = ChargeStatus.Overdue;
            await concurrent.SaveChangesAsync();
        });
        await RunAsync(connectionString, conflicting, charge.Id, apply: true);

        var output = await RunAsync(connectionString, new FakeProvider(ConfirmedDeleted(ChargeStatus.Overdue)), charge.Id, apply: true);

        Assert.Equal(ChargeStatus.Cancelled, (await TestDatabase.ReadChargeAsync(connectionString, Entity)).Status);
        Assert.Contains("Overdue -> Cancelled", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_ChargeSettledByCardAndDeletedAtTheProvider_IsLeftAsItIs()
    {
        var (connectionString, charge) = await Seed(ChargeStatus.Received);

        var output = await RunAsync(connectionString, new FakeProvider(ConfirmedDeleted(ChargeStatus.Received)), charge.Id, apply: true);

        Assert.Equal(ChargeStatus.Received, (await TestDatabase.ReadChargeAsync(connectionString, Entity)).Status);
        Assert.Contains("unchanged", output, StringComparison.Ordinal);
    }
}

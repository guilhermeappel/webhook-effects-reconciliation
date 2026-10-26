using Microsoft.EntityFrameworkCore;

namespace WebhookInbox;

internal interface IProviderReadClient
{
    Task<ProviderCharge?> GetChargeAsync(string providerId, CancellationToken ct);   // GET only
}

// --- Repair: one record at a time, same rules as the live handler, dry run by default
internal sealed class RepairDeletedCharges(PaymentsDbContext db, IProviderReadClient provider)
{
    public async Task RunAsync(IReadOnlyList<long> chargeIds, bool apply, CancellationToken ct)
    {
        foreach (var id in chargeIds)
        {
            var charge = await db.Charges.SingleOrDefaultAsync(c => c.Id == id, ct);
            if (charge is null)
            {
                Console.WriteLine($"charge {id}: not found, skipped");
                continue;
            }

            var unfinished = await db.InboxEvents.Where(InboxRetry.Unfinished)
                .AnyAsync(e => e.EntityKey == charge.ProviderId, ct);
            if (unfinished)
            {
                Console.WriteLine($"charge {id}: inbox still has unfinished events, skipped");
                continue;
            }

            var remote = await provider.GetChargeAsync(charge.ProviderId, ct);   // also in dry run
            if (remote is not { Deleted: true })
            {
                Console.WriteLine($"charge {id}: provider does not confirm deletion, skipped");
                continue;
            }

            var payload = new PaymentPayload(remote.Id, remote.Status, Deleted: true);
            var transition = ChargeRules.Decide(charge, payload, "PAYMENT_DELETED");
            Console.WriteLine($"charge {id}: {charge.Status} -> {transition.NewStatus?.ToString() ?? "unchanged"} ({transition.Reason})");
            if (!apply || transition.NewStatus is not { } newStatus)
            {
                continue;
            }

            charge.Status = newStatus;
            try
            {
                await db.SaveChangesAsync(ct);   // xmin check: fails if the processor changed it meanwhile
            }
            catch (DbUpdateConcurrencyException)
            {
                Console.WriteLine($"charge {id}: changed concurrently, skipped; run again");
                db.ChangeTracker.Clear();
            }
        }
    }
}

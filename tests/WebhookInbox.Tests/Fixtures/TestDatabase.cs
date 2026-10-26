using Microsoft.EntityFrameworkCore;

namespace WebhookInbox.Tests;

/// <summary>Opens the application's own context against a fresh database whose schema comes from the model.</summary>
internal static class TestDatabase
{
    public static async Task<string> CreateAsync(PostgresFixture postgres)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var db = Open(connectionString);
        await db.Database.EnsureCreatedAsync();
        return connectionString;
    }

    public static PaymentsDbContext Open(string connectionString) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(connectionString).Options);

    public static async Task<Charge> AddChargeAsync(
        string connectionString, string providerId, ChargeStatus status, bool cancelledByStaff = false, long? planId = null)
    {
        await using var db = Open(connectionString);
        var charge = new Charge { ProviderId = providerId, Status = status, CancelledByStaff = cancelledByStaff, PlanId = planId };
        db.Charges.Add(charge);
        await db.SaveChangesAsync();
        return charge;
    }

    public static async Task<InboxEvent> AddEventAsync(
        string connectionString, string eventId, string eventType, string? entityKey, string payload,
        InboxStatus status = InboxStatus.Pending, int attempts = 0, int notFoundAttempts = 0)
    {
        await using var db = Open(connectionString);
        var inboxEvent = new InboxEvent
        {
            ProviderEventId = eventId,
            EventType = eventType,
            EntityKey = entityKey,
            Payload = payload,
            ReceivedAt = DateTimeOffset.UtcNow,
            Status = status,
            Attempts = attempts,
            NotFoundAttempts = notFoundAttempts
        };
        db.InboxEvents.Add(inboxEvent);
        await db.SaveChangesAsync();
        return inboxEvent;
    }

    public static async Task<Charge> ReadChargeAsync(string connectionString, string providerId)
    {
        await using var db = Open(connectionString);
        return await db.Charges.AsNoTracking().SingleAsync(c => c.ProviderId == providerId);
    }

    public static async Task<InboxEvent> ReadEventAsync(string connectionString, string eventId)
    {
        await using var db = Open(connectionString);
        return await db.InboxEvents.AsNoTracking().SingleAsync(e => e.ProviderEventId == eventId);
    }

    /// <summary>A webhook body shaped like the ones the provider sends for a charge.</summary>
    public static string PaymentBody(string eventId, string eventType, string paymentId, string status, bool deleted = false) =>
        $$$"""{"id":"{{{eventId}}}","event":"{{{eventType}}}","payment":{"id":"{{{paymentId}}}","status":"{{{status}}}","deleted":{{{(deleted ? "true" : "false")}}}}}""";
}

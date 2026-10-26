using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace WebhookInbox;

internal sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<InboxEvent> InboxEvents => Set<InboxEvent>();
    public DbSet<Charge> Charges => Set<Charge>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<InboxEvent>().HasIndex(e => e.ProviderEventId).IsUnique();
        model.Entity<InboxEvent>().HasIndex(e => new { e.Status, e.NextAttemptAt });
        model.Entity<InboxEvent>().HasIndex(e => e.EntityKey);
        model.Entity<Charge>().HasIndex(c => c.ProviderId).IsUnique();
        model.Entity<Charge>().Property(c => c.Version).IsRowVersion();   // maps to xmin
    }
}

internal static class DbUpdateExceptionExtensions
{
    public static bool IsUniqueViolation(this DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    // The database refused the values themselves (class 22, data exception: a NUL character, for example) or their
    // size (an index entry that is too large). A timeout, a lost connection or a lock says nothing about the data.
    public static bool IsUnstorableData(this DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: { } state }
            && (state.StartsWith(DataExceptionClass, StringComparison.Ordinal) || state == PostgresErrorCodes.ProgramLimitExceeded);

    private const string DataExceptionClass = "22";
}

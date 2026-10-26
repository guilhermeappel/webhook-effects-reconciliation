using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;

namespace WebhookInbox;

internal sealed class InboxSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }
}

// the processor owns retries. One instance; each event in its own scope and transaction.
// Several instances would need row locking or a lease, not only a filter on the status: selecting and
// processing in the same transaction, or a lease on the row.
internal sealed class InboxProcessor(
    IServiceScopeFactory scopes, InboxSignal signal, TimeProvider clock,
    ILogger<InboxProcessor> logger) : BackgroundService
{
    private const int MaxAttempts = 8;
    private const double MaxBackoffMinutes = 60;
    private const double BackoffBase = 2;         // minutes after the first failure, doubling up to the cap
    private const int NotFoundRetryMinutes = 5;   // between two looks for an entity that is not there yet
    private const int BatchSize = 50;
    private const int MaxLastErrorLength = 1000;   // an exception message can carry a whole statement or payload

    // How long the loop sleeps when nothing is due (a stored event wakes it sooner), and how long it waits after a
    // pass that failed, such as a database that is down.
    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailedPassDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var due = await DueEventIdsAsync(stoppingToken);
                foreach (var id in due)
                {
                    await ProcessOneAsync(id, stoppingToken);
                }

                if (due.Count == 0)
                {
                    await signal.WaitAsync(IdleWait, stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // A database outage must not stop the host; wait and try again.
                logger.LogError(ex, "Inbox processor loop failed");
                await Task.Delay(FailedPassDelay, stoppingToken);
            }
        }
    }

    // Due events, never overtaking an earlier unfinished event for the same entity.
    // A dead-lettered event no longer blocks its successors; its alert is the signal
    // that this charge needs a look. A "not found" event blocks them only while it still
    // has short retries left, so a retry cannot apply an old payload over a newer one.
    // EF Core compares keys with C# null semantics, so two events without a key would count
    // as the same entity; they are never held back.
    private async Task<List<long>> DueEventIdsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var now = clock.GetUtcNow();
        return await db.InboxEvents
            .Where(e => e.Status == InboxStatus.Pending
                     || (e.Status == InboxStatus.Failed && e.NextAttemptAt <= now)
                     || (e.Status == InboxStatus.SkippedNotFound
                         && e.NotFoundAttempts < InboxRetry.NotFoundAttempts && e.NextAttemptAt <= now))
            .Where(e => e.EntityKey == null
                     || !db.InboxEvents.Where(InboxRetry.Unfinished).Any(p => p.EntityKey == e.EntityKey && p.Id < e.Id))
            .OrderBy(e => e.Id)
            .Select(e => e.Id)
            .Take(BatchSize)
            .ToListAsync(ct);
    }

    private async Task ProcessOneAsync(long id, CancellationToken ct)
    {
        InboxEvent? gaveUp = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var dispatcher = scope.ServiceProvider.GetRequiredService<EventDispatcher>();

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var evt = await db.InboxEvents.SingleAsync(e => e.Id == id, ct);
            evt.Status = await dispatcher.HandleAsync(evt, ct);
            evt.LastError = null;
            if (evt.Status == InboxStatus.SkippedNotFound)
            {
                evt.NotFoundAttempts++;
                evt.NextAttemptAt = clock.GetUtcNow().AddMinutes(NotFoundRetryMinutes);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            if (evt.Status == InboxStatus.SkippedNotFound && evt.NotFoundAttempts >= InboxRetry.NotFoundAttempts)
            {
                gaveUp = evt;
            }
        }
        catch (PayloadFormatException ex) when (!ct.IsCancellationRequested)
        {
            await DeadLetterAsync(id, ex, ct);      // the wrong shape stays the wrong shape: do not retry
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await RecordFailureAsync(id, ex, ct);   // the failed transaction rolled back
        }

        if (gaveUp is not null)
        {
            WarnThatTheShortRetriesRanOut(gaveUp);
        }
    }

    // The last short retry found nothing: the row stays SkippedNotFound for good, and nobody reads rows.
    // A warning, not an error: most of these are people the platform has no record of. This runs after the commit and
    // outside the try block on purpose: a log sink that throws must not turn a saved row into a failure to retry.
    private void WarnThatTheShortRetriesRanOut(InboxEvent evt)
    {
        try
        {
            logger.LogWarning("Inbox event {EventId} for {EntityKey} found no local record after {Attempts} attempts and stays skipped",
                LogText.Safe(evt.ProviderEventId), LogText.Safe(evt.EntityKey), evt.NotFoundAttempts);   // wire this log to an alert
        }
        catch (Exception)
        {
            // Nothing to do: the event is already recorded, and the loop must keep going.
        }
    }

    private async Task DeadLetterAsync(long id, Exception error, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var evt = await db.InboxEvents.SingleAsync(e => e.Id == id, ct);

        evt.Attempts++;
        evt.LastError = LogText.Cut(error.Message, MaxLastErrorLength);
        evt.Status = InboxStatus.DeadLettered;
        logger.LogError(error, "Inbox event {EventId} dead-lettered: unreadable payload", LogText.Safe(evt.ProviderEventId));   // wire this log to an alert
        await db.SaveChangesAsync(ct);
    }

    private async Task RecordFailureAsync(long id, Exception error, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var evt = await db.InboxEvents.SingleAsync(e => e.Id == id, ct);

        // The reason is in the innermost exception: a failed save says only "an error occurred while saving the entity
        // changes", and the database's own message is what a person looking at the row needs.
        var reason = error.GetBaseException().Message;
        evt.Attempts++;
        evt.LastError = LogText.Cut(reason, MaxLastErrorLength);
        var retryIn = default(TimeSpan?);
        if (evt.Attempts >= MaxAttempts)
        {
            evt.Status = InboxStatus.DeadLettered;
            logger.LogError(error, "Inbox event {EventId} dead-lettered after {Attempts} attempts",
                LogText.Safe(evt.ProviderEventId), evt.Attempts);       // wire this log to an alert
        }
        else
        {
            evt.Status = InboxStatus.Failed;
            retryIn = TimeSpan.FromMinutes(Math.Min(Math.Pow(BackoffBase, evt.Attempts), MaxBackoffMinutes));
            evt.NextAttemptAt = clock.GetUtcNow() + retryIn;
        }
        await db.SaveChangesAsync(ct);

        if (retryIn is { } wait)
        {
            WarnThatAnAttemptFailed(evt, wait, reason);
        }
    }

    // Only the last failure used to be logged, so the first seven attempts of an event that kept failing left no line
    // anywhere but in LastError. Like the other warning, it runs after the save: a log sink that throws must not undo it.
    private void WarnThatAnAttemptFailed(InboxEvent evt, TimeSpan retryIn, string reason)
    {
        try
        {
            logger.LogWarning("Inbox event {EventId} failed attempt {Attempts} of {MaxAttempts}; trying again in {RetryIn}: {Reason}",
                LogText.Safe(evt.ProviderEventId), evt.Attempts, MaxAttempts, retryIn, LogText.Safe(reason));
        }
        catch (Exception)
        {
            // Nothing to do: the failure is already recorded, and the loop must keep going.
        }
    }
}

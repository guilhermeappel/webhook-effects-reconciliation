using System.Linq.Expressions;

namespace WebhookInbox;

// How long the processor keeps trying, and what it still counts as unfinished work. The processor and the
// repair read the same definition: a repair must never run underneath an event the processor will still apply.
internal static class InboxRetry
{
    public const int NotFoundAttempts = 3;   // the entity may still be committing elsewhere

    public static readonly Expression<Func<InboxEvent, bool>> Unfinished = e =>
        e.Status == InboxStatus.Pending || e.Status == InboxStatus.Failed
        || (e.Status == InboxStatus.SkippedNotFound && e.NotFoundAttempts < NotFoundAttempts);
}

internal sealed class InboxEvent
{
    public long Id { get; set; }
    public required string ProviderEventId { get; set; }   // unique index
    public required string EventType { get; set; }
    public string? EntityKey { get; set; }                  // payment or subscription id
    public required string Payload { get; set; }            // body decoded as UTF-8, NUL escaped (docs/design-notes.md)
    public DateTimeOffset ReceivedAt { get; set; }
    public InboxStatus Status { get; set; }
    public int Attempts { get; set; }                       // failures
    public int NotFoundAttempts { get; set; }               // short retries of "not found", counted apart
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
}

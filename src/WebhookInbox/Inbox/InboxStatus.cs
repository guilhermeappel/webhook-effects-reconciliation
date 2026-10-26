namespace WebhookInbox;

// inbox statuses that separate "returned" from "changed something"
internal enum InboxStatus
{
    Pending,
    Applied,          // the handler changed local state
    NoOp,             // valid event, nothing to change: state already matched, or a rule ignores it
    SkippedNotFound,  // the event refers to something we do not have locally
    Failed,           // exception; retried by the processor with backoff
    DeadLettered      // gave up after MaxAttempts or could not be parsed; alerts
}

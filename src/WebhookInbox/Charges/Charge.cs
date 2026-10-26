namespace WebhookInbox;

internal enum ChargeStatus { Pending, Overdue, Confirmed, Received, ReceivedInCash, Refunded, Cancelled }

internal sealed class Charge
{
    public long Id { get; set; }
    public required string ProviderId { get; set; }
    public ChargeStatus Status { get; set; }
    public bool CancelledByStaff { get; set; }
    public long? PlanId { get; set; }
    public uint Version { get; set; }
}

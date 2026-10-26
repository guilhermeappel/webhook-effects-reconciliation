namespace WebhookInbox.Tests;

/// <summary>
/// A charge of an audited customer, as it stood locally before the deletion events were applied.
/// <paramref name="DueInDays"/> is the due date relative to the day of the audit: negative is days past due. For the
/// deleted charges it is the age the audit found; for the charges that still exist it is illustrative.
/// </summary>
internal sealed record AuditedCharge(string Key, ChargeStatus LocalStatus, bool DeletedAtProvider, int? CaseNumber, int DueInDays);

/// <summary>
/// One audited customer. <see cref="ExpectedBlockedAfterRepair"/> is null when the customer's plan was already
/// suspended or ended (nobody is blocked or released); otherwise it says whether an open overdue charge that
/// still exists at the provider must keep blocking access once the deleted ones are cancelled.
/// </summary>
internal sealed record AuditedCustomer(
    string Label, bool PlanActive, bool? ExpectedBlockedAfterRepair, IReadOnlyList<AuditedCharge> Charges);

/// <summary>
/// The 18 audited cases, as synthetic data shaped by an audit. A case is one charge that the provider had deleted while it was still open
/// locally. The cases belong to nine customers; labels and keys are synthetic, and no amount, calendar date or
/// account identifier is kept (due dates are days relative to the day of the audit). Statuses and groupings are as
/// found in the audit.
/// </summary>
internal static class AuditedCases
{
    private static AuditedCharge Deleted(string key, ChargeStatus status, int caseNumber, int dueInDays) =>
        new(key, status, DeletedAtProvider: true, caseNumber, dueInDays);

    private static AuditedCharge Live(string key, ChargeStatus status, int dueInDays) =>
        new(key, status, DeletedAtProvider: false, CaseNumber: null, dueInDays);

    public static IReadOnlyList<AuditedCustomer> Customers { get; } =
    [
        new("customer-01", PlanActive: false, ExpectedBlockedAfterRepair: null,
        [
            Deleted("c01-a", ChargeStatus.Overdue, 1, -158),
            Deleted("c01-b", ChargeStatus.Overdue, 2, -128)
        ]),
        new("customer-02", PlanActive: false, ExpectedBlockedAfterRepair: null,
        [
            Deleted("c02-a", ChargeStatus.Overdue, 3, -125)
        ]),
        new("customer-03", PlanActive: false, ExpectedBlockedAfterRepair: null,
        [
            Deleted("c03-a", ChargeStatus.Overdue, 4, -78)
        ]),
        // Still blocked after the repair: a different overdue charge that really exists at the provider remains.
        new("customer-04", PlanActive: true, ExpectedBlockedAfterRepair: true,
        [
            Deleted("c04-a", ChargeStatus.Overdue, 5, -62),
            Deleted("c04-b", ChargeStatus.Overdue, 6, -31),
            Live("c04-live-overdue", ChargeStatus.Overdue, -15),
            Live("c04-live-pending", ChargeStatus.Pending, 15)
        ]),
        // Released after the repair: only a charge that is not yet due remains.
        new("customer-05", PlanActive: true, ExpectedBlockedAfterRepair: false,
        [
            Deleted("c05-a", ChargeStatus.Pending, 7, -17),
            Live("c05-live-pending", ChargeStatus.Pending, 13)
        ]),
        new("customer-06", PlanActive: false, ExpectedBlockedAfterRepair: null,
        [
            Deleted("c06-a", ChargeStatus.Overdue, 8, -206),
            Deleted("c06-b", ChargeStatus.Overdue, 9, -175),
            Deleted("c06-c", ChargeStatus.Overdue, 10, -145)
        ]),
        new("customer-07", PlanActive: false, ExpectedBlockedAfterRepair: null,
        [
            Deleted("c07-a", ChargeStatus.Overdue, 11, -212),
            Deleted("c07-b", ChargeStatus.Overdue, 12, -181),
            Deleted("c07-c", ChargeStatus.Pending, 13, -151)
        ]),
        new("customer-08", PlanActive: false, ExpectedBlockedAfterRepair: null,
        [
            Deleted("c08-a", ChargeStatus.Overdue, 14, -78),
            Deleted("c08-b", ChargeStatus.Pending, 16, -47),
            Deleted("c08-c", ChargeStatus.Pending, 18, -16)
        ]),
        new("customer-09", PlanActive: false, ExpectedBlockedAfterRepair: null,
        [
            Deleted("c09-a", ChargeStatus.Pending, 15, -54),
            Deleted("c09-b", ChargeStatus.Overdue, 17, -23)
        ])
    ];

    public static IEnumerable<string> Labels => Customers.Select(c => c.Label);

    public static int[] CaseNumbers => [.. Customers.SelectMany(c => c.Charges).Where(c => c.CaseNumber is not null).Select(c => c.CaseNumber!.Value).Order()];

    public static AuditedCustomer CustomerOf(int caseNumber) =>
        Customers.Single(c => c.Charges.Any(x => x.CaseNumber == caseNumber));

    public static AuditedCustomer Customer(string label) => Customers.Single(c => c.Label == label);

    /// <summary>
    /// An approximation of the platform's access gate, for these tests only: it looks at the open charge with the
    /// earliest due date, and blocks when that charge is overdue or already past its due date even though its status
    /// is still pending. A charge that is not yet due, or cancelled, does not block. The real gate has more detail
    /// (a grace period, for one). What matters is that cancelling a deleted charge must not release a customer who
    /// still owes a charge that exists, and must release one whose only open charge was the deleted one.
    /// </summary>
    public static bool IsBlocked(IEnumerable<(AuditedCharge Fixture, ChargeStatus Status)> charges)
    {
        var first = charges
            .Where(c => c.Status is ChargeStatus.Pending or ChargeStatus.Overdue)
            .OrderBy(c => c.Fixture.DueInDays)
            .Select(c => ((AuditedCharge, ChargeStatus)?)c)
            .FirstOrDefault();
        return first is { } open && (open.Item2 == ChargeStatus.Overdue || open.Item1.DueInDays < 0);
    }
}

using System.Text.RegularExpressions;

namespace WebhookInbox.Tests;

/// <summary>
/// Every combination of 8 local states (the staff-cancelled one apart), 7 payload statuses, the deleted flag on or
/// off, and 4 event types, with the result the production implementation gives (decide-matrix.csv). The file was
/// produced by running the production webhook handler over the same grid, so it holds only states, statuses, event
/// types and results: no names, identifiers or data. The flag and the event type vary independently, so a
/// deletion flag on an update event, or a deletion event without the flag, are rows too. If
/// <see cref="ChargeRules.Decide"/> ever stops agreeing with production in any combination, the row that differs
/// fails by name.
/// </summary>
public partial class DecideMatrixTests
{
    private const string Unchanged = "UNCHANGED";
    private const string Header = "local_status|cancelled_by|payload_status|deleted|event|expected|plan_sync";
    private const string NotModeled = "CHARGEBACK_REQUESTED";

    // The grid the file must cover exactly, one row for each key.
    private static readonly string[] LocalStatuses = ["PENDING", "OVERDUE", "CONFIRMED", "RECEIVED", "RECEIVED_IN_CASH", "REFUNDED", "CANCELLED"];
    private static readonly string[] CancelledBy = ["system", "staff"];
    private static readonly string[] PayloadStatuses = ["PENDING", "OVERDUE", "CONFIRMED", "RECEIVED", "RECEIVED_IN_CASH", "REFUNDED", NotModeled];
    private static readonly string[] Deleted = ["False", "True"];
    private static readonly string[] Events = ["PAYMENT_UPDATED", "PAYMENT_RESTORED", "PAYMENT_DELETED", "PAYMENT_REFUNDED"];
    private static readonly string[] YesNo = ["yes", "no"];
    private static readonly string[] PaidThroughTheGateway = ["CONFIRMED", "RECEIVED"];

    private static readonly string MatrixFile = Path.Combine(AppContext.BaseDirectory, "decide-matrix.csv");

    private sealed record Row(string Local, string By, string Payload, string Deleted, string Event, string Expected, string PlanSync)
    {
        public string Key => string.Join('|', Local, By, Payload, Deleted, Event);
    }

    // Production records the status the provider reports even when this example does not model it.
    private static bool ProductionRecordsAStatusTheExampleDoesNotModel(Row row) => row.Expected == NotModeled;

    private static List<Row> ReadRows()
    {
        var lines = File.ReadAllLines(MatrixFile);
        if (lines.Length == 0 || lines[0].Trim() != Header)
        {
            throw new InvalidDataException($"the header must be exactly '{Header}'");
        }

        var rows = new List<Row>();
        foreach (var line in lines.Skip(1).Where(l => l.Trim().Length > 0))
        {
            var c = line.Split('|').Select(v => v.Trim()).ToArray();
            if (c.Length != 7)
            {
                throw new InvalidDataException($"'{line}' does not have 7 columns");
            }
            var row = new Row(c[0], c[1], c[2], c[3], c[4], c[5], c[6]);
            Require(row, LocalStatuses.Contains(row.Local), "local_status");
            Require(row, CancelledBy.Contains(row.By), "cancelled_by");
            Require(row, PayloadStatuses.Contains(row.Payload), "payload_status");
            Require(row, Deleted.Contains(row.Deleted), "deleted");
            Require(row, Events.Contains(row.Event), "event");
            Require(row, row.Expected == Unchanged || PayloadStatuses.Contains(row.Expected) || LocalStatuses.Contains(row.Expected), "expected");
            Require(row, YesNo.Contains(row.PlanSync), "plan_sync");
            rows.Add(row);
        }
        return rows;
    }

    private static void Require(Row row, bool valid, string column)
    {
        if (!valid)
        {
            throw new InvalidDataException($"row {row.Key}: value out of range in column {column}");
        }
    }

    // The 7 local statuses with "system", plus the staff-cancelled one: 8 local states.
    private static IEnumerable<string> ExpectedKeys() =>
        from local in LocalStatuses
        from who in CancelledBy
        where who == "system" || local == "CANCELLED"
        from payload in PayloadStatuses
        from deleted in Deleted
        from evt in Events
        select string.Join('|', local, who, payload, deleted, evt);

    public static TheoryData<string> Rows
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var row in ReadRows())
            {
                data.Add(string.Join('|', row.Key, row.Expected, row.PlanSync));
            }
            return data;
        }
    }

    [Fact]
    public void Matrix_HasTheExactHeaderValidValuesAndEveryCombinationExactlyOnce()
    {
        var rows = ReadRows();

        var expectedKeys = ExpectedKeys().OrderBy(k => k, StringComparer.Ordinal).ToList();
        var actualKeys = rows.Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedKeys.Count, actualKeys.Count);
        Assert.Equal(expectedKeys, actualKeys);
        Assert.Equal(actualKeys.Count, actualKeys.Distinct().Count());
    }

    [Fact]
    public void Matrix_RowsWhereProductionRecordsAStatusThisExampleDoesNotModel_AreOnlyOnesWithThatPayloadStatus()
    {
        // The example maps six statuses and treats any other as "not modeled" (docs/rule-matrix.md): production
        // stores the new status, the example leaves the charge as it is. The rows are checked here so that the
        // difference cannot grow unnoticed.
        var rows = ReadRows().Where(ProductionRecordsAStatusTheExampleDoesNotModel).ToList();

        // 8 local states x 4 events = 32 rows carry that payload status without the deleted flag; production stores
        // it in 21 of them, and in the other 11 a rule leaves the charge alone first (a refunded or cancelled charge).
        Assert.Equal(21, rows.Count);
        Assert.All(rows, r => Assert.Equal(NotModeled, r.Payload));
        Assert.All(rows, r => Assert.Equal("False", r.Deleted));
    }

    [Fact]
    public void Matrix_RowsWhereProductionReevaluatesAPlanWithoutAChange_AreOnlyRepeatedStatuses()
    {
        // Production re-evaluates the plan when an event repeats the status the charge already has, which can heal a
        // plan whose earlier re-evaluation was lost. The example asks for nothing there (docs/rule-matrix.md). The rows
        // are pinned so that the difference cannot grow unnoticed, as for the unmodeled status above.
        var rows = ReadRows().Where(r => r.Expected == Unchanged && r.PlanSync == "yes").ToList();

        Assert.Equal(28, rows.Count);
        Assert.All(rows, r => Assert.Equal(r.Local, r.Payload));
    }

    [Fact]
    public void Matrix_RepeatedStatusWithoutAPlanReevaluation_IsOnlyASettledChargeWithTheDeletedFlag()
    {
        // 36 rows repeat the status the charge already has and leave it unchanged. Production re-evaluates the plan in
        // 28 of them (above). In the other 8 the event carries the deleted flag on a charge that was paid through the
        // gateway: the deletion is ignored and does not revoke access, so there is nothing to re-evaluate.
        var repeated = ReadRows().Where(r => r.Expected == Unchanged && r.Local == r.Payload).ToList();
        var withoutReevaluation = repeated.Where(r => r.PlanSync == "no").ToList();

        Assert.Equal(36, repeated.Count);
        Assert.Equal(8, withoutReevaluation.Count);
        Assert.All(withoutReevaluation, r => Assert.Equal("True", r.Deleted));
        Assert.All(withoutReevaluation, r => Assert.Contains(r.Local, PaidThroughTheGateway));
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Decide_GivesTheSameResultAsTheProductionRule(string row)
    {
        var c = row.Split('|');
        var (localName, by, payloadName, deleted, eventType, expected, planSync) = (c[0], c[1], c[2], c[3] == "True", c[4], c[5], c[6]);
        var local = new Charge { ProviderId = "pay_1", Status = ParseStatus(localName), CancelledByStaff = by == "staff" };
        var payload = new PaymentPayload("pay_1", payloadName == NotModeled ? null : ParseStatus(payloadName), deleted);

        var transition = ChargeRules.Decide(local, payload, eventType);

        // A status the example does not model is not recorded; everything else must match production exactly.
        var expectedHere = expected == NotModeled ? Unchanged : expected;
        var actual = transition.NewStatus is { } status ? ToProductionName(status) : Unchanged;
        Assert.Equal(expectedHere, actual);

        // The plan is re-evaluated when a charge changes, except when a deletion cancels it (production skips the
        // plan sync there). A charge that does not change asks for nothing here, although production re-evaluates
        // the plan for a repeated status: the known difference pinned by the test above.
        var changed = transition.NewStatus is not null;
        var cancelledByDeletion = changed && transition.NewStatus == ChargeStatus.Cancelled && deleted;
        var expectedEffect = changed && !cancelledByDeletion ? PlanEffect.Reevaluate : PlanEffect.None;
        Assert.Equal(expectedEffect, transition.PlanEffect);
        if (changed && expected != NotModeled)
        {
            Assert.Equal(planSync == "yes", transition.PlanEffect == PlanEffect.Reevaluate);
        }
    }

    private static ChargeStatus ParseStatus(string productionName) =>
        Enum.Parse<ChargeStatus>(productionName.Replace("_", "", StringComparison.Ordinal), ignoreCase: true);

    private static string ToProductionName(ChargeStatus status) =>
        WordBoundary().Replace(status.ToString(), "_$1").ToUpperInvariant();

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex WordBoundary();
}

using Npgsql;

namespace WebhookInbox.Tests;

/// <summary>
/// Runs the two queries of the article exactly as written (sql/attribution.sql and sql/invariants.sql) against
/// scratch tables that have the shape those queries read. The queries are written for the platform's schema; the
/// application's own schema has no provider snapshot and no change timestamp, so the tables are created here.
/// </summary>
[Collection(SharedPostgres.Name)]
public class ReferenceQueriesTests(PostgresFixture postgres)
{
    private static readonly string SqlFolder = Path.Combine(AppContext.BaseDirectory, "sql");

    private const string Schema = """
        CREATE TABLE customers (provider_id text PRIMARY KEY);
        CREATE TABLE charges (provider_id text PRIMARY KEY, status text NOT NULL, last_changed_at timestamptz, created_at timestamptz NOT NULL);
        CREATE TABLE provider_snapshot (provider_id text PRIMARY KEY, customer_id text NOT NULL, status text NOT NULL, deleted boolean, first_read_at timestamptz NOT NULL);
        CREATE TABLE inbox_events (id bigserial PRIMARY KEY, entity_key text, event_type text NOT NULL, status text);
        CREATE TABLE contracts (id bigserial PRIMARY KEY, status text NOT NULL, end_date date);
        """;

    private static readonly DateTimeOffset FirstRead = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private async Task<NpgsqlConnection> OpenWithSchema()
    {
        var connection = new NpgsqlConnection(await postgres.CreateDatabaseAsync());
        await connection.OpenAsync();
        await ExecuteAsync(connection, Schema);
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    // changedBeforeRead is null for a charge whose change timestamp was never filled in.
    private static Task AddCharge(
        NpgsqlConnection connection, string id, string localStatus, string remoteStatus, bool? remoteDeleted,
        TimeSpan? changedBeforeRead, TimeSpan? createdBeforeRead = null) =>
        ExecuteAsync(connection,
            """
            INSERT INTO customers VALUES (@customer) ON CONFLICT DO NOTHING;
            INSERT INTO charges VALUES (@id, @local, @changed, @created);
            INSERT INTO provider_snapshot VALUES (@id, @customer, @remote, @deleted, @read);
            """,
            ("id", id), ("customer", "cus_" + id), ("local", localStatus),
            ("changed", changedBeforeRead is { } changed ? FirstRead - changed : DBNull.Value),
            ("created", FirstRead - (createdBeforeRead ?? TimeSpan.FromDays(30))),
            ("remote", remoteStatus), ("deleted", (object?)remoteDeleted ?? DBNull.Value), ("read", FirstRead));

    // A charge the provider returned and the platform has no record of. The customer is known locally or not.
    private static Task AddRemoteOnly(NpgsqlConnection connection, string id, string remoteStatus, bool? remoteDeleted, bool customerKnown) =>
        ExecuteAsync(connection,
            """
            INSERT INTO customers SELECT @customer WHERE @known;
            INSERT INTO provider_snapshot VALUES (@id, @customer, @remote, @deleted, @read);
            """,
            ("id", id), ("customer", "cus_" + id), ("known", customerKnown),
            ("remote", remoteStatus), ("deleted", (object?)remoteDeleted ?? DBNull.Value), ("read", FirstRead));

    private static Task AddInboxEvent(NpgsqlConnection connection, string entity, string eventType, string? status) =>
        ExecuteAsync(connection, "INSERT INTO inbox_events (entity_key, event_type, status) VALUES (@e, @t, @s)",
            ("e", entity), ("t", eventType), ("s", (object?)status ?? DBNull.Value));

    [Fact]
    public async Task AttributionQuery_PutsEachMismatchInTheLayerWhereTheEffectWasLost()
    {
        await using var connection = await OpenWithSchema();
        var stable = TimeSpan.FromHours(5);
        // Deleted at the provider, still open here, and the inbox never saw the event.
        await AddCharge(connection, "not-delivered", "PENDING", "PENDING", true, stable);
        // Deleted at the provider, and every deletion event we received failed.
        await AddCharge(connection, "failed", "OVERDUE", "OVERDUE", true, stable);
        await AddInboxEvent(connection, "failed", "PAYMENT_DELETED", "FAILED");
        // Deleted at the provider, the deletion event was processed, and the charge is still open.
        await AddCharge(connection, "processed", "PENDING", "PENDING", true, stable);
        await AddInboxEvent(connection, "processed", "PAYMENT_DELETED", "FAILED");
        await AddInboxEvent(connection, "processed", "PAYMENT_DELETED", "PROCESSED");
        // Received at the provider, the received event was processed, and the charge is still open.
        await AddCharge(connection, "received", "PENDING", "RECEIVED", false, stable);
        await AddInboxEvent(connection, "received", "PAYMENT_RECEIVED", "PROCESSED");
        // A status the query does not attribute.
        await AddCharge(connection, "unattributed", "PENDING", "CHARGEBACK_REQUESTED", false, stable);
        // The provider has the charge, the platform has no record of it, and the customer is known here.
        await AddRemoteOnly(connection, "no-local-record", "RECEIVED", false, customerKnown: true);
        await AddInboxEvent(connection, "no-local-record", "PAYMENT_RECEIVED", "PROCESSED");
        // Not counted: a settled charge of a customer this platform has never had (whether it should have the charge
        // is not verified), and a charge with no local record that is not settled at the provider.
        await AddRemoteOnly(connection, "unknown-customer", "RECEIVED", false, customerKnown: false);
        await AddRemoteOnly(connection, "no-local-open", "PENDING", false, customerKnown: true);
        // The change timestamp was never filled in: the creation date stands in for it.
        await AddCharge(connection, "no-change-date", "PENDING", "PENDING", true, changedBeforeRead: null, createdBeforeRead: TimeSpan.FromDays(3));
        await AddCharge(connection, "no-change-date-new", "PENDING", "PENDING", true, changedBeforeRead: null, createdBeforeRead: TimeSpan.FromMinutes(20));
        // A deleted flag that is NULL is "not deleted": the status difference is what counts.
        await AddCharge(connection, "null-flag", "PENDING", "OVERDUE", null, stable);
        await AddInboxEvent(connection, "null-flag", "PAYMENT_OVERDUE", "PROCESSED");
        // An event with no status is not a failure.
        await AddCharge(connection, "null-event-status", "PENDING", "PENDING", true, stable);
        await AddInboxEvent(connection, "null-event-status", "PAYMENT_DELETED", null);
        // The only events are still waiting or were ignored: they are neither a failure nor a processed event.
        await AddCharge(connection, "waiting", "PENDING", "PENDING", true, stable);
        await AddInboxEvent(connection, "waiting", "PAYMENT_DELETED", "PENDING");
        await AddCharge(connection, "ignored", "PENDING", "PENDING", true, stable);
        await AddInboxEvent(connection, "ignored", "PAYMENT_DELETED", "IGNORED");
        // An event of another type does not count for this mismatch.
        await AddCharge(connection, "other-type", "PENDING", "PENDING", true, stable);
        await AddInboxEvent(connection, "other-type", "PAYMENT_OVERDUE", "PROCESSED");
        // Not counted: changed less than an hour before the first read, agreeing with the provider, or deleted
        // at the provider after it was settled here.
        await AddCharge(connection, "too-recent", "PENDING", "PENDING", true, TimeSpan.FromMinutes(30));
        await AddCharge(connection, "agrees", "RECEIVED", "RECEIVED", false, stable);
        await AddCharge(connection, "settled-then-deleted", "RECEIVED", "RECEIVED", true, stable);

        var layers = await ReadLayers(connection);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["not-delivered"] = "1 not delivered (unproven)",
                ["failed"] = "2 failed, never retried",
                ["processed"] = "3 processed without effect",
                ["received"] = "3 processed without effect",
                ["unattributed"] = "unattributed",
                ["no-local-record"] = "unattributed",
                ["no-change-date"] = "1 not delivered (unproven)",
                ["null-flag"] = "3 processed without effect",
                ["null-event-status"] = "unattributed",
                ["waiting"] = "unattributed",
                ["ignored"] = "unattributed",
                ["other-type"] = "1 not delivered (unproven)"
            },
            layers);
    }

    [Fact]
    public async Task AttributionQuery_CountsAChargeOnlyAfterItWasQuietForMoreThanSixtyMinutes()
    {
        // "At least 60 minutes" in the article means strictly more than 60 before the first read.
        await using var connection = await OpenWithSchema();
        await AddCharge(connection, "quiet-59", "PENDING", "PENDING", true, TimeSpan.FromMinutes(59));
        await AddCharge(connection, "quiet-60", "PENDING", "PENDING", true, TimeSpan.FromMinutes(60));
        await AddCharge(connection, "quiet-61", "PENDING", "PENDING", true, TimeSpan.FromMinutes(61));

        var layers = await ReadLayers(connection);

        Assert.Equal(["quiet-61"], layers.Keys);
    }

    [Fact]
    public async Task AttributionQuery_ListsTheLayersInTheOrderOfTheTable()
    {
        await using var connection = await OpenWithSchema();
        var stable = TimeSpan.FromHours(5);
        await AddCharge(connection, "z-unattributed", "PENDING", "CHARGEBACK_REQUESTED", false, stable);
        await AddCharge(connection, "a-processed", "PENDING", "PENDING", true, stable);
        await AddInboxEvent(connection, "a-processed", "PAYMENT_DELETED", "PROCESSED");
        await AddCharge(connection, "m-not-delivered", "PENDING", "PENDING", true, stable);

        var ordered = await ReadLayerRows(connection);

        Assert.Equal(["m-not-delivered", "a-processed", "z-unattributed"], ordered.Select(r => r.ProviderId));
    }

    [Fact]
    public async Task AttributionQuery_AndTheCSharpRule_AgreeOnEveryHistoryOfUpToTwoEvents()
    {
        await using var connection = await OpenWithSchema();
        var stable = TimeSpan.FromHours(5);
        var statuses = Enum.GetValues<InboxStatus>();
        var histories = new List<InboxStatus[]> { Array.Empty<InboxStatus>() };
        histories.AddRange(statuses.Select(s => new[] { s }));
        for (var i = 0; i < statuses.Length; i++)
        {
            for (var j = i; j < statuses.Length; j++)
            {
                histories.Add([statuses[i], statuses[j]]);
            }
        }
        for (var n = 0; n < histories.Count; n++)
        {
            await AddCharge(connection, $"h{n}", "PENDING", "PENDING", true, stable);
            foreach (var status in histories[n])
            {
                await AddInboxEvent(connection, $"h{n}", "PAYMENT_DELETED", OldInboxStatusName(status));
            }
        }
        await AddRemoteOnly(connection, "no-local", "RECEIVED", false, customerKnown: true);
        await AddInboxEvent(connection, "no-local", "PAYMENT_RECEIVED", "PROCESSED");

        var fromSql = await ReadLayers(connection);

        for (var n = 0; n < histories.Count; n++)
        {
            var events = histories[n]
                .Select(s => new InboxEvent { ProviderEventId = "e", EventType = "PAYMENT_DELETED", Payload = "{}", Status = s })
                .ToList();
            var layer = Attribution.Attribute(new ProviderCharge($"h{n}", ChargeStatus.Pending, Deleted: true), events);
            Assert.True(LayerLabel(layer) == fromSql[$"h{n}"],
                $"history [{string.Join(", ", histories[n])}]: SQL says {fromSql[$"h{n}"]}, C# says {LayerLabel(layer)}");
        }
        var noLocal = Attribution.Attribute(new ProviderCharge("no-local", ChargeStatus.Received, Deleted: false), [], hasLocalRecord: false);
        Assert.Equal(LayerLabel(noLocal), fromSql["no-local"]);
    }

    // The statuses of the platform's old inbox, which the SQL reads.
    private static string OldInboxStatusName(InboxStatus status) => status switch
    {
        InboxStatus.Applied or InboxStatus.NoOp or InboxStatus.SkippedNotFound => "PROCESSED",
        InboxStatus.DeadLettered => "FAILED",   // the old inbox's FAILED was never retried
        _ => "PENDING"                          // including Failed, which here still has retries ahead
    };

    private static string LayerLabel(Layer? layer) => layer switch
    {
        Layer.ProviderDelivery => "1 not delivered (unproven)",
        Layer.ProcessingFailed => "2 failed, never retried",
        Layer.ProcessedWithoutEffect => "3 processed without effect",
        _ => "unattributed"
    };

    // The date the invariant query works with: today in Sao Paulo, where the training centers are.
    private static async Task<DateOnly> SaoPauloToday(NpgsqlConnection connection) =>
        (DateOnly)(await ScalarAsync(connection, "SELECT (now() AT TIME ZONE 'America/Sao_Paulo')::date"))!;

    // The date is read once and passed to the inserts. The query reads the clock itself, so a run that straddles
    // midnight (in Sao Paulo) is repeated once instead of failing.
    private async Task<long> CountViolations(string insertContracts, string? sessionTimeZone = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var connection = await OpenWithSchema();
            if (sessionTimeZone is not null)
            {
                await ExecuteAsync(connection, $"SET TIME ZONE '{sessionTimeZone}'");
            }
            var today = await SaoPauloToday(connection);
            await ExecuteAsync(connection, insertContracts, ("today", today));
            var violations = (long)(await ScalarAsync(connection, await File.ReadAllTextAsync(Path.Combine(SqlFolder, "invariants.sql"))))!;
            if (await SaoPauloToday(connection) == today || attempt == 1)
            {
                return violations;
            }
        }
    }

    private const string Contracts = """
        INSERT INTO contracts (status, end_date) VALUES
          ('ACTIVE', @today::date - 10),
          ('ACTIVE', @today::date - 1),
          ('ACTIVE', @today::date),
          ('ACTIVE', @today::date + 30),
          ('CANCELLED', @today::date - 100);
        """;

    [Fact]
    public async Task InvariantsQuery_CountsActiveContractsPastTheirEndDate()
    {
        Assert.Equal(2L, await CountViolations(Contracts));
    }

    [Theory]
    [InlineData("Pacific/Kiritimati")]   // UTC+14: its date is ahead of Sao Paulo's for most of the day
    [InlineData("Etc/GMT+12")]           // UTC-12: its date is behind for the rest of it
    public async Task InvariantsQuery_DoesNotDependOnTheSessionTimeZone(string sessionTimeZone)
    {
        Assert.Equal(2L, await CountViolations(Contracts, sessionTimeZone));
    }

    [Fact]
    public async Task InvariantsQuery_AnActiveContractWithNoEndDateIsAViolation()
    {
        // Nothing would ever end it: the same failure as a contract that ran past its end date unnoticed.
        var violations = await CountViolations(Contracts + "INSERT INTO contracts (status, end_date) VALUES ('ACTIVE', NULL), ('CANCELLED', NULL);");

        Assert.Equal(3L, violations);
    }

    private static async Task<Dictionary<string, string>> ReadLayers(NpgsqlConnection connection) =>
        (await ReadLayerRows(connection)).ToDictionary(r => r.ProviderId, r => r.Layer);

    private static async Task<List<(string ProviderId, string LocalStatus, string RemoteStatus, string Layer)>> ReadLayerRows(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(SqlFolder, "attribution.sql")), connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(string, string, string, string)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        return rows;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}

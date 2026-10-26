using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace WebhookInbox;

// store the raw event, acknowledge, process later
internal static class WebhookEndpoint
{
    private const string LogCategory = "WebhookInbox.Endpoint";
    private const int ReadChunkBytes = 8192;

    public static void MapPaymentWebhook(this WebApplication app) =>
        app.MapPost("/webhooks/payments", async (
            HttpRequest request,
            PaymentsDbContext db,
            InboxSignal signal,
            IOptions<GatewayOptions> options,
            ILoggerFactory loggers,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            if (!HasValidToken(request, options.Value))
            {
                return Results.Unauthorized();
            }

            // The one body this endpoint refuses: it holds a body in memory several times over, and a webhook is small.
            var raw = await ReadBodyAsync(request, options.Value.MaxBodyBytes, ct);
            if (raw is null)
            {
                // Nothing is stored, so this line is the only record that an event was turned away. The size the sender
                // declared is a number the sender chose; none of the body is logged.
                loggers.CreateLogger(LogCategory).LogWarning(
                    "Webhook body refused: declared length {DeclaredLength}, limit {Limit} bytes",
                    request.ContentLength?.ToString(CultureInfo.InvariantCulture) ?? "none", options.Value.MaxBodyBytes);
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var received = Describe(raw, clock.GetUtcNow());
            db.InboxEvents.Add(received);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation())
            {
                return Results.Ok();   // duplicate delivery, already stored
            }
            catch (DbUpdateException ex) when (ex.IsUnstorableData())
            {
                // Something the columns cannot hold (a NUL character in an id, for example). Answering 500 would
                // make the provider resend it and, under sequential delivery, stop the whole queue, so keep a
                // dead letter that holds the body. Any other database error is not about the body: it propagates,
                // the answer is a 500 and the provider resends. The same happens if even this row cannot be stored.
                loggers.CreateLogger(LogCategory).LogError(ex, "Event could not be stored as received");
                db.ChangeTracker.Clear();
                db.InboxEvents.Add(Unstorable(raw, clock.GetUtcNow(), ex));
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException inner) when (inner.IsUniqueViolation())
                {
                    return Results.Ok();
                }
            }

            if (received.Status == InboxStatus.DeadLettered)
            {
                // Dead-lettered on arrival, so the processor never sees it and cannot report it.
                loggers.CreateLogger(LogCategory).LogError(
                    "Event {EventId} stored as a dead letter: the body could not be read", LogText.Safe(received.ProviderEventId));   // wire this log to an alert
            }

            signal.Notify();
            return Results.Ok();       // from here on, retrying is our job
        });

    // Never reject a body we cannot parse: a non-200 would make the provider resend it,
    // and with sequential delivery one bad event would hold up every event behind it.
    private static InboxEvent Describe(string raw, DateTimeOffset receivedAt)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var i) ? ProviderId.Read(i) : null;
            var type = root.TryGetProperty("event", out var e) ? e.GetString() : null;
            if (type is not null)
            {
                // An event without an id is still a real event: key it by its body, so that
                // a redelivery of the same body is still recognized as a duplicate.
                return new InboxEvent
                {
                    ProviderEventId = id ?? "body:" + BodyHash(raw),
                    EventType = type,
                    EntityKey = EntityId(root, "payment") ?? EntityId(root, "subscription"),
                    Payload = Storable(raw),
                    ReceivedAt = receivedAt,
                    Status = InboxStatus.Pending
                };
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // fall through: store it anyway, as dead-lettered
        }

        return new InboxEvent
        {
            ProviderEventId = "unparsed:" + BodyHash(raw),
            EventType = "UNPARSED",
            Payload = Storable(raw),
            ReceivedAt = receivedAt,
            Status = InboxStatus.DeadLettered
        };
    }

    // The row of last resort: a key made from the hash of the body, the reason, and the body itself. The body is
    // kept as received. It needs no Storable: a raw NUL makes the JSON invalid, so such a body never gets here.
    private static InboxEvent Unstorable(string raw, DateTimeOffset receivedAt, DbUpdateException error) => new()
    {
        ProviderEventId = "unstorable:" + BodyHash(raw),
        EventType = "UNSTORABLE",
        Payload = raw,
        ReceivedAt = receivedAt,
        Status = InboxStatus.DeadLettered,
        LastError = "not stored as received: " + ((error.InnerException as PostgresException)?.SqlState ?? error.GetType().Name)
    };

    // PostgreSQL text cannot hold a NUL character; keep the rest of the body exactly as received.
    private static string Storable(string raw) => raw.Replace("\0", "\\u0000", StringComparison.Ordinal);

    private static string BodyHash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static string? EntityId(JsonElement root, string name) =>
        root.TryGetProperty(name, out var obj) && obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty("id", out var id) ? ProviderId.Read(id) : null;

    // The body as text, or null when it is longer than the limit. The declared length is only a shortcut: a chunked
    // body declares none, and a sender can declare less than it sends, so the bytes are counted as they are read.
    private static async Task<string?> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken ct)
    {
        if (request.ContentLength > maxBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[ReadChunkBytes];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8);   // drops a byte order mark, as before
        return await reader.ReadToEndAsync(ct);
    }

    private static bool HasValidToken(HttpRequest request, GatewayOptions gateway)
    {
        // Options validation at startup guarantees the token and its header name are not empty.
        var received = request.Headers[gateway.TokenHeader].ToString();

        // The digests are compared, not the strings: FixedTimeEquals returns at once when the lengths differ,
        // which would tell a caller how long the token is.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(received)), SHA256.HashData(Encoding.UTF8.GetBytes(gateway.WebhookToken)));
    }
}

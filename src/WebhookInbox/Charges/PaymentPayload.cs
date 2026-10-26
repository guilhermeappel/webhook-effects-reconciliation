using System.Text.Json;

namespace WebhookInbox;

// The body is the wrong shape for a payment event. Retrying cannot change that, so the processor
// dead-letters it at once instead of spending its retries.
internal sealed class PayloadFormatException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed record PaymentPayload(string Id, ChargeStatus? Status, bool Deleted)
{
    public static PaymentPayload Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new PayloadFormatException("the payload is not valid JSON", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("payment", out var payment)
                || payment.ValueKind != JsonValueKind.Object)
            {
                throw new PayloadFormatException("the payload has no payment object");
            }

            var id = payment.TryGetProperty("id", out var idElement) ? ProviderId.Read(idElement) : null;
            if (id is null)
            {
                throw new PayloadFormatException("payment.id is missing or empty");
            }

            if (!payment.TryGetProperty("status", out var status)
                || status.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                throw new PayloadFormatException("payment.status is missing or not text");
            }

            // A lone surrogate escape is valid JSON but cannot be read as text: the wrong shape, not a transient error.
            string? statusText;
            try
            {
                statusText = status.GetString();
            }
            catch (InvalidOperationException ex)
            {
                throw new PayloadFormatException("payment.status is not valid text", ex);
            }

            var deleted = payment.TryGetProperty("deleted", out var d) && d.ValueKind == JsonValueKind.True;
            return new PaymentPayload(id, MapStatus(statusText), deleted);
        }
    }

    private static ChargeStatus? MapStatus(string? status) => status switch
    {
        "PENDING" => ChargeStatus.Pending,
        "OVERDUE" => ChargeStatus.Overdue,
        "CONFIRMED" => ChargeStatus.Confirmed,
        "RECEIVED" => ChargeStatus.Received,
        "RECEIVED_IN_CASH" => ChargeStatus.ReceivedInCash,
        "REFUNDED" => ChargeStatus.Refunded,
        _ => null   // statuses this example does not model
    };
}

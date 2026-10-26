using System.Text.Json;

namespace WebhookInbox;

// Provider identifiers are text, but a payload can carry them as a number, or empty.
internal static class ProviderId
{
    // The id as text, or null when it is missing, empty or not something an id can be.
    public static string? Read(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => string.IsNullOrWhiteSpace(element.GetString()) ? null : element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        _ => null
    };
}

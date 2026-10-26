namespace WebhookInbox;

internal sealed class GatewayOptions
{
    public string WebhookToken { get; init; } = "";

    // The header the provider sends the token in. Providers differ, so it is configuration, not code.
    public string TokenHeader { get; init; } = "webhook-token";

    // The largest body the endpoint reads. Webhook bodies are small, and the endpoint holds a body in memory several
    // times over while it stores it, so a larger one is refused (413) instead of read.
    public int MaxBodyBytes { get; init; } = DefaultMaxBodyBytes;

    public const int DefaultMaxBodyBytes = 1024 * 1024;

    // The largest limit that can be set. Above it the limit would stop being the one that applies: Kestrel refuses
    // bodies over 30,000,000 bytes by itself, and a body that size takes hundreds of megabytes here.
    public const int MaxAllowedBodyBytes = 10 * 1024 * 1024;
}

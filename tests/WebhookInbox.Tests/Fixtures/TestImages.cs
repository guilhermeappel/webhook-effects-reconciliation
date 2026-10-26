namespace WebhookInbox.Tests;

/// <summary>The container image the tests start. Keep the tag in sync with docker-compose.yml.</summary>
public static class TestImages
{
    public const string Postgres = "postgres:18.6";
}

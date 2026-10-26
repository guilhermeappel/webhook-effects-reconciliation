namespace WebhookInbox.Tests;

public class AttributionTests
{
    private static InboxEvent Event(string eventType, InboxStatus status) =>
        new() { ProviderEventId = Guid.NewGuid().ToString("N"), EventType = eventType, Payload = "{}", Status = status };

    private static ProviderCharge DeletedAtProvider() => new("pay_1", ChargeStatus.Pending, Deleted: true);

    [Fact]
    public void Attribute_NoEventOfTheExpectedTypeInTheInbox_PointsToProviderDelivery()
    {
        var history = new[] { Event("PAYMENT_CREATED", InboxStatus.Applied) };

        Assert.Equal(Layer.ProviderDelivery, Attribution.Attribute(DeletedAtProvider(), history));
    }

    [Fact]
    public void Attribute_EmptyHistory_PointsToProviderDelivery()
    {
        Assert.Equal(Layer.ProviderDelivery, Attribution.Attribute(DeletedAtProvider(), []));
    }

    [Fact]
    public void Attribute_EveryEventOfTheExpectedTypeFailed_PointsToProcessingFailed()
    {
        var history = new[]
        {
            Event("PAYMENT_DELETED", InboxStatus.DeadLettered),
            Event("PAYMENT_DELETED", InboxStatus.DeadLettered),
            Event("PAYMENT_CREATED", InboxStatus.Applied)
        };

        Assert.Equal(Layer.ProcessingFailed, Attribution.Attribute(DeletedAtProvider(), history));
    }

    [Fact]
    public void Attribute_AnEventOfTheExpectedTypeWasProcessed_PointsToProcessedWithoutEffect()
    {
        var history = new[]
        {
            Event("PAYMENT_DELETED", InboxStatus.Failed),
            Event("PAYMENT_DELETED", InboxStatus.NoOp)
        };

        Assert.Equal(Layer.ProcessedWithoutEffect, Attribution.Attribute(DeletedAtProvider(), history));
    }

    [Theory]
    [InlineData("Received", "PAYMENT_RECEIVED")]
    [InlineData("ReceivedInCash", "PAYMENT_RECEIVED")]
    [InlineData("Confirmed", "PAYMENT_CONFIRMED")]
    [InlineData("Overdue", "PAYMENT_OVERDUE")]
    [InlineData("Refunded", "PAYMENT_REFUNDED")]
    public void Attribute_StatusAtTheProvider_LooksForTheEventThatShouldHaveCarriedIt(string status, string expectedEvent)
    {
        var remote = new ProviderCharge("pay_1", Enum.Parse<ChargeStatus>(status), Deleted: false);

        Assert.Equal(Layer.ProcessedWithoutEffect, Attribution.Attribute(remote, [Event(expectedEvent, InboxStatus.Applied)]));
        Assert.Equal(Layer.ProviderDelivery, Attribution.Attribute(remote, [Event("PAYMENT_UPDATED", InboxStatus.Applied)]));
    }

    [Fact]
    public void Attribute_OnlyWaitingEventsOfTheExpectedType_IsNotAttributed()
    {
        Assert.Null(Attribution.Attribute(DeletedAtProvider(), [Event("PAYMENT_DELETED", InboxStatus.Pending)]));
        Assert.Null(Attribution.Attribute(DeletedAtProvider(),
            [Event("PAYMENT_DELETED", InboxStatus.Failed), Event("PAYMENT_DELETED", InboxStatus.Pending)]));
    }

    [Fact]
    public void Attribute_DeadLetteredEventsOfTheExpectedType_CountAsFailed()
    {
        Assert.Equal(Layer.ProcessingFailed,
            Attribution.Attribute(DeletedAtProvider(), [Event("PAYMENT_DELETED", InboxStatus.DeadLettered)]));
    }

    [Fact]
    public void Attribute_DeadLettersMixedWithWaitingEventsOfTheExpectedType_IsNotAttributed()
    {
        // Layer 2 needs every expected-type event to have failed for good; one still waiting keeps the case open.
        Assert.Null(Attribution.Attribute(DeletedAtProvider(),
            [Event("PAYMENT_DELETED", InboxStatus.DeadLettered), Event("PAYMENT_DELETED", InboxStatus.Pending)]));
        Assert.Null(Attribution.Attribute(DeletedAtProvider(),
            [Event("PAYMENT_DELETED", InboxStatus.DeadLettered), Event("PAYMENT_DELETED", InboxStatus.Failed)]));
    }

    [Fact]
    public void Attribute_AFailedEventThatTheProcessorWillStillRetry_IsNotAttributed()
    {
        // In this design Failed means "will be retried"; only a dead letter is "failed and never retried".
        Assert.Null(Attribution.Attribute(DeletedAtProvider(), [Event("PAYMENT_DELETED", InboxStatus.Failed)]));
    }

    [Fact]
    public void Attribute_NoLocalRecord_IsNotAttributedWhateverTheHistory()
    {
        Assert.Null(Attribution.Attribute(DeletedAtProvider(), [Event("PAYMENT_DELETED", InboxStatus.Applied)], hasLocalRecord: false));
        Assert.Null(Attribution.Attribute(DeletedAtProvider(), [], hasLocalRecord: false));
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Cancelled")]
    public void Attribute_StatusNotAttributed_ReturnsNullWhateverTheHistory(string status)
    {
        var remote = new ProviderCharge("pay_1", Enum.Parse<ChargeStatus>(status), Deleted: false);

        Assert.Null(Attribution.Attribute(remote, []));
        Assert.Null(Attribution.Attribute(remote, [Event("PAYMENT_DELETED", InboxStatus.Applied)]));
    }
}

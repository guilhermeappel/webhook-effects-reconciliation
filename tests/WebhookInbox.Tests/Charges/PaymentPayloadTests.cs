namespace WebhookInbox.Tests;

public class PaymentPayloadTests
{
    [Fact]
    public void Parse_NumericPaymentId_IsReadAsItsText()
    {
        var payload = PaymentPayload.Parse("""{"event":"PAYMENT_UPDATED","payment":{"id":5,"status":"OVERDUE"}}""");

        Assert.Equal("5", payload.Id);
    }

    [Theory]
    [InlineData("PENDING", "Pending")]
    [InlineData("OVERDUE", "Overdue")]
    [InlineData("CONFIRMED", "Confirmed")]
    [InlineData("RECEIVED", "Received")]
    [InlineData("RECEIVED_IN_CASH", "ReceivedInCash")]
    [InlineData("REFUNDED", "Refunded")]
    public void Parse_EachStatusTheExampleModels_MapsToItsOwnChargeStatus(string providerStatus, string expected)
    {
        // The rules and the matrix are tested with charge statuses given directly, so this is the only test that would
        // notice two provider statuses being swapped on the way in. (The names are text because the enum is internal.)
        var payload = PaymentPayload.Parse($$$"""{"payment":{"id":"pay_1","status":"{{{providerStatus}}}"}}""");

        Assert.Equal(expected, payload.Status.ToString());
    }

    [Theory]
    [InlineData("""{"payment":{"id":"pay_1","status":"CHARGEBACK_REQUESTED"}}""")]
    [InlineData("""{"payment":{"id":"pay_1","status":"received"}}""")]   // the names are matched exactly
    [InlineData("""{"payment":{"id":"pay_1","status":null}}""")]
    public void Parse_StatusTheExampleDoesNotModel_HasNoChargeStatus(string body)
    {
        Assert.Null(PaymentPayload.Parse(body).Status);
    }

    [Theory]
    [InlineData("""{"payment":{"id":"pay_1","status":"OVERDUE","deleted":true}}""", true)]
    [InlineData("""{"payment":{"id":"pay_1","status":"OVERDUE","deleted":false}}""", false)]
    [InlineData("""{"payment":{"id":"pay_1","status":"OVERDUE"}}""", false)]
    [InlineData("""{"payment":{"id":"pay_1","status":"OVERDUE","deleted":"true"}}""", false)]   // only the JSON true counts
    public void Parse_DeletedFlag_IsTrueOnlyForTheJsonTrue(string body, bool expected)
    {
        Assert.Equal(expected, PaymentPayload.Parse(body).Deleted);
    }

    [Fact]
    public void Parse_StatusThatIsNotValidText_IsTheWrongShapeNotATransientError()
    {
        // JSON accepts a lone UTF-16 surrogate escape, but reading it as text throws. Retrying cannot fix the body, so
        // it has to be reported as the wrong shape (dead-lettered at once) and not as an error worth eight retries.
        var body = """{"payment":{"id":"pay_1","status":"\ud800"}}""";

        Assert.Throws<PayloadFormatException>(() => PaymentPayload.Parse(body));
    }
}

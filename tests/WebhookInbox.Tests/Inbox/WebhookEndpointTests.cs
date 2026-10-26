using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace WebhookInbox.Tests;

[Collection(SharedPostgres.Name)]
public class WebhookEndpointTests(PostgresFixture postgres)
{
    private const string Token = "test-token";
    private const string TokenHeader = "webhook-token";
    private const string Route = "/webhooks/payments";

    private sealed class Factory(
        string connectionString, string token, bool keepProcessor, TimeProvider? clock = null, string? tokenHeader = null,
        ListLoggerProvider? logs = null, DbCommandInterceptor? interceptor = null, string? maxBodyBytes = null,
        bool stripContentLength = false, long? declaredContentLength = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            if (logs is not null)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(logs));
            }
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Payments"] = connectionString,
                    ["Gateway:WebhookToken"] = token
                };
                if (tokenHeader is not null)
                {
                    settings["Gateway:TokenHeader"] = tokenHeader;   // otherwise the default header applies
                }
                if (maxBodyBytes is not null)
                {
                    settings["Gateway:MaxBodyBytes"] = maxBodyBytes;   // otherwise the default limit applies
                }
                configuration.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services =>
            {
                if (clock is not null)
                {
                    services.AddSingleton(clock);
                }

                if (interceptor is not null)
                {
                    services.ConfigureDbContext<PaymentsDbContext>(o => o.AddInterceptors(interceptor));
                }

                if (stripContentLength)
                {
                    services.AddTransient<IStartupFilter, StripContentLength>();
                }

                if (declaredContentLength is { } declared)
                {
                    services.AddTransient<IStartupFilter>(_ => new DeclareContentLength(declared));
                }

                // The endpoint tests look at what was stored, so the processor must not touch the rows.
                if (!keepProcessor)
                {
                    foreach (var processor in services.Where(d => d.ImplementationType == typeof(InboxProcessor)).ToList())
                    {
                        services.Remove(processor);
                    }
                }
            });
        }
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string body, string? token = Token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = Json(body) };
        if (token is not null)
        {
            request.Headers.Add(TokenHeader, token);
        }
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Post_DuplicateDelivery_RespondsOkWithoutStoringASecondRow()
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false);
        using var client = factory.CreateClient();
        var body = TestDatabase.PaymentBody("evt-1", "PAYMENT_UPDATED", "pay_1", "PENDING");

        var first = await PostAsync(client, body);
        var second = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        await using var db = TestDatabase.Open(connectionString);
        Assert.Equal(1, await db.InboxEvents.CountAsync());
    }

    [Fact]
    public async Task Post_ValidEvent_IsStoredPendingWithItsEntityKey()
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false);
        using var client = factory.CreateClient();

        var response = await PostAsync(client, TestDatabase.PaymentBody("evt-1", "PAYMENT_DELETED", "pay_9", "PENDING", deleted: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await TestDatabase.ReadEventAsync(connectionString, "evt-1");
        Assert.Equal(InboxStatus.Pending, stored.Status);
        Assert.Equal("PAYMENT_DELETED", stored.EventType);
        Assert.Equal("pay_9", stored.EntityKey);
    }

    [Fact]
    public async Task Post_EventWithoutAnId_IsStoredPendingUnderAKeyDerivedFromItsBody()
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false);
        using var client = factory.CreateClient();
        const string Body = """{"event":"PAYMENT_UPDATED","payment":{"id":"pay_7","status":"OVERDUE"}}""";

        var response = await PostAsync(client, Body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = TestDatabase.Open(connectionString);
        var stored = await db.InboxEvents.AsNoTracking().SingleAsync();
        Assert.Equal(InboxStatus.Pending, stored.Status);
        Assert.Equal("PAYMENT_UPDATED", stored.EventType);
        Assert.Equal("pay_7", stored.EntityKey);
        Assert.StartsWith("body:", stored.ProviderEventId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_SameEventWithoutAnIdTwice_IsStoredOnce()
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false);
        using var client = factory.CreateClient();
        const string Body = """{"event":"PAYMENT_UPDATED","payment":{"id":"pay_7","status":"OVERDUE"}}""";

        await PostAsync(client, Body);
        var second = await PostAsync(client, Body);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        await using var db = TestDatabase.Open(connectionString);
        Assert.Equal(1, await db.InboxEvents.CountAsync());
    }

    [Fact]
    public async Task Post_TwoDifferentEventsWithoutAnId_AreBothStored()
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false);
        using var client = factory.CreateClient();

        await PostAsync(client, """{"event":"PAYMENT_UPDATED","payment":{"id":"pay_7","status":"OVERDUE"}}""");
        await PostAsync(client, """{"event":"PAYMENT_UPDATED","payment":{"id":"pay_7","status":"RECEIVED"}}""");

        await using var db = TestDatabase.Open(connectionString);
        Assert.Equal(2, await db.InboxEvents.CountAsync());
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("""{"payment":{"id":"pay_7","status":"OVERDUE"}}""")]
    [InlineData("""{"id":"evt-1"}""")]
    [InlineData("[]")]
    public async Task Post_BodyThatCannotBeUsed_IsStoredDeadLetteredAndStillAnswersOk(string body)
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false);
        using var client = factory.CreateClient();

        var response = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = TestDatabase.Open(connectionString);
        var stored = await db.InboxEvents.AsNoTracking().SingleAsync();
        Assert.Equal(InboxStatus.DeadLettered, stored.Status);
        Assert.Equal("UNPARSED", stored.EventType);
        Assert.StartsWith("unparsed:", stored.ProviderEventId, StringComparison.Ordinal);
        Assert.Equal(body, stored.Payload);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("""{"payment":{"id":"pay_7","status":"OVERDUE"}}""")]
    [InlineData("[]")]
    public async Task Post_BodyThatCannotBeUsed_IsLoggedAsAnErrorSoThatAnAlertCanFire(string body)
    {
        // The dead letter is written here, before the processor ever sees it, so this is the only place that can report it.
        var connectionString = await TestDatabase.CreateAsync(postgres);
        var logs = new ListLoggerProvider();
        await using var factory = new Factory(connectionString, Token, keepProcessor: false, logs: logs);
        using var client = factory.CreateClient();

        await PostAsync(client, body);

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("dead letter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Post_DuplicateDelivery_AnswersOkAndLogsNoError()
    {
        // A redelivery is normal for a webhook. EF Core would log the unique-constraint violation twice as errors, with
        // a stack trace each (about 9 KB), and a caller could use that to fill the log: those categories are off.
        var connectionString = await TestDatabase.CreateAsync(postgres);
        var logs = new ListLoggerProvider();
        await using var factory = new Factory(connectionString, Token, keepProcessor: false, logs: logs);
        using var client = factory.CreateClient();
        var body = TestDatabase.PaymentBody("evt-1", "PAYMENT_UPDATED", "pay_1", "PENDING");

        var first = await PostAsync(client, body);
        var second = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Post_ValidEvent_LogsNoError()
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        var logs = new ListLoggerProvider();
        await using var factory = new Factory(connectionString, Token, keepProcessor: false, logs: logs);
        using var client = factory.CreateClient();

        await PostAsync(client, TestDatabase.PaymentBody("evt-1", "PAYMENT_UPDATED", "pay_1", "PENDING"));

        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Post_SameUnparseableBodyTwice_IsStoredOnce()
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false);
        using var client = factory.CreateClient();

        await PostAsync(client, "garbage");
        var second = await PostAsync(client, "garbage");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        await using var db = TestDatabase.Open(connectionString);
        Assert.Equal(1, await db.InboxEvents.CountAsync());
    }

    [Fact]
    public async Task Post_EventWithANumericId_IsStoredPendingUnderThatId()
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false);
        using var client = factory.CreateClient();

        await PostAsync(client, """{"id":123,"event":"PAYMENT_UPDATED","payment":{"id":"pay_1","status":"OVERDUE"}}""");

        await using var db = TestDatabase.Open(connectionString);
        var stored = await db.InboxEvents.AsNoTracking().SingleAsync();
        Assert.Equal(InboxStatus.Pending, stored.Status);
        Assert.Equal("123", stored.ProviderEventId);
        Assert.Equal("pay_1", stored.EntityKey);
    }

    [Fact]
    public async Task Post_EventWhoseEntityHasANumericId_IsKeyedByThatId()
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false);
        using var client = factory.CreateClient();

        await PostAsync(client, """{"id":"evt-1","event":"PAYMENT_UPDATED","payment":{"id":5,"status":"OVERDUE"}}""");

        await using var db = TestDatabase.Open(connectionString);
        var stored = await db.InboxEvents.AsNoTracking().SingleAsync();
        Assert.Equal(InboxStatus.Pending, stored.Status);
        Assert.Equal("5", stored.EntityKey);
    }

    [Fact]
    public async Task Post_TwoDifferentEventsWithAnEmptyId_AreNotTakenForDuplicates()
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false);
        using var client = factory.CreateClient();

        await PostAsync(client, """{"id":"","event":"PAYMENT_UPDATED","payment":{"id":"pay_7","status":"OVERDUE"}}""");
        await PostAsync(client, """{"id":"","event":"PAYMENT_UPDATED","payment":{"id":"pay_7","status":"RECEIVED"}}""");

        await using var db = TestDatabase.Open(connectionString);
        var stored = await db.InboxEvents.AsNoTracking().ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, e => Assert.StartsWith("body:", e.ProviderEventId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Post_EventWithAnEmptyEntityId_HasNoEntityKey()
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false);
        using var client = factory.CreateClient();

        await PostAsync(client, """{"id":"evt-1","event":"PAYMENT_UPDATED","payment":{"id":"","status":"OVERDUE"}}""");

        await using var db = TestDatabase.Open(connectionString);
        Assert.Null((await db.InboxEvents.AsNoTracking().SingleAsync()).EntityKey);
    }

    [Fact]
    public async Task Post_BodyWithARawNulByte_IsStoredDeadLetteredAndStillAnswersOk()
    {
        // PostgreSQL text cannot hold 0x00: storing the body as received would fail, answer 500 and, under
        // sequential delivery, stop the provider's whole queue.
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false);
        using var client = factory.CreateClient();

        var response = await PostAsync(client, "{\"event\":\"PAYMENT_UPDATED\",\"payment\":{\"id\":\"pay_\u0000\"}}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = TestDatabase.Open(connectionString);
        var stored = await db.InboxEvents.AsNoTracking().SingleAsync();
        Assert.Equal(InboxStatus.DeadLettered, stored.Status);
        Assert.DoesNotContain('\0', stored.Payload);
        Assert.Contains("\\u0000", stored.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_ValidJsonWhoseIdCarriesANulCharacter_IsStoredAsAnUnstorableDeadLetterWithItsBodyAndAnswersOk()
    {
        // Valid JSON, so the body is kept as received; but the id it yields cannot be stored in a text column.
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false);
        using var client = factory.CreateClient();
        const string Body = """{"id":"evt\u0000x","event":"PAYMENT_UPDATED","payment":{"id":"pay_1","status":"OVERDUE"}}""";

        var response = await PostAsync(client, Body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = TestDatabase.Open(connectionString);
        var stored = await db.InboxEvents.AsNoTracking().SingleAsync();
        Assert.Equal(InboxStatus.DeadLettered, stored.Status);
        Assert.StartsWith("unstorable:", stored.ProviderEventId, StringComparison.Ordinal);
        Assert.Equal(Body, stored.Payload);   // nothing in the body itself is unstorable, so it is kept for a person to read
        Assert.NotNull(stored.LastError);
    }

    [Fact]
    public async Task Post_ValidJsonWhoseIdIsTooLongToIndex_KeepsTheBodyInTheUnstorableDeadLetter()
    {
        // About 24,000 characters that do not compress, so the unique index on the id cannot take the entry
        // however PostgreSQL stores the column. The body itself is ordinary text.
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false);
        using var client = factory.CreateClient();
        var longId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18_000));
        var body = $$$"""{"id":"{{{longId}}}","event":"PAYMENT_UPDATED","payment":{"id":"pay_1","status":"OVERDUE"}}""";

        var response = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = TestDatabase.Open(connectionString);
        var stored = await db.InboxEvents.AsNoTracking().SingleAsync();
        Assert.StartsWith("unstorable:", stored.ProviderEventId, StringComparison.Ordinal);
        Assert.Equal(body, stored.Payload);
    }

    [Fact]
    public async Task Post_WhenTheDatabaseFailsForAReasonThatIsNotTheData_AnswersAnErrorSoTheProviderResends()
    {
        // A statement timeout says nothing about the body. Turning it into a dead letter and answering 200 would
        // lose a valid event for good, because the provider does not resend after a 200.
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(
            connectionString, Token, keepProcessor: false, interceptor: new FailTheFirstInsert(PostgresErrorCodes.QueryCanceled));
        using var client = factory.CreateClient();

        var status = await StatusOfAsync(client, TestDatabase.PaymentBody("evt-1", "PAYMENT_UPDATED", "pay_1", "PENDING"));

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        await using var db = TestDatabase.Open(connectionString);
        Assert.Equal(0, await db.InboxEvents.CountAsync());
    }

    // The test server hands an exception the application does not handle to the caller; a real server answers 500.
    private static async Task<HttpStatusCode> StatusOfAsync(HttpClient client, string body)
    {
        try
        {
            using var response = await PostAsync(client, body);
            return response.StatusCode;
        }
        catch (DbUpdateException)
        {
            return HttpStatusCode.InternalServerError;
        }
    }

    private sealed class FailTheFirstInsert(string sqlState) : DbCommandInterceptor
    {
        private int _inserts;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.StartsWith("INSERT", StringComparison.Ordinal) && Interlocked.Increment(ref _inserts) == 1)
            {
                throw new PostgresException("injected failure", "ERROR", "ERROR", sqlState);
            }

            return base.ReaderExecutingAsync(command, eventData, result, ct);
        }
    }

    [Fact]
    public async Task Post_StoredEvent_WakesTheProcessorAtOnce()
    {
        // Without the signal the processor only looks again when its 30-second wait ends.
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false);
        using var client = factory.CreateClient();
        var signal = factory.Services.GetRequiredService<InboxSignal>();
        var started = Stopwatch.StartNew();

        await PostAsync(client, TestDatabase.PaymentBody("evt-1", "PAYMENT_UPDATED", "pay_1", "PENDING"));
        await signal.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"The processor was not signalled; it waited {started.Elapsed}.");
    }

    [Fact]
    public async Task Post_SubscriptionEvent_IsKeyedBySubscriptionId()
    {
        // The key is what keeps the events of one subscription in order behind each other.
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false);
        using var client = factory.CreateClient();

        await PostAsync(client, """{"id":"evt-s","event":"SUBSCRIPTION_UPDATED","subscription":{"id":"sub_1","status":"ACTIVE"}}""");

        await using var db = TestDatabase.Open(connectionString);
        var stored = await db.InboxEvents.AsNoTracking().SingleAsync();
        Assert.Equal("sub_1", stored.EntityKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    [InlineData("test-tokeN")]       // same length as the real token, one character off
    [InlineData("test-token-extra")] // starts with the real token
    public async Task Post_WithoutAValidToken_IsRejectedAndStoresNothing(string? token)
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false);
        using var client = factory.CreateClient();

        var response = await PostAsync(client, TestDatabase.PaymentBody("evt-1", "PAYMENT_UPDATED", "pay_1", "PENDING"), token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var db = TestDatabase.Open(connectionString);
        Assert.Equal(0, await db.InboxEvents.CountAsync());
    }

    [Fact]
    public async Task Post_StoresTheReceiptTimeFromTheClockItIsGiven()
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        await using var factory = new Factory(connectionString, Token, keepProcessor: false, clock);
        using var client = factory.CreateClient();

        await PostAsync(client, TestDatabase.PaymentBody("evt-1", "PAYMENT_UPDATED", "pay_1", "PENDING"));

        Assert.Equal(clock.GetUtcNow(), (await TestDatabase.ReadEventAsync(connectionString, "evt-1")).ReceivedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Startup_EmptyConnectionString_FailsWithAClearMessage(string connectionString)
    {
        await using var factory = new Factory(connectionString, Token, keepProcessor: true);

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("ConnectionStrings:Payments is required", Describe(failure), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Startup_EmptyWebhookTokenInConfiguration_FailsInsteadOfAcceptingEveryRequest(string token)
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, token, keepProcessor: true);

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Gateway:WebhookToken is required", Describe(failure), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_TokenHeaderFromConfiguration_IsTheOneThatIsChecked()
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false, tokenHeader: "x-provider-token");
        using var client = factory.CreateClient();
        var body = TestDatabase.PaymentBody("evt-1", "PAYMENT_UPDATED", "pay_1", "PENDING");

        using var withConfiguredHeader = new HttpRequestMessage(HttpMethod.Post, Route) { Content = Json(body) };
        withConfiguredHeader.Headers.Add("x-provider-token", Token);
        var accepted = await client.SendAsync(withConfiguredHeader);
        var withDefaultHeader = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, withDefaultHeader.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Startup_EmptyTokenHeaderInConfiguration_Fails(string tokenHeader)
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: true, tokenHeader: tokenHeader);

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Gateway:TokenHeader is required", Describe(failure), StringComparison.Ordinal);
    }

    // Webhook bodies are small. A limit is the one deliberate exception to "never reject a body": the endpoint holds the
    // whole body in memory, and an event this large is not one the provider sends.
    private const int BodyLimit = 400;
    private static readonly string BodyLimitSetting = BodyLimit.ToString(CultureInfo.InvariantCulture);

    private static string BodyOfExactly(int bytes)
    {
        const string Frame = """{"id":"evt-1","event":"PAYMENT_UPDATED","payment":{"id":"pay_1","status":"PENDING"},"pad":""}""";
        return Frame.Replace("\"pad\":\"\"", "\"pad\":\"" + new string('x', bytes - Encoding.UTF8.GetByteCount(Frame)) + "\"", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_BodyLargerThanTheLimit_AnswersPayloadTooLargeAndStoresNothing()
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false, maxBodyBytes: BodyLimitSetting);
        using var client = factory.CreateClient();

        var response = await PostAsync(client, BodyOfExactly(BodyLimit + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await using var db = TestDatabase.Open(connectionString);
        Assert.Equal(0, await db.InboxEvents.CountAsync());
    }

    [Fact]
    public async Task Post_BodyExactlyAtTheLimit_IsStored()
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false, maxBodyBytes: BodyLimitSetting);
        using var client = factory.CreateClient();

        var response = await PostAsync(client, BodyOfExactly(BodyLimit));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = TestDatabase.Open(connectionString);
        Assert.Equal(BodyLimit, Encoding.UTF8.GetByteCount((await db.InboxEvents.AsNoTracking().SingleAsync()).Payload));
    }

    [Fact]
    public async Task Post_LargeBodyWithoutADeclaredLength_AnswersPayloadTooLargeToo()
    {
        // The length a sender declares is only a shortcut: a chunked body declares none, and a sender can send more
        // than it declared. The test server always declares one, so the filter takes it off before the endpoint.
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(
            connectionString, Token, keepProcessor: false, maxBodyBytes: BodyLimitSetting, stripContentLength: true);
        using var client = factory.CreateClient();

        var response = await PostAsync(client, BodyOfExactly(BodyLimit * 10));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await using var db = TestDatabase.Open(connectionString);
        Assert.Equal(0, await db.InboxEvents.CountAsync());
    }

    [Fact]
    public async Task Post_BodyOneByteOverTheLimitWithoutADeclaredLength_AnswersPayloadTooLarge()
    {
        // The count of bytes read, not the declared length, decides at the boundary.
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(
            connectionString, Token, keepProcessor: false, maxBodyBytes: BodyLimitSetting, stripContentLength: true);
        using var client = factory.CreateClient();

        var response = await PostAsync(client, BodyOfExactly(BodyLimit + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Post_BodyAtTheLimitWithoutADeclaredLength_IsStored()
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(
            connectionString, Token, keepProcessor: false, maxBodyBytes: BodyLimitSetting, stripContentLength: true);
        using var client = factory.CreateClient();

        var response = await PostAsync(client, BodyOfExactly(BodyLimit));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = TestDatabase.Open(connectionString);
        Assert.Equal(1, await db.InboxEvents.CountAsync());
    }

    // Removes the declared length of the request before the endpoint sees it, as for a chunked body.
    private sealed class StripContentLength : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, pipeline) =>
            {
                context.Request.ContentLength = null;
                return pipeline(context);
            });
            next(app);
        };
    }

    [Fact]
    public async Task Post_DeclaredLengthOverTheLimit_AnswersPayloadTooLargeWithoutReadingTheBody()
    {
        // The shortcut: a sender that says it is sending too much is turned away before any of it is read. The body
        // here is small, so only the declared length can be what is refused.
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(
            connectionString, Token, keepProcessor: false, maxBodyBytes: BodyLimitSetting, declaredContentLength: BodyLimit + 1);
        using var client = factory.CreateClient();

        var response = await PostAsync(client, BodyOfExactly(BodyLimit - 100));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await using var db = TestDatabase.Open(connectionString);
        Assert.Equal(0, await db.InboxEvents.CountAsync());
    }

    [Fact]
    public async Task Post_DeclaredLengthExactlyAtTheLimit_IsStored()
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(
            connectionString, Token, keepProcessor: false, maxBodyBytes: BodyLimitSetting, declaredContentLength: BodyLimit);
        using var client = factory.CreateClient();

        var response = await PostAsync(client, BodyOfExactly(BodyLimit - 100));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Replaces the declared length of the request before the endpoint sees it, as for a sender that declares more
    // than it sends.
    private sealed class DeclareContentLength(long length) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, pipeline) =>
            {
                context.Request.ContentLength = length;
                return pipeline(context);
            });
            next(app);
        };
    }

    [Fact]
    public void Options_DefaultBodyLimit_IsOneMebibyte()
    {
        Assert.Equal(1_048_576, new GatewayOptions().MaxBodyBytes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("10485761")]   // one over the largest limit that can be set
    [InlineData("2147483647")] // beyond that, Kestrel's own 30 MB limit would be the one that applies
    public async Task Startup_BodyLimitOutsideTheAllowedRange_Fails(string limit)
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: true, maxBodyBytes: limit);

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Gateway:MaxBodyBytes must be between 1 and 10485760", Describe(failure), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_SmallestAllowedBodyLimit_StartsTheService()
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false, maxBodyBytes: "1");

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    [Fact]
    public async Task Startup_LargestAllowedBodyLimit_StartsTheService()
    {
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, Token, keepProcessor: false, maxBodyBytes: "10485760");

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    [Theory]
    [InlineData("secret\n")]   // what a secret read from a file usually ends with
    [InlineData(" secret")]
    public async Task Startup_WebhookTokenWithWhitespaceAtAnEnd_FailsInsteadOfRefusingEveryRequest(string token)
    {
        // The header value reaches the endpoint trimmed, so a configured token with a trailing newline would never match
        // and every delivery would be refused with a 401 that nothing logs.
        var connectionString = await TestDatabase.CreateAsync(postgres);
        await using var factory = new Factory(connectionString, token, keepProcessor: true);

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Gateway:WebhookToken must not start or end with whitespace", Describe(failure), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_BodyOverTheLimit_IsLoggedAsAWarningWithoutItsContent()
    {
        // The one request the endpoint refuses after authentication would otherwise leave no line anywhere.
        var connectionString = await TestDatabase.CreateAsync(postgres);
        var logs = new ListLoggerProvider();
        await using var factory = new Factory(connectionString, Token, keepProcessor: false, logs: logs, maxBodyBytes: BodyLimitSetting);
        using var client = factory.CreateClient();

        await PostAsync(client, BodyOfExactly(BodyLimit + 1));

        var warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("refused", StringComparison.Ordinal));
        Assert.Contains($"limit {BodyLimitSetting} bytes", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("xxxx", warning.Message, StringComparison.Ordinal);
    }

    private static string Describe(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }
        return string.Join(" | ", messages);
    }
}

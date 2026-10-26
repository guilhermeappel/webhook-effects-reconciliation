using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace WebhookInbox.Tests;

[Collection(SharedPostgres.Name)]
public class InboxProcessorTests(PostgresFixture postgres)
{
    // Mirrors the private constants of InboxProcessor; the behavior under test is what they cause.
    private const int MaxAttempts = 8;
    private const int NotFoundAttempts = 3;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(60);
    private const string Entity = "pay_1";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NothingShouldHappen = TimeSpan.FromSeconds(1);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly InboxProcessor _processor;

        public Harness(
            string connectionString, IServiceScopeFactory? scopes = null, ILogger<InboxProcessor>? processorLogger = null,
            ISubscriptionEventHandler? subscriptions = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ILogger<EventDispatcher>>(DispatcherLogger);
            services.AddSingleton<ILogger<PaymentEventHandler>>(HandlerLogger);
            services.AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(connectionString));
            services.AddScoped<PaymentEventHandler>();
            services.AddSingleton(subscriptions ?? new AlwaysFailingSubscriptionHandler());
            services.AddScoped<EventDispatcher>();
            services.AddScoped<IPlanService, RecordingPlanService>();
            _services = services.BuildServiceProvider();

            Logger = new ListLogger<InboxProcessor>();
            _processor = new InboxProcessor(
                scopes ?? _services.GetRequiredService<IServiceScopeFactory>(), Signal, Clock, processorLogger ?? Logger);
        }

        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

        public InboxSignal Signal { get; } = new();

        public ListLogger<InboxProcessor> Logger { get; }

        public ListLogger<EventDispatcher> DispatcherLogger { get; } = new();

        public ListLogger<PaymentEventHandler> HandlerLogger { get; } = new();

        public Task? Running => _processor.ExecuteTask;

        public Task StartAsync() => _processor.StartAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await _processor.StopAsync(CancellationToken.None);
            await _services.DisposeAsync();
        }
    }

    // A subscription event: the harness's subscription handler always throws a transient error, so the event
    // goes to the retry path. (A body that is the wrong shape is dead-lettered at once instead.)
    private const string BodyThatAlwaysFails = """{"id":"evt","event":"SUBSCRIPTION_UPDATED"}""";
    private const string EventTypeThatAlwaysFails = "SUBSCRIPTION_UPDATED";

    private sealed class AlwaysFailingSubscriptionHandler : ISubscriptionEventHandler
    {
        public Task<InboxStatus> HandleAsync(InboxEvent evt, CancellationToken ct) =>
            throw new InvalidOperationException("the subscription system is unavailable");
    }

    // An id as an attacker could send it: a line break that starts a forged line, a terminal escape, and a right-to-left
    // override. Logged with LogText.Safe it becomes the text in HostileIdAsLogged.
    private const string HostileId = "evt\nfail: Forged[0]\u001b[31m\u202e";
    private const string HostileIdAsLogged = "evt?fail: Forged[0]?[31m?";

    private static void AssertNothingCouldForgeALine(IEnumerable<string> messages)
    {
        foreach (var message in messages)
        {
            Assert.DoesNotContain('\n', message);
            Assert.DoesNotContain('\u001b', message);
            Assert.DoesNotContain('\u202e', message);
        }
    }

    private async Task<string> NewDatabase() => await TestDatabase.CreateAsync(postgres);

    private static async Task<InboxEvent> Read(string connectionString, string eventId) =>
        await TestDatabase.ReadEventAsync(connectionString, eventId);

    private static async Task<InboxStatus> StatusOf(string connectionString, string eventId) =>
        (await Read(connectionString, eventId)).Status;

    private static async Task<InboxStatus> WaitForStatus(string connectionString, string eventId, InboxStatus expected, TimeSpan timeout) =>
        await Polling.UntilAsync(() => StatusOf(connectionString, eventId), s => s == expected, timeout);

    /// <summary>Moves the clock past every pending retry time and wakes the processor, until the condition holds.</summary>
    private static async Task<bool> AdvanceUntil(Harness harness, Func<Task<bool>> condition)
    {
        for (var round = 0; round < 40; round++)
        {
            if (await condition())
            {
                return true;
            }
            harness.Clock.Advance(TimeSpan.FromDays(1));
            harness.Signal.Notify();
            if (await Polling.UntilAsync(condition, done => done, TimeSpan.FromSeconds(2)))
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public async Task Processing_ValidEvent_IsAppliedAndTheChargeChanges()
    {
        var connectionString = await NewDatabase();
        await TestDatabase.AddChargeAsync(connectionString, Entity, ChargeStatus.Pending);
        await TestDatabase.AddEventAsync(connectionString, "evt-1", "PAYMENT_UPDATED", Entity,
            TestDatabase.PaymentBody("evt-1", "PAYMENT_UPDATED", Entity, "OVERDUE"));
        await using var harness = new Harness(connectionString);

        await harness.StartAsync();
        harness.Signal.Notify();

        Assert.Equal(InboxStatus.Applied, await WaitForStatus(connectionString, "evt-1", InboxStatus.Applied, Patience));
        Assert.Equal(ChargeStatus.Overdue, (await TestDatabase.ReadChargeAsync(connectionString, Entity)).Status);
    }

    [Fact]
    public async Task Retry_OfAnEarlierFailedEvent_IsNotOvertakenByALaterEventOfTheSameEntity()
    {
        var connectionString = await NewDatabase();
        await TestDatabase.AddChargeAsync(connectionString, Entity, ChargeStatus.Pending);
        await TestDatabase.AddEventAsync(connectionString, "evt-first", EventTypeThatAlwaysFails, Entity, BodyThatAlwaysFails);
        await TestDatabase.AddEventAsync(connectionString, "evt-second", "PAYMENT_UPDATED", Entity,
            TestDatabase.PaymentBody("evt-second", "PAYMENT_UPDATED", Entity, "OVERDUE"));
        await using var harness = new Harness(connectionString);

        await harness.StartAsync();
        harness.Signal.Notify();
        await WaitForStatus(connectionString, "evt-first", InboxStatus.Failed, Patience);

        // While the first event is waiting for its retry, the second must wait too.
        var second = await WaitForStatus(connectionString, "evt-second", InboxStatus.Applied, NothingShouldHappen);
        Assert.Equal(InboxStatus.Pending, second);
        Assert.Equal(ChargeStatus.Pending, (await TestDatabase.ReadChargeAsync(connectionString, Entity)).Status);
    }

    [Fact]
    public async Task Retry_EventsOfOtherEntities_AreNotHeldBackByAFailedEvent()
    {
        var connectionString = await NewDatabase();
        await TestDatabase.AddChargeAsync(connectionString, "pay_other", ChargeStatus.Pending);
        await TestDatabase.AddEventAsync(connectionString, "evt-first", EventTypeThatAlwaysFails, Entity, BodyThatAlwaysFails);
        await TestDatabase.AddEventAsync(connectionString, "evt-other", "PAYMENT_UPDATED", "pay_other",
            TestDatabase.PaymentBody("evt-other", "PAYMENT_UPDATED", "pay_other", "OVERDUE"));
        await using var harness = new Harness(connectionString);

        await harness.StartAsync();
        harness.Signal.Notify();

        Assert.Equal(InboxStatus.Applied, await WaitForStatus(connectionString, "evt-other", InboxStatus.Applied, Patience));
        Assert.Equal(InboxStatus.Failed, await WaitForStatus(connectionString, "evt-first", InboxStatus.Failed, Patience));
    }

    [Theory]
    [InlineData("""{"id":"evt-bad","event":"PAYMENT_UPDATED","payment":{"id":"pay_1"}}""")]
    [InlineData("""{"id":"evt-bad","event":"PAYMENT_UPDATED","payment":"pay_1"}""")]
    [InlineData("""{"id":"evt-bad","event":"PAYMENT_UPDATED","payment":{"id":"pay_1","status":5}}""")]
    [InlineData("""{"id":"evt-bad","event":"PAYMENT_UPDATED","payment":{"status":"OVERDUE"}}""")]
    [InlineData("""{"id":"evt-bad","event":"PAYMENT_UPDATED","payment":{"id":"pay_1","status":"\ud800"}}""")]
    public async Task Processing_PayloadOfTheWrongShape_IsDeadLetteredAtOnceAndDoesNotHoldBackLaterEvents(string badBody)
    {
        // Retrying cannot fix a body that is the wrong shape: eight retries over three hours would only keep the
        // entity blocked until the same result.
        var connectionString = await NewDatabase();
        await TestDatabase.AddChargeAsync(connectionString, Entity, ChargeStatus.Pending);
        await TestDatabase.AddEventAsync(connectionString, "evt-bad", "PAYMENT_UPDATED", Entity, badBody);
        await TestDatabase.AddEventAsync(connectionString, "evt-good", "PAYMENT_UPDATED", Entity,
            TestDatabase.PaymentBody("evt-good", "PAYMENT_UPDATED", Entity, "OVERDUE"));
        await using var harness = new Harness(connectionString);

        await harness.StartAsync();
        harness.Signal.Notify();

        Assert.Equal(InboxStatus.DeadLettered, await WaitForStatus(connectionString, "evt-bad", InboxStatus.DeadLettered, Patience));
        Assert.Equal(InboxStatus.Applied, await WaitForStatus(connectionString, "evt-good", InboxStatus.Applied, Patience));
        var bad = await Read(connectionString, "evt-bad");
        Assert.NotNull(bad.LastError);
        Assert.Contains(harness.Logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("dead-lettered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retry_BackoffStopsGrowingAtTheCap()
    {
        var connectionString = await NewDatabase();
        await TestDatabase.AddEventAsync(connectionString, "evt-first", EventTypeThatAlwaysFails, Entity, BodyThatAlwaysFails);
        await using var harness = new Harness(connectionString);

        await harness.StartAsync();
        harness.Signal.Notify();
        // Attempt 7 would wait 2^7 = 128 minutes without the cap.
        await AdvanceUntil(harness, async () => (await Read(connectionString, "evt-first")).Attempts >= MaxAttempts - 1);
        var last = await Read(connectionString, "evt-first");

        Assert.Equal(MaxAttempts - 1, last.Attempts);
        Assert.Equal(MaxBackoff, last.NextAttemptAt - harness.Clock.GetUtcNow());
    }

    [Fact]
    public async Task Retry_EventsWithoutAnEntityKey_DoNotHoldEachOtherBack()
    {
        // An event with no payment or subscription has no key. Two of them are not "the same entity".
        var connectionString = await NewDatabase();
        await TestDatabase.AddChargeAsync(connectionString, "pay_other", ChargeStatus.Pending);
        await TestDatabase.AddEventAsync(connectionString, "evt-first", EventTypeThatAlwaysFails, null, BodyThatAlwaysFails);
        await TestDatabase.AddEventAsync(connectionString, "evt-second", "PAYMENT_UPDATED", null,
            TestDatabase.PaymentBody("evt-second", "PAYMENT_UPDATED", "pay_other", "OVERDUE"));
        await using var harness = new Harness(connectionString);

        await harness.StartAsync();
        harness.Signal.Notify();

        Assert.Equal(InboxStatus.Applied, await WaitForStatus(connectionString, "evt-second", InboxStatus.Applied, Patience));
        Assert.Equal(InboxStatus.Failed, await WaitForStatus(connectionString, "evt-first", InboxStatus.Failed, Patience));
    }

    [Fact]
    public async Task Retry_AfterMaxAttempts_TheEventIsDeadLetteredAndStopsBlockingLaterEvents()
    {
        var connectionString = await NewDatabase();
        await TestDatabase.AddChargeAsync(connectionString, Entity, ChargeStatus.Pending);
        await TestDatabase.AddEventAsync(connectionString, "evt-first", EventTypeThatAlwaysFails, Entity, BodyThatAlwaysFails);
        await TestDatabase.AddEventAsync(connectionString, "evt-second", "PAYMENT_UPDATED", Entity,
            TestDatabase.PaymentBody("evt-second", "PAYMENT_UPDATED", Entity, "OVERDUE"));
        await using var harness = new Harness(connectionString);

        await harness.StartAsync();
        harness.Signal.Notify();
        var gaveUp = await AdvanceUntil(harness, async () => await StatusOf(connectionString, "evt-first") == InboxStatus.DeadLettered);

        Assert.True(gaveUp, "the event was never dead-lettered");
        var first = await Read(connectionString, "evt-first");
        Assert.Equal(MaxAttempts, first.Attempts);
        Assert.NotNull(first.LastError);
        Assert.Contains(harness.Logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("dead-lettered", StringComparison.Ordinal));
        // The later event was held back until now and then went through.
        Assert.Equal(InboxStatus.Applied, await WaitForStatus(connectionString, "evt-second", InboxStatus.Applied, Patience));
    }

    [Fact]
    public async Task Retry_BackoffGrowsWithEachFailure()
    {
        var connectionString = await NewDatabase();
        await TestDatabase.AddEventAsync(connectionString, "evt-first", EventTypeThatAlwaysFails, Entity, BodyThatAlwaysFails);
        await using var harness = new Harness(connectionString);

        await harness.StartAsync();
        harness.Signal.Notify();
        await WaitForStatus(connectionString, "evt-first", InboxStatus.Failed, Patience);
        var afterFirst = await Read(connectionString, "evt-first");
        harness.Clock.Advance(TimeSpan.FromDays(1));
        harness.Signal.Notify();
        await Polling.UntilAsync(async () => (await Read(connectionString, "evt-first")).Attempts, a => a >= 2, Patience);
        var afterSecond = await Read(connectionString, "evt-first");

        Assert.Equal(TimeSpan.FromMinutes(2), afterFirst.NextAttemptAt - harness.Clock.GetUtcNow().AddDays(-1));
        Assert.Equal(TimeSpan.FromMinutes(4), afterSecond.NextAttemptAt - harness.Clock.GetUtcNow());
    }

    [Fact]
    public async Task Retry_OfASkippedEvent_IsNotOvertakenByALaterEventOfTheSameEntity()
    {
        var connectionString = await NewDatabase();
        await TestDatabase.AddEventAsync(connectionString, "evt-older", "PAYMENT_UPDATED", Entity,
            TestDatabase.PaymentBody("evt-older", "PAYMENT_UPDATED", Entity, "OVERDUE"));
        await using var harness = new Harness(connectionString);
        await harness.StartAsync();
        harness.Signal.Notify();
        await WaitForStatus(connectionString, "evt-older", InboxStatus.SkippedNotFound, Patience);

        // The charge now exists and a newer event arrives while the older one waits for its short retry.
        await TestDatabase.AddChargeAsync(connectionString, Entity, ChargeStatus.Pending);
        await TestDatabase.AddEventAsync(connectionString, "evt-newer", "PAYMENT_UPDATED", Entity,
            TestDatabase.PaymentBody("evt-newer", "PAYMENT_UPDATED", Entity, "RECEIVED"));
        harness.Signal.Notify();

        var newer = await WaitForStatus(connectionString, "evt-newer", InboxStatus.Applied, NothingShouldHappen);
        Assert.Equal(InboxStatus.Pending, newer);

        // When the retry comes, the older event is applied first and the newer one after it, so the charge
        // ends as the newer event says and a paid charge does not go back to overdue.
        harness.Clock.Advance(TimeSpan.FromMinutes(6));
        harness.Signal.Notify();
        Assert.Equal(InboxStatus.Applied, await WaitForStatus(connectionString, "evt-newer", InboxStatus.Applied, Patience));
        Assert.Equal(InboxStatus.Applied, (await Read(connectionString, "evt-older")).Status);
        Assert.Equal(ChargeStatus.Received, (await TestDatabase.ReadChargeAsync(connectionString, Entity)).Status);
    }

    [Fact]
    public async Task Skipped_AfterTheShortRetries_NoLongerHoldsBackLaterEventsOfTheSameEntity()
    {
        var connectionString = await NewDatabase();
        await TestDatabase.AddEventAsync(connectionString, "evt-older", "PAYMENT_UPDATED", Entity,
            TestDatabase.PaymentBody("evt-older", "PAYMENT_UPDATED", Entity, "OVERDUE"));
        await using var harness = new Harness(connectionString);
        await harness.StartAsync();
        harness.Signal.Notify();
        await AdvanceUntil(harness, async () => (await Read(connectionString, "evt-older")).NotFoundAttempts >= NotFoundAttempts);

        await TestDatabase.AddChargeAsync(connectionString, Entity, ChargeStatus.Pending);
        await TestDatabase.AddEventAsync(connectionString, "evt-newer", "PAYMENT_UPDATED", Entity,
            TestDatabase.PaymentBody("evt-newer", "PAYMENT_UPDATED", Entity, "RECEIVED"));
        harness.Signal.Notify();

        Assert.Equal(InboxStatus.Applied, await WaitForStatus(connectionString, "evt-newer", InboxStatus.Applied, Patience));
        Assert.Equal(InboxStatus.SkippedNotFound, (await Read(connectionString, "evt-older")).Status);
    }

    [Fact]
    public async Task Skipped_EntityNotFoundYet_IsRetriedShortlyAndAppliedOnceItExists()
    {
        var connectionString = await NewDatabase();
        await TestDatabase.AddEventAsync(connectionString, "evt-1", "PAYMENT_UPDATED", Entity,
            TestDatabase.PaymentBody("evt-1", "PAYMENT_UPDATED", Entity, "OVERDUE"));
        await using var harness = new Harness(connectionString);

        await harness.StartAsync();
        harness.Signal.Notify();
        await WaitForStatus(connectionString, "evt-1", InboxStatus.SkippedNotFound, Patience);
        var skipped = await Read(connectionString, "evt-1");
        Assert.Equal(1, skipped.NotFoundAttempts);
        Assert.Equal(0, skipped.Attempts);
        Assert.Equal(harness.Clock.GetUtcNow().AddMinutes(5), skipped.NextAttemptAt);

        // The record the event refers to finishes committing, and the short retry picks the event up.
        await TestDatabase.AddChargeAsync(connectionString, Entity, ChargeStatus.Pending);
        harness.Clock.Advance(TimeSpan.FromMinutes(6));
        harness.Signal.Notify();

        Assert.Equal(InboxStatus.Applied, await WaitForStatus(connectionString, "evt-1", InboxStatus.Applied, Patience));
        Assert.Equal(ChargeStatus.Overdue, (await TestDatabase.ReadChargeAsync(connectionString, Entity)).Status);
    }

    [Fact]
    public async Task Skipped_AfterEarlierTransientFailures_IsStillRetriedAndStillHoldsBackLaterEvents()
    {
        // Two transient failures already happened. They must not count as "not found" attempts: the event
        // has been skipped once, so it is still due for its short retries and still orders its successors.
        var connectionString = await NewDatabase();
        await TestDatabase.AddEventAsync(connectionString, "evt-older", "PAYMENT_UPDATED", Entity,
            TestDatabase.PaymentBody("evt-older", "PAYMENT_UPDATED", Entity, "OVERDUE"), attempts: 2);
        await using var harness = new Harness(connectionString);
        await harness.StartAsync();
        harness.Signal.Notify();
        await WaitForStatus(connectionString, "evt-older", InboxStatus.SkippedNotFound, Patience);

        await TestDatabase.AddChargeAsync(connectionString, Entity, ChargeStatus.Pending);
        await TestDatabase.AddEventAsync(connectionString, "evt-newer", "PAYMENT_UPDATED", Entity,
            TestDatabase.PaymentBody("evt-newer", "PAYMENT_UPDATED", Entity, "RECEIVED"));
        harness.Signal.Notify();
        var newer = await WaitForStatus(connectionString, "evt-newer", InboxStatus.Applied, NothingShouldHappen);
        Assert.Equal(InboxStatus.Pending, newer);

        harness.Clock.Advance(TimeSpan.FromMinutes(6));
        harness.Signal.Notify();
        Assert.Equal(InboxStatus.Applied, await WaitForStatus(connectionString, "evt-older", InboxStatus.Applied, Patience));
        Assert.Equal(InboxStatus.Applied, await WaitForStatus(connectionString, "evt-newer", InboxStatus.Applied, Patience));
        Assert.Equal(ChargeStatus.Received, (await TestDatabase.ReadChargeAsync(connectionString, Entity)).Status);
    }

    [Fact]
    public async Task Skipped_EntityNeverAppears_StopsRetryingAfterTheShortRetries()
    {
        var connectionString = await NewDatabase();
        await TestDatabase.AddEventAsync(connectionString, "evt-1", "PAYMENT_UPDATED", Entity,
            TestDatabase.PaymentBody("evt-1", "PAYMENT_UPDATED", Entity, "OVERDUE"));
        await using var harness = new Harness(connectionString);

        await harness.StartAsync();
        harness.Signal.Notify();
        await AdvanceUntil(harness, async () => (await Read(connectionString, "evt-1")).NotFoundAttempts >= NotFoundAttempts);
        var settled = await Read(connectionString, "evt-1");
        harness.Clock.Advance(TimeSpan.FromDays(1));
        harness.Signal.Notify();
        await Polling.UntilAsync(async () => (await Read(connectionString, "evt-1")).NotFoundAttempts, a => a > NotFoundAttempts, NothingShouldHappen);
        var later = await Read(connectionString, "evt-1");

        Assert.Equal(InboxStatus.SkippedNotFound, later.Status);
        Assert.Equal(NotFoundAttempts, later.NotFoundAttempts);
        Assert.Equal(settled.NextAttemptAt, later.NextAttemptAt);
    }

    [Fact]
    public async Task Skipped_AfterTheLastShortRetry_IsLoggedOnceSoThatAnAlertCanFire()
    {
        // A "not found" that never resolves is the quiet failure the inbox statuses exist to expose: the row is
        // there, but nobody looks at rows. Giving up has to leave a log line.
        var connectionString = await NewDatabase();
        await TestDatabase.AddEventAsync(connectionString, "evt-1", "PAYMENT_UPDATED", Entity,
            TestDatabase.PaymentBody("evt-1", "PAYMENT_UPDATED", Entity, "OVERDUE"));
        await using var harness = new Harness(connectionString);

        await harness.StartAsync();
        harness.Signal.Notify();
        await WaitForStatus(connectionString, "evt-1", InboxStatus.SkippedNotFound, Patience);
        Assert.DoesNotContain(harness.Logger.Entries, e => e.Level >= LogLevel.Warning);   // still has retries ahead

        await AdvanceUntil(harness, async () => (await Read(connectionString, "evt-1")).NotFoundAttempts >= NotFoundAttempts);
        harness.Clock.Advance(TimeSpan.FromDays(1));
        harness.Signal.Notify();
        await Polling.UntilAsync(async () => (await Read(connectionString, "evt-1")).NotFoundAttempts, a => a > NotFoundAttempts, NothingShouldHappen);

        var warnings = harness.Logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        var warning = Assert.Single(warnings);
        Assert.Contains("evt-1", warning.Message, StringComparison.Ordinal);
        Assert.Contains(Entity, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skipped_WhenTheLogSinkFailsAfterTheCommit_TheCommittedRowIsLeftAlone()
    {
        // The warning is written after the commit. A sink that throws (an alerting client that is down) must not
        // turn a row that is already saved into a failure that is retried and counted again.
        var connectionString = await NewDatabase();
        // Two short retries have already happened, so the next "not found" is the last one.
        await TestDatabase.AddEventAsync(connectionString, "evt-1", "PAYMENT_UPDATED", Entity,
            TestDatabase.PaymentBody("evt-1", "PAYMENT_UPDATED", Entity, "OVERDUE"), notFoundAttempts: NotFoundAttempts - 1);
        await using var harness = new Harness(connectionString, processorLogger: new ThrowingOnWarningLogger());
        await harness.StartAsync();
        harness.Signal.Notify();
        await Polling.UntilAsync(async () => (await Read(connectionString, "evt-1")).NotFoundAttempts, a => a >= NotFoundAttempts, Patience);
        harness.Clock.Advance(TimeSpan.FromDays(1));
        harness.Signal.Notify();
        await Polling.UntilAsync(async () => (await Read(connectionString, "evt-1")).Attempts, a => a > 0, NothingShouldHappen);

        var row = await Read(connectionString, "evt-1");
        Assert.Equal(InboxStatus.SkippedNotFound, row.Status);
        Assert.Equal(NotFoundAttempts, row.NotFoundAttempts);
        Assert.Equal(0, row.Attempts);
    }

    [Fact]
    public async Task Retry_EveryFailedAttemptBeforeTheLast_IsLoggedAsAWarning()
    {
        // The last failure is dead-lettered and logged as an error. The ones before it used to leave no line at all.
        var connectionString = await NewDatabase();
        await TestDatabase.AddEventAsync(connectionString, "evt-1", EventTypeThatAlwaysFails, Entity, BodyThatAlwaysFails);
        await using var harness = new Harness(connectionString);

        await harness.StartAsync();
        harness.Signal.Notify();
        var gaveUp = await AdvanceUntil(harness, async () => await StatusOf(connectionString, "evt-1") == InboxStatus.DeadLettered);

        Assert.True(gaveUp, "the event was never dead-lettered");
        var warnings = harness.Logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(MaxAttempts - 1, warnings.Count);
        Assert.Contains("failed attempt 1 of 8", warnings[0].Message, StringComparison.Ordinal);
        Assert.Contains("the subscription system is unavailable", warnings[0].Message, StringComparison.Ordinal);
        Assert.Single(harness.Logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("dead-lettered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retry_WhenTheLogSinkFailsAfterAFailedAttempt_TheFailureIsStillRecorded()
    {
        var connectionString = await NewDatabase();
        await TestDatabase.AddEventAsync(connectionString, "evt-1", EventTypeThatAlwaysFails, Entity, BodyThatAlwaysFails);
        await using var harness = new Harness(connectionString, processorLogger: new ThrowingOnWarningLogger());

        await harness.StartAsync();
        harness.Signal.Notify();

        Assert.Equal(InboxStatus.Failed, await WaitForStatus(connectionString, "evt-1", InboxStatus.Failed, Patience));
        var row = await Read(connectionString, "evt-1");
        Assert.Equal(1, row.Attempts);
        Assert.NotNull(row.NextAttemptAt);

        // The loop went on: a sink that throws must not send it into the pause that follows a failed pass.
        await TestDatabase.AddChargeAsync(connectionString, "pay_2", ChargeStatus.Pending);
        await TestDatabase.AddEventAsync(connectionString, "evt-2", "PAYMENT_UPDATED", "pay_2",
            TestDatabase.PaymentBody("evt-2", "PAYMENT_UPDATED", "pay_2", "OVERDUE"));
        harness.Signal.Notify();
        Assert.Equal(InboxStatus.Applied, await WaitForStatus(connectionString, "evt-2", InboxStatus.Applied, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Retry_AnExceptionWithAVeryLongMessage_IsKeptShortInTheRow()
    {
        var connectionString = await NewDatabase();
        await TestDatabase.AddEventAsync(connectionString, "evt-1", EventTypeThatAlwaysFails, Entity, BodyThatAlwaysFails);
        await using var harness = new Harness(connectionString, subscriptions: new FailingWith(new string('x', 5_000)));

        await harness.StartAsync();
        harness.Signal.Notify();

        Assert.Equal(InboxStatus.Failed, await WaitForStatus(connectionString, "evt-1", InboxStatus.Failed, Patience));
        var error = (await Read(connectionString, "evt-1")).LastError;
        Assert.Equal(new string('x', 1_000) + "...", error);
    }

    [Fact]
    public async Task Retry_AnExceptionThatWrapsTheRealReason_StoresAndLogsTheInnermostMessage()
    {
        // A failed save says only "An error occurred while saving the entity changes"; the reason is in the inner exception.
        var connectionString = await NewDatabase();
        await TestDatabase.AddEventAsync(connectionString, "evt-1", EventTypeThatAlwaysFails, Entity, BodyThatAlwaysFails);
        var wrapped = new InvalidOperationException("An error occurred while saving the entity changes", new TimeoutException("the statement timed out"));
        await using var harness = new Harness(connectionString, subscriptions: new FailingWith(wrapped));

        await harness.StartAsync();
        harness.Signal.Notify();

        Assert.Equal(InboxStatus.Failed, await WaitForStatus(connectionString, "evt-1", InboxStatus.Failed, Patience));
        Assert.Equal("the statement timed out", (await Read(connectionString, "evt-1")).LastError);
        var warning = Assert.Single(harness.Logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("the statement timed out", warning.Message, StringComparison.Ordinal);
    }

    private sealed class FailingWith(Exception error) : ISubscriptionEventHandler
    {
        public FailingWith(string message)
            : this(new InvalidOperationException(message))
        {
        }

        public Task<InboxStatus> HandleAsync(InboxEvent evt, CancellationToken ct) => throw error;
    }

    private sealed class ThrowingOnWarningLogger : ILogger<InboxProcessor>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                throw new InvalidOperationException("the alerting sink is unavailable");
            }
        }
    }

    [Fact]
    public async Task Dispatch_EventTypeNobodyHandles_IsNoOpAndLogsAWarning()
    {
        var connectionString = await NewDatabase();
        await TestDatabase.AddEventAsync(connectionString, "evt-1", "INVOICE_CREATED", null, """{"id":"evt-1","event":"INVOICE_CREATED"}""");
        await using var harness = new Harness(connectionString);

        await harness.StartAsync();
        harness.Signal.Notify();

        Assert.Equal(InboxStatus.NoOp, await WaitForStatus(connectionString, "evt-1", InboxStatus.NoOp, Patience));
        Assert.Contains(harness.DispatcherLogger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("INVOICE_CREATED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispatch_EventTypeWithControlCharacters_IsLoggedWithoutThem()
    {
        // The event type comes from the request body. Logged as it is, the line break would start a line of the
        // attacker's choosing in the log, and the escape would recolor the terminal.
        var connectionString = await NewDatabase();
        await TestDatabase.AddEventAsync(connectionString, HostileId, "INVOICE\nfail: Forged.Category[0]\u001b[31m", null,
            """{"id":"x","event":"INVOICE"}""");
        await using var harness = new Harness(connectionString);

        await harness.StartAsync();
        harness.Signal.Notify();

        Assert.Equal(InboxStatus.NoOp, await WaitForStatus(connectionString, HostileId, InboxStatus.NoOp, Patience));
        var warning = Assert.Single(harness.DispatcherLogger.Entries, e => e.Level == LogLevel.Warning);
        AssertNothingCouldForgeALine([warning.Message]);
        Assert.Contains("INVOICE?fail: Forged.Category[0]?[31m", warning.Message, StringComparison.Ordinal);
        Assert.Contains(HostileIdAsLogged, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retry_AnIdAndAReasonFromTheOutside_AreLoggedWithoutCharactersThatCouldForgeALine()
    {
        var connectionString = await NewDatabase();
        await TestDatabase.AddEventAsync(connectionString, HostileId, EventTypeThatAlwaysFails, Entity, BodyThatAlwaysFails);
        await using var harness = new Harness(connectionString, subscriptions: new FailingWith("boom\nfail: Forged"));

        await harness.StartAsync();
        harness.Signal.Notify();
        var gaveUp = await AdvanceUntil(harness, async () => await StatusOf(connectionString, HostileId) == InboxStatus.DeadLettered);

        Assert.True(gaveUp, "the event was never dead-lettered");
        var attempts = harness.Logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
        var error = Assert.Single(harness.Logger.Entries, e => e.Level == LogLevel.Error).Message;
        Assert.Equal(MaxAttempts - 1, attempts.Count);
        AssertNothingCouldForgeALine(attempts.Append(error));
        Assert.All(attempts, m => Assert.Contains(HostileIdAsLogged, m, StringComparison.Ordinal));
        Assert.All(attempts, m => Assert.Contains("boom?fail: Forged", m, StringComparison.Ordinal));
        Assert.Contains(HostileIdAsLogged, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Processing_PayloadOfTheWrongShapeWithAnIdFromTheOutside_IsLoggedWithoutCharactersThatCouldForgeALine()
    {
        var connectionString = await NewDatabase();
        await TestDatabase.AddEventAsync(connectionString, HostileId, "PAYMENT_UPDATED", Entity,
            """{"id":"x","event":"PAYMENT_UPDATED","payment":"pay_1"}""");
        await using var harness = new Harness(connectionString);

        await harness.StartAsync();
        harness.Signal.Notify();

        Assert.Equal(InboxStatus.DeadLettered, await WaitForStatus(connectionString, HostileId, InboxStatus.DeadLettered, Patience));
        var error = Assert.Single(harness.Logger.Entries, e => e.Level == LogLevel.Error).Message;
        AssertNothingCouldForgeALine([error]);
        Assert.Contains(HostileIdAsLogged, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skipped_AnIdAndAnEntityKeyFromTheOutside_AreLoggedWithoutCharactersThatCouldForgeALine()
    {
        var connectionString = await NewDatabase();
        await TestDatabase.AddEventAsync(connectionString, HostileId, "PAYMENT_UPDATED", "pay\n1",
            TestDatabase.PaymentBody("x", "PAYMENT_UPDATED", Entity, "OVERDUE"), notFoundAttempts: NotFoundAttempts - 1);
        await using var harness = new Harness(connectionString);

        await harness.StartAsync();
        harness.Signal.Notify();
        await Polling.UntilAsync(
            () => Task.FromResult(harness.Logger.Entries.Count(e => e.Level == LogLevel.Warning)), count => count > 0, Patience);

        var warning = Assert.Single(harness.Logger.Entries, e => e.Level == LogLevel.Warning).Message;
        AssertNothingCouldForgeALine([warning]);
        Assert.Contains(HostileIdAsLogged, warning, StringComparison.Ordinal);
        Assert.Contains("pay?1", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Processing_APaymentLeftUnappliedForAnIdFromTheOutside_IsLoggedWithoutCharactersThatCouldForgeALine()
    {
        var connectionString = await NewDatabase();
        await TestDatabase.AddChargeAsync(connectionString, Entity, ChargeStatus.Refunded);
        await TestDatabase.AddEventAsync(connectionString, HostileId, "PAYMENT_UPDATED", Entity,
            TestDatabase.PaymentBody("x", "PAYMENT_UPDATED", Entity, "RECEIVED"));
        await using var harness = new Harness(connectionString);

        await harness.StartAsync();
        harness.Signal.Notify();

        Assert.Equal(InboxStatus.NoOp, await WaitForStatus(connectionString, HostileId, InboxStatus.NoOp, Patience));
        var warning = Assert.Single(harness.HandlerLogger.Entries, e => e.Level == LogLevel.Warning).Message;
        AssertNothingCouldForgeALine([warning]);
        Assert.Contains(HostileIdAsLogged, warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loop_DatabaseUnavailable_LogsAndKeepsTheHostRunning()
    {
        var connectionString = await NewDatabase();
        await using var harness = new Harness(connectionString, new UnavailableDatabaseScopes());

        await harness.StartAsync();
        var logged = await Polling.UntilAsync(
            () => Task.FromResult(harness.Logger.Entries.Any(e => e.Level == LogLevel.Error)), found => found, Patience);

        Assert.True(logged, "the loop failure was not logged");
        Assert.NotNull(harness.Running);
        Assert.False(harness.Running.IsCompleted, "the processor stopped instead of waiting to try again");
    }

    [Fact]
    public async Task Loop_WhenTheDatabaseStaysDown_WaitsBeforeTryingAgain()
    {
        // Without the wait a database outage becomes a hot loop that floods the log with a stack trace per pass.
        var connectionString = await NewDatabase();
        var scopes = new UnavailableDatabaseScopes();
        await using var harness = new Harness(connectionString, scopes);

        await harness.StartAsync();
        await Polling.UntilAsync(() => Task.FromResult(scopes.Calls), calls => calls >= 1, Patience);
        await Task.Delay(NothingShouldHappen);

        Assert.Equal(1, scopes.Calls);
    }

    [Fact]
    public async Task Retry_OfAnEventThatFailedBefore_ClearsItsErrorOnceItSucceeds()
    {
        var connectionString = await NewDatabase();
        await TestDatabase.AddChargeAsync(connectionString, Entity, ChargeStatus.Pending);
        await TestDatabase.AddEventAsync(connectionString, "evt-1", "PAYMENT_UPDATED", Entity,
            TestDatabase.PaymentBody("evt-1", "PAYMENT_UPDATED", Entity, "OVERDUE"));
        await using (var db = TestDatabase.Open(connectionString))
        {
            var failedBefore = await db.InboxEvents.SingleAsync();
            failedBefore.Status = InboxStatus.Failed;
            failedBefore.Attempts = 2;
            failedBefore.LastError = "the database was unavailable";
            failedBefore.NextAttemptAt = new DateTimeOffset(2026, 10, 1, 11, 0, 0, TimeSpan.Zero);
            await db.SaveChangesAsync();
        }
        await using var harness = new Harness(connectionString);

        await harness.StartAsync();
        harness.Signal.Notify();

        Assert.Equal(InboxStatus.Applied, await WaitForStatus(connectionString, "evt-1", InboxStatus.Applied, Patience));
        var stored = await Read(connectionString, "evt-1");
        Assert.Null(stored.LastError);   // a row that says "failed: ..." next to Applied would send a reader the wrong way
        Assert.Equal(2, stored.Attempts);
    }

    /// <summary>A scope factory whose every call fails the way a database that is down would.</summary>
    private sealed class UnavailableDatabaseScopes : IServiceScopeFactory
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("the database is unavailable");
        }
    }
}

using Microsoft.EntityFrameworkCore;
using WebhookInbox;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<PaymentsDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Payments")));
builder.Services.AddOptions<GatewayOptions>()
    .Bind(builder.Configuration.GetSection("Gateway"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.WebhookToken), "Gateway:WebhookToken is required")
    .Validate(o => o.WebhookToken == o.WebhookToken.Trim(), "Gateway:WebhookToken must not start or end with whitespace")
    .Validate(o => !string.IsNullOrWhiteSpace(o.TokenHeader), "Gateway:TokenHeader is required")
    .Validate(
        o => o.MaxBodyBytes is > 0 and <= GatewayOptions.MaxAllowedBodyBytes,
        $"Gateway:MaxBodyBytes must be between 1 and {GatewayOptions.MaxAllowedBodyBytes}")
    .ValidateOnStart();
builder.Services.AddSingleton<InboxSignal>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<PaymentEventHandler>();
builder.Services.AddScoped<ISubscriptionEventHandler, NoSubscriptionHandler>();
builder.Services.AddScoped<EventDispatcher>();
builder.Services.AddScoped<IPlanService, NoPlanService>();
builder.Services.AddHostedService<InboxProcessor>();

var app = builder.Build();

// Configuration added by the host (environment variables, a test factory) is only complete after Build.
if (string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("Payments")))
{
    throw new InvalidOperationException("ConnectionStrings:Payments is required");
}

// Local convenience, off unless configured: creates the schema from the model so that the compose
// database is usable without a migration step. The tests create their own schema.
if (app.Configuration.GetValue<bool>("Database:EnsureCreated"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.EnsureCreatedAsync();
}

app.MapPaymentWebhook();
await app.RunAsync();

internal sealed class NoPlanService : IPlanService
{
    public Task ReevaluateAsync(long planId, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class NoSubscriptionHandler : ISubscriptionEventHandler
{
    public Task<InboxStatus> HandleAsync(InboxEvent evt, CancellationToken ct) =>
        Task.FromResult(InboxStatus.NoOp);
}

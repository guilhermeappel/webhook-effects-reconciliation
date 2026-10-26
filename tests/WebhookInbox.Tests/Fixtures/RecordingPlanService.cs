namespace WebhookInbox.Tests;

/// <summary>Records which plans the handler asked to re-evaluate, so a test can say none was.</summary>
internal sealed class RecordingPlanService : IPlanService
{
    public List<long> Reevaluated { get; } = [];

    public Task ReevaluateAsync(long planId, CancellationToken ct)
    {
        Reevaluated.Add(planId);
        return Task.CompletedTask;
    }
}

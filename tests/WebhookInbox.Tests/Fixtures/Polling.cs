namespace WebhookInbox.Tests;

/// <summary>
/// Waits for something asynchronous (the processor handling an event) by asking again until it has happened or
/// a timeout has passed, instead of sleeping for a fixed time.
/// </summary>
public static class Polling
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Calls <paramref name="probe"/> until <paramref name="isDone"/> accepts its result or
    /// <paramref name="timeout"/> has passed, and returns the last result; the caller decides what a timeout means.
    /// </summary>
    public static async Task<T> UntilAsync<T>(Func<Task<T>> probe, Func<T, bool> isDone, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            var result = await probe();
            if (isDone(result) || DateTime.UtcNow >= deadline)
            {
                return result;
            }

            await Task.Delay(Interval);
        }
    }
}

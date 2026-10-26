using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace WebhookInbox.Tests;

public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

/// <summary>Keeps what was logged so a test can say that an error was reported.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyCollection<LogEntry> Entries => [.. _entries];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
}

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace WebhookInbox.Tests;

/// <summary>Collects what every logger the host creates writes, for tests that read the endpoint's logs.</summary>
public sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyCollection<LogEntry> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new Recorder(_entries);

    public void Dispose()
    {
    }

    private sealed class Recorder(ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CopilotStudioA2A.Tests;

/// <summary>Captures formatted and structured logs without console output or external sinks.</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<RecordedLog> _entries = new();

    internal IReadOnlyList<RecordedLog> Entries => _entries.ToArray();

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _entries);

    /// <inheritdoc/>
    public void Dispose() { }

    private sealed class RecordingLogger(string categoryName, ConcurrentQueue<RecordedLog> entries) : ILogger
    {
        /// <inheritdoc/>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc/>
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        /// <inheritdoc/>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();
            entries.Enqueue(new RecordedLog(categoryName, logLevel, formatter(state, exception), exception, properties));
        }
    }
}

internal sealed record RecordedLog(string Category, LogLevel Level, string Message, Exception? Exception,
    IReadOnlyDictionary<string, object?> Properties);
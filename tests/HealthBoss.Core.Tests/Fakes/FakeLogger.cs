// <copyright file="FakeLogger.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using Microsoft.Extensions.Logging;

namespace HealthBoss.Core.Tests.Fakes;

/// <summary>
/// Test double that captures log entries for assertion in tests.
/// Thread-safe for concurrent logging scenarios.
/// </summary>
/// <typeparam name="T">The category type for the logger.</typeparam>
internal sealed class FakeLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> _entries = [];
    private readonly object _lock = new();

    /// <summary>
    /// Gets a snapshot of all captured log entries.
    /// </summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        lock (_lock)
        {
            _entries.Add(new LogEntry(logLevel, message, exception));
        }
    }

    /// <summary>
    /// Represents a single captured log entry.
    /// </summary>
    /// <param name="Level">The log level.</param>
    /// <param name="Message">The formatted log message.</param>
    /// <param name="Exception">The optional exception associated with the entry.</param>
    internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}

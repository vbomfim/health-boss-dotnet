using System.Collections.Concurrent;
using HealthBoss.Core;
using HealthBoss.Core.Contracts;

namespace HealthBoss.AspNetCore.Tests;

/// <summary>
/// A simple in-memory signal buffer for testing purposes.
/// Captures all recorded signals for assertion.
/// </summary>
internal sealed class RecordingSignalBuffer : ISignalBuffer
{
    private readonly ConcurrentBag<HealthSignal> _signals = [];

    /// <summary>Gets all recorded signals as a list for assertions.</summary>
    public IReadOnlyList<HealthSignal> Signals => [.. _signals];

    /// <inheritdoc />
    public void Record(HealthSignal signal) => _signals.Add(signal);

    /// <inheritdoc />
    public IReadOnlyList<HealthSignal> GetSignals(TimeSpan window) =>
        [.. _signals];

    /// <inheritdoc />
    public void Trim(DateTimeOffset cutoff)
    {
        // No-op for testing.
    }

    /// <inheritdoc />
    public int Count => _signals.Count;
}

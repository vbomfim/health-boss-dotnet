// <copyright file="ISignalBuffer.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Thread-safe ring buffer for health signal ingestion and retrieval.
/// Extends <see cref="ISignalRecorder"/> so callers that only record signals
/// can depend on the narrower interface (Interface Segregation Principle).
/// </summary>
public interface ISignalBuffer : ISignalRecorder
{
    /// <summary>
    /// Returns a snapshot of signals within the specified time window.
    /// </summary>
    /// <param name="window">The duration of the sliding window to query.</param>
    /// <returns>An immutable list of signals within the window.</returns>
    IReadOnlyList<HealthSignal> GetSignals(TimeSpan window);

    /// <summary>
    /// Removes signals older than the specified cutoff.
    /// </summary>
    /// <param name="cutoff">Signals with timestamps before this value are removed.</param>
    void Trim(DateTimeOffset cutoff);

    /// <summary>
    /// Gets the current number of buffered signals.
    /// </summary>
    int Count { get; }
}

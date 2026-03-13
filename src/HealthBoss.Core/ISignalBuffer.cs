// <copyright file="ISignalBuffer.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Thread-safe ring buffer for health signal ingestion and retrieval.
/// Provides both write (<see cref="Record"/>) and read (<see cref="GetSignals"/>)
/// access to buffered signals for a single component.
/// </summary>
public interface ISignalBuffer
{
    /// <summary>
    /// Records a health signal. O(1), never blocks readers.
    /// </summary>
    /// <param name="signal">The signal to record.</param>
    void Record(HealthSignal signal);

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

// <copyright file="SessionTypes.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core.Contracts;

/// <summary>
/// Represents the outcome of a tracked session.
/// </summary>
public enum SessionOutcome
{
    /// <summary>The session completed successfully.</summary>
    Success,

    /// <summary>The session completed with a failure.</summary>
    Failure,

    /// <summary>The session timed out before completing.</summary>
    Timeout,

    /// <summary>The session was cancelled (e.g., disposed without calling Complete).</summary>
    Cancelled,
}

/// <summary>
/// A point-in-time snapshot of session health metrics.
/// </summary>
/// <param name="ActiveSessions">The number of currently active sessions.</param>
/// <param name="RecentSuccesses">Successes within the sliding window.</param>
/// <param name="RecentFailures">Failures (including timeouts and cancellations) within the sliding window.</param>
/// <param name="SuccessRate">Success rate within the sliding window (0.0–1.0). Returns 1.0 when no sessions have completed.</param>
/// <param name="SnapshotAt">The timestamp when this snapshot was taken.</param>
public sealed record SessionHealthSnapshot(
    int ActiveSessions,
    int RecentSuccesses,
    int RecentFailures,
    double SuccessRate,
    DateTimeOffset SnapshotAt);

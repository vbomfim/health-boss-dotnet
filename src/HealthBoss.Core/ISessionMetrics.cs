// <copyright file="ISessionMetrics.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Metrics contract for session lifecycle tracking: active session count
/// and drain status gauges.
/// <para>
/// Consumers: <c>SessionHealthTracker</c>, <c>DrainCoordinator</c>.
/// </para>
/// </summary>
/// <remarks>
/// Split from <see cref="IHealthBossMetrics"/> per Interface Segregation Principle (ISP).
/// See GitHub Issue #61.
/// </remarks>
public interface ISessionMetrics
{
    /// <summary>
    /// Sets the active session count (observable gauge).
    /// Instrument: <c>healthboss.active_sessions</c>.
    /// </summary>
    /// <param name="count">The current number of active sessions.</param>
    void SetActiveSessionCount(int count);

    /// <summary>
    /// Sets the current drain status (observable gauge).
    /// Instrument: <c>healthboss.drain_status</c>.
    /// </summary>
    /// <param name="status">The current drain status.</param>
    void SetDrainStatus(DrainStatus status);
}

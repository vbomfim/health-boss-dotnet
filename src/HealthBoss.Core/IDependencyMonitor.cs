// <copyright file="IDependencyMonitor.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Per-dependency health monitor that owns a signal buffer, evaluates signals
/// against a health policy, and maintains the current health state via the
/// transition engine.
/// </summary>
public interface IDependencyMonitor
{
    /// <summary>
    /// Gets the dependency identifier this monitor tracks.
    /// </summary>
    DependencyId DependencyId { get; }

    /// <summary>
    /// Records a health signal for this dependency.
    /// </summary>
    /// <param name="signal">The health signal to record.</param>
    void RecordSignal(HealthSignal signal);

    /// <summary>
    /// Returns a point-in-time snapshot of this dependency's health state.
    /// Evaluates buffered signals against the configured policy.
    /// </summary>
    /// <returns>A snapshot containing the current state, latest assessment, and metadata.</returns>
    DependencySnapshot GetSnapshot();

    /// <summary>
    /// Gets the current health state of this dependency.
    /// </summary>
    HealthState CurrentState { get; }
}

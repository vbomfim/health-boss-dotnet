// <copyright file="IHealthStateReader.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Read-only view of the aggregate health state.
/// Used by the ShutdownOrchestrator to decide whether to initiate shutdown.
/// Separated from <see cref="IHealthReportProvider"/> to avoid coupling
/// the Core layer to AspNetCore report types.
/// </summary>
public interface IHealthStateReader
{
    /// <summary>
    /// Gets the current aggregate health state across all monitored dependencies.
    /// </summary>
    HealthState CurrentState { get; }

    /// <summary>
    /// Gets the current aggregate readiness status.
    /// </summary>
    ReadinessStatus ReadinessStatus { get; }

    /// <summary>
    /// Gets snapshots for all registered dependencies.
    /// </summary>
    IReadOnlyCollection<DependencySnapshot> GetAllSnapshots();

    /// <summary>
    /// Gets the total number of signals recorded across all dependencies.
    /// </summary>
    int TotalSignalCount { get; }

    /// <summary>
    /// Gets the time of the last state transition across any dependency.
    /// Null if no transitions have occurred.
    /// </summary>
    DateTimeOffset? LastTransitionTime { get; }
}

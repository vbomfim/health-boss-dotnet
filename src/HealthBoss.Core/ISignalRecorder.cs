// <copyright file="ISignalRecorder.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Write-only interface for recording health signals at the orchestrator level.
/// Routes signals to the correct dependency monitor by <see cref="DependencyId"/>.
/// <para>
/// Prefer this over keyed <see cref="ISignalBuffer"/> for application code that
/// records signals manually — it decouples callers from the internal per-component
/// buffer topology.
/// </para>
/// </summary>
public interface ISignalRecorder
{
    /// <summary>
    /// Records a health signal for the specified dependency.
    /// If the dependency is not registered, the signal is dropped with a warning.
    /// </summary>
    /// <param name="id">The dependency identifier.</param>
    /// <param name="signal">The health signal to record.</param>
    void RecordSignal(DependencyId id, HealthSignal signal);
}

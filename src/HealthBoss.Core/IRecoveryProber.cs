// <copyright file="IRecoveryProber.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Periodically probes dependencies in <see cref="HealthState.CircuitOpen"/> state
/// to detect recovery. Each dependency gets an independent probing loop that runs
/// at the interval specified by <see cref="Contracts.HealthPolicy.RecoveryProbeInterval"/>.
/// </summary>
public interface IRecoveryProber : IDisposable
{
    /// <summary>
    /// Starts periodic probing for a dependency in CircuitOpen state.
    /// If the dependency is already being probed, the call is a no-op.
    /// </summary>
    /// <param name="id">The dependency to probe.</param>
    /// <param name="policy">The health policy containing the probe interval.</param>
    /// <param name="ct">Cancellation token to stop the probing loop.</param>
    /// <returns>A task that completes once the probing loop has been started.</returns>
    Task StartProbingAsync(DependencyId id, HealthPolicy policy, CancellationToken ct);

    /// <summary>
    /// Stops probing for a dependency (e.g., when it recovers or is removed).
    /// If the dependency is not being probed, the call is a no-op.
    /// </summary>
    /// <param name="id">The dependency to stop probing.</param>
    void StopProbing(DependencyId id);

    /// <summary>
    /// Gets a value indicating whether the specified dependency is currently being probed.
    /// </summary>
    /// <param name="id">The dependency to check.</param>
    /// <returns><c>true</c> if probing is active; otherwise, <c>false</c>.</returns>
    bool IsProbing(DependencyId id);
}

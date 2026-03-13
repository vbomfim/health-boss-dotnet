// <copyright file="IShutdownOrchestrator.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Enforces the 3-gate safety chain before allowing a shutdown signal.
/// <para>
/// Gate 1: <b>MinSignals</b> — enough signals have been observed to trust health data.<br/>
/// Gate 2: <b>Cooldown</b> — sufficient time since last state transition.<br/>
/// Gate 3: <b>ConfirmDelegate</b> — caller-supplied async delegate returns <c>true</c>.
/// </para>
/// <para>All 3 gates must pass for shutdown to be approved.</para>
/// </summary>
public interface IShutdownOrchestrator
{
    /// <summary>
    /// Synchronously evaluate gates 1 (MinSignals) and 2 (Cooldown).
    /// Gate 3 (ConfirmDelegate) is skipped when <see cref="ShutdownConfig.RequireConfirmDelegate"/>
    /// is <c>false</c>; otherwise, this method returns denied because the async delegate
    /// cannot be invoked synchronously — use <see cref="RequestShutdownAsync"/> instead.
    /// </summary>
    /// <param name="stateReader">Read-only view of current health state.</param>
    /// <returns>A <see cref="ShutdownDecision"/> with gate details.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stateReader"/> is <c>null</c>.</exception>
    ShutdownDecision Evaluate(IHealthStateReader stateReader);

    /// <summary>
    /// Request graceful shutdown by evaluating all 3 gates, including the async
    /// confirm delegate (wrapped in a 5-second timeout).
    /// </summary>
    /// <param name="stateReader">Read-only view of current health state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ShutdownDecision"/> indicating approval or the blocking gate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stateReader"/> is <c>null</c>.</exception>
    Task<ShutdownDecision> RequestShutdownAsync(
        IHealthStateReader stateReader,
        CancellationToken cancellationToken);
}

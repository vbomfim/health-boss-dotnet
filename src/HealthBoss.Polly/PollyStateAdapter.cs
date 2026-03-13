// <copyright file="PollyStateAdapter.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core;
using HealthBoss.Core.Contracts;
using Polly.CircuitBreaker;

namespace HealthBoss.Polly;

/// <summary>
/// Maps Polly <see cref="CircuitState"/> values to HealthBoss <see cref="HealthSignal"/> instances.
/// </summary>
internal static class PollyStateAdapter
{
    /// <summary>
    /// Creates a <see cref="HealthSignal"/> from the given Polly circuit state.
    /// </summary>
    /// <param name="state">The Polly circuit breaker state.</param>
    /// <param name="dependencyId">The dependency that owns the circuit.</param>
    /// <param name="clock">Clock used to timestamp the signal.</param>
    /// <returns>A health signal reflecting the circuit state transition.</returns>
    public static HealthSignal ToSignal(CircuitState state, DependencyId dependencyId, ISystemClock clock)
    {
        var (outcome, metadata) = state switch
        {
            CircuitState.Closed => (SignalOutcome.Success, "Circuit closed"),
            CircuitState.Open => (SignalOutcome.Failure, "Circuit opened"),
            CircuitState.HalfOpen => (SignalOutcome.Success, "Circuit half-open"),
            CircuitState.Isolated => (SignalOutcome.Failure, "Circuit isolated"),
            _ => (SignalOutcome.Failure, $"Circuit unknown ({state})"),
        };

        return new HealthSignal(
            timestamp: clock.UtcNow,
            dependencyId: dependencyId,
            outcome: outcome,
            metadata: metadata);
    }
}

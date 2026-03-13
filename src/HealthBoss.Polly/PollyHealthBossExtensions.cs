// <copyright file="PollyHealthBossExtensions.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core;
using HealthBoss.Core.Contracts;
using Polly.CircuitBreaker;

namespace HealthBoss.Polly;

/// <summary>
/// Extension methods that wire Polly v8 circuit breaker state transitions
/// into HealthBoss signal recording.
/// </summary>
public static class PollyHealthBossExtensions
{
    /// <summary>
    /// Adds HealthBoss signal recording to circuit breaker state change callbacks.
    /// When the circuit opens, closes, or half-opens, a corresponding
    /// <see cref="HealthSignal"/> is recorded via the <paramref name="recorder"/>.
    /// Existing callbacks on the options are preserved and invoked first.
    /// </summary>
    /// <param name="options">The circuit breaker strategy options to augment.</param>
    /// <param name="recorder">The signal buffer to write health signals to.</param>
    /// <param name="dependencyId">Identifies which dependency the circuit breaker protects.</param>
    /// <param name="clock">Clock used to timestamp recorded signals.</param>
    /// <returns>The same <paramref name="options"/> instance for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="recorder"/> or <paramref name="clock"/> is <c>null</c>.
    /// </exception>
    public static CircuitBreakerStrategyOptions WithHealthBossTracking(
        this CircuitBreakerStrategyOptions options,
        ISignalBuffer recorder,
        DependencyId dependencyId,
        ISystemClock clock)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(clock);

        var previousOnOpened = options.OnOpened;
        options.OnOpened = async args =>
        {
            if (previousOnOpened is not null)
            {
                await previousOnOpened(args);
            }

            var signal = PollyStateAdapter.ToSignal(CircuitState.Open, dependencyId, clock);
            recorder.Record(signal);
        };

        var previousOnClosed = options.OnClosed;
        options.OnClosed = async args =>
        {
            if (previousOnClosed is not null)
            {
                await previousOnClosed(args);
            }

            var signal = PollyStateAdapter.ToSignal(CircuitState.Closed, dependencyId, clock);
            recorder.Record(signal);
        };

        var previousOnHalfOpened = options.OnHalfOpened;
        options.OnHalfOpened = async args =>
        {
            if (previousOnHalfOpened is not null)
            {
                await previousOnHalfOpened(args);
            }

            var signal = PollyStateAdapter.ToSignal(CircuitState.HalfOpen, dependencyId, clock);
            recorder.Record(signal);
        };

        return options;
    }
}

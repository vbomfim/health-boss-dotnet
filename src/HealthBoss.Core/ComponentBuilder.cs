// <copyright file="ComponentBuilder.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Fluent builder for configuring a tracked component's health policy.
/// All parameters have sensible defaults so that a minimal configuration works out of the box.
/// </summary>
public sealed class ComponentBuilder
{
    private TimeSpan _window = TimeSpan.FromMinutes(5);
    private double _healthyAbove = 0.9;
    private double _degradedAbove = 0.5;
    private int _minimumSignals = 5;
    private ResponseTimePolicy? _responseTimePolicy;

    /// <summary>
    /// Sets the sliding window duration for signal evaluation.
    /// Default: 5 minutes.
    /// </summary>
    /// <param name="window">The sliding window duration.</param>
    /// <returns>This builder for chaining.</returns>
    public ComponentBuilder Window(TimeSpan window)
    {
        _window = window;
        return this;
    }

    /// <summary>
    /// Sets the success-rate threshold above which the component is considered healthy.
    /// When the rate drops below this value, the state transitions to Degraded.
    /// Default: 0.9 (90%).
    /// </summary>
    /// <param name="threshold">The healthy threshold (0.0–1.0).</param>
    /// <returns>This builder for chaining.</returns>
    public ComponentBuilder HealthyAbove(double threshold)
    {
        _healthyAbove = threshold;
        return this;
    }

    /// <summary>
    /// Sets the success-rate threshold above which the component remains degraded (not circuit-open).
    /// When the rate drops below this value, the circuit opens.
    /// Default: 0.5 (50%).
    /// </summary>
    /// <param name="threshold">The degraded threshold (0.0–1.0).</param>
    /// <returns>This builder for chaining.</returns>
    public ComponentBuilder DegradedAbove(double threshold)
    {
        _degradedAbove = threshold;
        return this;
    }

    /// <summary>
    /// Sets the minimum number of signals required before health evaluation begins.
    /// Default: 5.
    /// </summary>
    /// <param name="count">The minimum signal count.</param>
    /// <returns>This builder for chaining.</returns>
    public ComponentBuilder MinimumSignals(int count)
    {
        _minimumSignals = count;
        return this;
    }

    /// <summary>
    /// Configures an optional response-time (latency) policy for this component.
    /// When configured, the worst-of-both-dimensions determines the final health state.
    /// </summary>
    /// <param name="configure">The fluent configuration action for the response-time policy.</param>
    /// <returns>This builder for chaining.</returns>
    public ComponentBuilder WithResponseTime(Action<ResponseTimePolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new ResponseTimePolicyBuilder();
        configure(builder);
        _responseTimePolicy = builder.Build();

        return this;
    }

    /// <summary>
    /// Builds the configured <see cref="HealthPolicy"/> from the current builder state.
    /// </summary>
    /// <returns>A fully-constructed <see cref="HealthPolicy"/>.</returns>
    internal HealthPolicy Build() => new(
        SlidingWindow: _window,
        DegradedThreshold: _healthyAbove,
        CircuitOpenThreshold: _degradedAbove,
        MinSignalsForEvaluation: _minimumSignals,
        CooldownBeforeTransition: TimeSpan.FromSeconds(30),
        RecoveryProbeInterval: TimeSpan.FromSeconds(10),
        Jitter: new JitterConfig(TimeSpan.Zero, TimeSpan.Zero),
        ResponseTime: _responseTimePolicy);
}

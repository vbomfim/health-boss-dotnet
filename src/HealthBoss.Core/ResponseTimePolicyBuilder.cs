// <copyright file="ResponseTimePolicyBuilder.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Fluent builder for configuring a <see cref="ResponseTimePolicy"/>.
/// All parameters have sensible defaults for common latency monitoring scenarios.
/// </summary>
public sealed class ResponseTimePolicyBuilder
{
    private double _percentile = 0.95;
    private TimeSpan _degradedAfter = TimeSpan.FromMilliseconds(500);
    private TimeSpan? _unhealthyAfter;
    private int _minimumSignals = 5;

    /// <summary>
    /// Sets the latency percentile to evaluate (e.g. 0.95 for p95).
    /// Default: 0.95.
    /// </summary>
    /// <param name="value">The percentile value in (0.0, 1.0) exclusive.</param>
    /// <returns>This builder for chaining.</returns>
    public ResponseTimePolicyBuilder Percentile(double value)
    {
        _percentile = value;
        return this;
    }

    /// <summary>
    /// Sets the response-time threshold above which the component is degraded.
    /// Default: 500 ms.
    /// </summary>
    /// <param name="threshold">The degraded latency threshold.</param>
    /// <returns>This builder for chaining.</returns>
    public ResponseTimePolicyBuilder DegradedAfter(TimeSpan threshold)
    {
        _degradedAfter = threshold;
        return this;
    }

    /// <summary>
    /// Sets the response-time threshold above which the component is unhealthy.
    /// Must be greater than the degraded threshold.
    /// </summary>
    /// <param name="threshold">The unhealthy latency threshold.</param>
    /// <returns>This builder for chaining.</returns>
    public ResponseTimePolicyBuilder UnhealthyAfter(TimeSpan threshold)
    {
        _unhealthyAfter = threshold;
        return this;
    }

    /// <summary>
    /// Sets the minimum number of signals with latency data required before evaluation.
    /// Default: 5.
    /// </summary>
    /// <param name="count">The minimum signal count (≥ 1).</param>
    /// <returns>This builder for chaining.</returns>
    public ResponseTimePolicyBuilder MinimumSignals(int count)
    {
        _minimumSignals = count;
        return this;
    }

    /// <summary>
    /// Builds the configured <see cref="ResponseTimePolicy"/> from the current builder state.
    /// </summary>
    /// <returns>A fully-constructed <see cref="ResponseTimePolicy"/>.</returns>
    internal ResponseTimePolicy Build() => new(
        DegradedThreshold: _degradedAfter,
        Percentile: _percentile,
        UnhealthyThreshold: _unhealthyAfter,
        MinimumSignals: _minimumSignals);
}

// <copyright file="Configuration.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core.Contracts;

/// <summary>
/// Policy configuration that controls health evaluation thresholds and timing.
/// </summary>
/// <param name="SlidingWindow">Duration of the sliding window for signal evaluation.</param>
/// <param name="DegradedThreshold">Success rate below which the state becomes Degraded (0.0–1.0).</param>
/// <param name="CircuitOpenThreshold">Success rate below which the circuit opens (0.0–1.0).</param>
/// <param name="MinSignalsForEvaluation">Minimum number of signals required before evaluation.</param>
/// <param name="CooldownBeforeTransition">Minimum time between state transitions.</param>
/// <param name="RecoveryProbeInterval">Interval between recovery probe attempts.</param>
/// <param name="Jitter">Jitter configuration for transition delays.</param>
/// <param name="ResponseTime">Optional response-time (latency) policy for this dependency.</param>
public sealed record HealthPolicy(
    TimeSpan SlidingWindow,
    double DegradedThreshold,
    double CircuitOpenThreshold,
    int MinSignalsForEvaluation,
    TimeSpan CooldownBeforeTransition,
    TimeSpan RecoveryProbeInterval,
    JitterConfig Jitter,
    ResponseTimePolicy? ResponseTime = null);

/// <summary>
/// Latency-based policy evaluated alongside the primary success-rate policy.
/// When configured, the worst-of-both-dimensions determines the final health state.
/// </summary>
/// <param name="DegradedThreshold">Response time above which the dependency is considered degraded.</param>
/// <param name="Percentile">The percentile to evaluate (e.g. 0.95 for p95). Must be in (0.0, 1.0) exclusive.</param>
/// <param name="UnhealthyThreshold">Optional response time above which the dependency is considered unhealthy. Must be greater than <paramref name="DegradedThreshold"/>.</param>
/// <param name="MinimumSignals">Minimum number of signals with latency data required before evaluation. Must be ≥ 1.</param>
/// <param name="InsufficientDataState">Health state to return when signal count is below <paramref name="MinimumSignals"/>. Default: <see cref="HealthState.Healthy"/>.</param>
public sealed record ResponseTimePolicy(
    TimeSpan DegradedThreshold,
    double Percentile = 0.95,
    TimeSpan? UnhealthyThreshold = null,
    int MinimumSignals = 5,
    HealthState InsufficientDataState = HealthState.Healthy);

/// <summary>
/// Configuration for randomized jitter applied to transition delays.
/// </summary>
/// <param name="MinDelay">Minimum jitter delay.</param>
/// <param name="MaxDelay">Maximum jitter delay.</param>
public sealed record JitterConfig(TimeSpan MinDelay, TimeSpan MaxDelay);

/// <summary>
/// Configuration for the 3-gate shutdown safety chain.
/// </summary>
/// <param name="MinSignals">Gate 1: Minimum signals observed before allowing shutdown.</param>
/// <param name="Cooldown">Gate 2: Minimum time after the last state transition before allowing shutdown.</param>
/// <param name="RequireConfirmDelegate">Gate 3: Whether a caller-supplied confirmation delegate must approve.</param>
public sealed record ShutdownConfig(
    int MinSignals,
    TimeSpan Cooldown,
    bool RequireConfirmDelegate)
{
    /// <summary>
    /// Gets the conservative default configuration.
    /// MinSignals = 100, Cooldown = 60 s, RequireConfirmDelegate = <c>true</c>.
    /// </summary>
    public static ShutdownConfig Default { get; } = new(
        MinSignals: 100,
        Cooldown: TimeSpan.FromSeconds(60),
        RequireConfirmDelegate: true);
}

/// <summary>
/// Configuration for graceful drain behavior.
/// </summary>
/// <param name="Timeout">Maximum time allowed for drain to complete.</param>
/// <param name="DrainDelegate">Optional delegate invoked during drain with active connection count.</param>
public sealed record DrainConfig(
    TimeSpan Timeout,
    Func<int, CancellationToken, Task<bool>>? DrainDelegate);

/// <summary>
/// Configuration for tenant eviction in multi-tenant scenarios.
/// </summary>
/// <param name="MaxTenants">Maximum number of tenants to track.</param>
/// <param name="Ttl">Time-to-live for tenant entries.</param>
public sealed record TenantEvictionConfig(int MaxTenants, TimeSpan Ttl);



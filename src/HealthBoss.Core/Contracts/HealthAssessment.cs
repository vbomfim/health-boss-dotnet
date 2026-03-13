// <copyright file="HealthAssessment.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core.Contracts;

/// <summary>
/// The result of evaluating health signals against a policy.
/// Immutable snapshot of dependency health at a point in time.
/// </summary>
/// <param name="DependencyId">The dependency that was evaluated.</param>
/// <param name="SuccessRate">The ratio of successful signals (0.0–1.0).</param>
/// <param name="TotalSignals">Total number of signals in the evaluation window.</param>
/// <param name="FailureCount">Number of failed signals.</param>
/// <param name="SuccessCount">Number of successful signals.</param>
/// <param name="WindowDuration">The duration of the sliding window used.</param>
/// <param name="EvaluatedAt">When this assessment was generated.</param>
/// <param name="RecommendedState">The composite recommended health state (worst of success rate and latency).</param>
/// <param name="SuccessRateStatus">The health state from success-rate evaluation alone.</param>
/// <param name="ResponseTime">Latency assessment when a <see cref="ResponseTimePolicy"/> is configured; null otherwise.</param>
public sealed record HealthAssessment(
    DependencyId DependencyId,
    double SuccessRate,
    int TotalSignals,
    int FailureCount,
    int SuccessCount,
    TimeSpan WindowDuration,
    DateTimeOffset EvaluatedAt,
    HealthState RecommendedState,
    HealthState SuccessRateStatus,
    ResponseTimeAssessment? ResponseTime = null);

// <copyright file="ResponseTimeAssessment.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core.Contracts;

/// <summary>
/// The result of evaluating latency signals against a <see cref="ResponseTimePolicy"/>.
/// Immutable snapshot of latency health for a single evaluation window.
/// </summary>
/// <param name="SignalCount">Number of signals with non-null latency in the evaluation window.</param>
/// <param name="Evaluated">True if <paramref name="SignalCount"/> meets the policy's minimum; false when insufficient data.</param>
/// <param name="ConfiguredPercentile">The percentile used for evaluation (e.g., 0.95).</param>
/// <param name="P50">Median latency, or null when no latency signals exist.</param>
/// <param name="P95">95th-percentile latency, or null when no latency signals exist.</param>
/// <param name="P99">99th-percentile latency, or null when no latency signals exist.</param>
/// <param name="ThresholdValue">The latency value at <paramref name="ConfiguredPercentile"/>, or null when not evaluated.</param>
/// <param name="Status">The recommended health state from latency evaluation alone.</param>
/// <param name="Reason">Human-readable explanation of the assessment outcome.</param>
public sealed record ResponseTimeAssessment(
    int SignalCount,
    bool Evaluated,
    double ConfiguredPercentile,
    TimeSpan? P50,
    TimeSpan? P95,
    TimeSpan? P99,
    TimeSpan? ThresholdValue,
    HealthState Status,
    string? Reason);

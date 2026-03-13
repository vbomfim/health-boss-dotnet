// <copyright file="PolicyEvaluator.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Components;

/// <summary>
/// Pure-function evaluator: computes a <see cref="HealthAssessment"/>
/// from signals and policy with no side effects.
/// Supports two-dimensional evaluation when a <see cref="ResponseTimePolicy"/> is configured.
/// </summary>
internal sealed class PolicyEvaluator : IPolicyEvaluator
{
    /// <inheritdoc />
    public HealthAssessment Evaluate(
        IReadOnlyList<HealthSignal> signals,
        HealthPolicy policy,
        HealthState currentState,
        DateTimeOffset evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(policy);

        int totalSignals = signals.Count;
        int successCount = signals.Count(s => s.Outcome == SignalOutcome.Success);
        int failureCount = totalSignals - successCount;
        double successRate = totalSignals > 0
            ? (double)successCount / totalSignals
            : 0.0;

        HealthState successRateStatus = DetermineSuccessRateState(
            totalSignals, successRate, policy, currentState);

        // Evaluate latency dimension when ResponseTimePolicy is configured
        ResponseTimeAssessment? responseTime = policy.ResponseTime is not null
            ? EvaluateResponseTime(signals, policy.ResponseTime)
            : null;

        // Composite: worst-of-both dimensions
        HealthState recommendedState = responseTime is not null
            ? Worst(successRateStatus, responseTime.Status)
            : successRateStatus;

        // Use the first signal's DependencyId if available; otherwise default
        var dependencyId = signals.Count > 0
            ? signals[0].DependencyId
            : default;

        return new HealthAssessment(
            DependencyId: dependencyId,
            SuccessRate: successRate,
            TotalSignals: totalSignals,
            FailureCount: failureCount,
            SuccessCount: successCount,
            WindowDuration: policy.SlidingWindow,
            EvaluatedAt: evaluatedAt,
            RecommendedState: recommendedState,
            SuccessRateStatus: successRateStatus,
            ResponseTime: responseTime);
    }

    /// <summary>
    /// Evaluates latency signals against a <see cref="ResponseTimePolicy"/>.
    /// </summary>
    internal static ResponseTimeAssessment EvaluateResponseTime(
        IReadOnlyList<HealthSignal> signals,
        ResponseTimePolicy responseTimePolicy)
    {
        var latencies = PercentileCalculator.ExtractAndSortLatencies(signals);
        int signalCount = latencies.Length;
        var (p50, p95, p99) = latencies.Length > 0
            ? ComputePercentilesFromSorted(latencies)
            : (null, null, null);

        if (signalCount < responseTimePolicy.MinimumSignals)
        {
            return new ResponseTimeAssessment(
                SignalCount: signalCount,
                Evaluated: false,
                ConfiguredPercentile: responseTimePolicy.Percentile,
                P50: p50,
                P95: p95,
                P99: p99,
                ThresholdValue: null,
                Status: responseTimePolicy.InsufficientDataState,
                Reason: $"Insufficient latency signals ({signalCount}/{responseTimePolicy.MinimumSignals}).");
        }

        Span<TimeSpan> sorted = latencies.AsSpan();
        TimeSpan thresholdValue = PercentileCalculator.Compute(sorted, responseTimePolicy.Percentile);

        HealthState status = DetermineLatencyState(thresholdValue, responseTimePolicy);

        string reason = status switch
        {
            HealthState.Healthy => $"P{responseTimePolicy.Percentile * 100:F0} ({thresholdValue.TotalMilliseconds:F1}ms) within thresholds.",
            HealthState.Degraded => $"P{responseTimePolicy.Percentile * 100:F0} ({thresholdValue.TotalMilliseconds:F1}ms) exceeds degraded threshold ({responseTimePolicy.DegradedThreshold.TotalMilliseconds:F1}ms).",
            HealthState.CircuitOpen => $"P{responseTimePolicy.Percentile * 100:F0} ({thresholdValue.TotalMilliseconds:F1}ms) exceeds unhealthy threshold ({responseTimePolicy.UnhealthyThreshold!.Value.TotalMilliseconds:F1}ms).",
            _ => string.Empty,
        };

        return new ResponseTimeAssessment(
            SignalCount: signalCount,
            Evaluated: true,
            ConfiguredPercentile: responseTimePolicy.Percentile,
            P50: p50,
            P95: p95,
            P99: p99,
            ThresholdValue: thresholdValue,
            Status: status,
            Reason: reason);
    }

    private static (TimeSpan? P50, TimeSpan? P95, TimeSpan? P99) ComputePercentilesFromSorted(
        TimeSpan[] sortedLatencies)
    {
        Span<TimeSpan> sorted = sortedLatencies.AsSpan();
        return (
            PercentileCalculator.Compute(sorted, 0.50),
            PercentileCalculator.Compute(sorted, 0.95),
            PercentileCalculator.Compute(sorted, 0.99));
    }

    private static HealthState DetermineLatencyState(
        TimeSpan thresholdValue,
        ResponseTimePolicy policy)
    {
        if (policy.UnhealthyThreshold.HasValue && thresholdValue > policy.UnhealthyThreshold.Value)
        {
            return HealthState.CircuitOpen;
        }

        if (thresholdValue > policy.DegradedThreshold)
        {
            return HealthState.Degraded;
        }

        return HealthState.Healthy;
    }

    private static HealthState DetermineSuccessRateState(
        int totalSignals,
        double successRate,
        HealthPolicy policy,
        HealthState currentState)
    {
        if (totalSignals < policy.MinSignalsForEvaluation)
        {
            return currentState;
        }

        if (successRate >= policy.DegradedThreshold)
        {
            return HealthState.Healthy;
        }

        if (successRate >= policy.CircuitOpenThreshold)
        {
            return HealthState.Degraded;
        }

        return HealthState.CircuitOpen;
    }

    /// <summary>
    /// Returns the worse of two health states.
    /// Uses explicit pattern matching instead of enum ordinal comparison
    /// to avoid silent breakage if enum values are reordered.
    /// </summary>
    internal static HealthState Worst(HealthState a, HealthState b) => (a, b) switch
    {
        _ when a == HealthState.CircuitOpen || b == HealthState.CircuitOpen => HealthState.CircuitOpen,
        _ when a == HealthState.Degraded || b == HealthState.Degraded => HealthState.Degraded,
        _ => HealthState.Healthy,
    };
}

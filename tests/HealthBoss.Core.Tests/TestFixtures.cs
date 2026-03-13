using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Tests;

/// <summary>
/// Shared test helpers for creating signals, policies, and assessments.
/// </summary>
internal static class TestFixtures
{
    public static readonly DependencyId DefaultDependencyId = new("test-dependency");

    public static readonly DateTimeOffset BaseTime =
        new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

    public static HealthPolicy DefaultPolicy => new(
        SlidingWindow: TimeSpan.FromMinutes(5),
        DegradedThreshold: 0.9,
        CircuitOpenThreshold: 0.5,
        MinSignalsForEvaluation: 5,
        CooldownBeforeTransition: TimeSpan.FromSeconds(30),
        RecoveryProbeInterval: TimeSpan.FromSeconds(10),
        Jitter: new JitterConfig(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500)));

    public static HealthPolicy ZeroJitterPolicy => DefaultPolicy with
    {
        Jitter = new JitterConfig(TimeSpan.Zero, TimeSpan.Zero)
    };

    public static HealthSignal CreateSignal(
        SignalOutcome outcome = SignalOutcome.Success,
        DateTimeOffset? timestamp = null,
        DependencyId? dependencyId = null,
        TimeSpan? latency = null) => new(
        timestamp: timestamp ?? BaseTime,
        dependencyId: dependencyId ?? DefaultDependencyId,
        outcome: outcome,
        latency: latency ?? TimeSpan.FromMilliseconds(50));

    public static List<HealthSignal> CreateSignals(
        int successCount,
        int failureCount,
        DateTimeOffset? startTime = null,
        DependencyId? dependencyId = null)
    {
        var start = startTime ?? BaseTime;
        var depId = dependencyId ?? DefaultDependencyId;
        var signals = new List<HealthSignal>();

        for (int i = 0; i < successCount; i++)
        {
            signals.Add(CreateSignal(
                SignalOutcome.Success,
                start.AddSeconds(i),
                depId));
        }

        for (int i = 0; i < failureCount; i++)
        {
            signals.Add(CreateSignal(
                SignalOutcome.Failure,
                start.AddSeconds(successCount + i),
                depId));
        }

        return signals;
    }

    public static HealthAssessment CreateAssessment(
        HealthState recommendedState = HealthState.Healthy,
        double successRate = 1.0,
        int totalSignals = 10,
        int failureCount = 0,
        int successCount = 10,
        DateTimeOffset? evaluatedAt = null,
        HealthState? successRateStatus = null,
        ResponseTimeAssessment? responseTime = null) => new(
        DependencyId: DefaultDependencyId,
        SuccessRate: successRate,
        TotalSignals: totalSignals,
        FailureCount: failureCount,
        SuccessCount: successCount,
        WindowDuration: TimeSpan.FromMinutes(5),
        EvaluatedAt: evaluatedAt ?? BaseTime,
        RecommendedState: recommendedState,
        SuccessRateStatus: successRateStatus ?? recommendedState,
        ResponseTime: responseTime);

    /// <summary>
    /// Creates a signal with explicit null latency (no duration measured).
    /// Used for testing AC20: signals without Duration excluded from latency.
    /// </summary>
    public static HealthSignal CreateSignalWithoutLatency(
        SignalOutcome outcome = SignalOutcome.Success,
        DateTimeOffset? timestamp = null,
        DependencyId? dependencyId = null) => new(
        timestamp: timestamp ?? BaseTime,
        dependencyId: dependencyId ?? DefaultDependencyId,
        outcome: outcome,
        latency: null);

    /// <summary>
    /// Creates a list of signals with specific latency values.
    /// Useful for testing percentile calculations with known distributions.
    /// </summary>
    public static List<HealthSignal> CreateSignalsWithLatencies(
        IEnumerable<TimeSpan> latencies,
        DateTimeOffset? startTime = null,
        DependencyId? dependencyId = null)
    {
        var start = startTime ?? BaseTime;
        var depId = dependencyId ?? DefaultDependencyId;
        var signals = new List<HealthSignal>();
        int i = 0;

        foreach (var latency in latencies)
        {
            signals.Add(new HealthSignal(
                timestamp: start.AddSeconds(i++),
                dependencyId: depId,
                outcome: SignalOutcome.Success,
                latency: latency));
        }

        return signals;
    }

    /// <summary>
    /// Creates a default <see cref="ResponseTimePolicy"/> for testing.
    /// DegradedThreshold = 200ms, UnhealthyThreshold = 1000ms, P95, MinSignals = 5.
    /// </summary>
    public static ResponseTimePolicy DefaultResponseTimePolicy => new(
        DegradedThreshold: TimeSpan.FromMilliseconds(200),
        Percentile: 0.95,
        UnhealthyThreshold: TimeSpan.FromMilliseconds(1000),
        MinimumSignals: 5);

    /// <summary>
    /// Creates a list of signals with a mix of success and failure outcomes,
    /// all sharing the same latency value. Useful for two-dimensional evaluation tests
    /// where success rate and latency must be controlled independently.
    /// </summary>
    public static List<HealthSignal> CreateMixedSignals(
        int successCount,
        int failureCount,
        TimeSpan latency,
        DateTimeOffset? startTime = null,
        DependencyId? dependencyId = null)
    {
        var start = startTime ?? BaseTime;
        var depId = dependencyId ?? DefaultDependencyId;
        var signals = new List<HealthSignal>(successCount + failureCount);

        for (int i = 0; i < successCount; i++)
        {
            signals.Add(new HealthSignal(
                timestamp: start.AddSeconds(i),
                dependencyId: depId,
                outcome: SignalOutcome.Success,
                latency: latency));
        }

        for (int i = 0; i < failureCount; i++)
        {
            signals.Add(new HealthSignal(
                timestamp: start.AddSeconds(successCount + i),
                dependencyId: depId,
                outcome: SignalOutcome.Failure,
                latency: latency));
        }

        return signals;
    }
}

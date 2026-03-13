using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Tests;

/// <summary>
/// Tests for response-time (latency) evaluation, two-dimensional worst-of-both composition,
/// and all related acceptance criteria (AC16–AC22).
/// </summary>
public sealed class ResponseTimeEvaluationTests
{
    private readonly IPolicyEvaluator _evaluator = new PolicyEvaluator();

    // ─── AC16: Latency causes Degraded when success rate is Healthy ───────

    [Fact]
    public void AC16_latency_causes_degraded_when_success_rate_is_healthy()
    {
        // Arrange — all signals succeed (success rate = 100% → Healthy)
        // but p95 latency (250ms) exceeds DegradedThreshold (200ms)
        var latencies = Enumerable.Range(1, 20)
            .Select(i => TimeSpan.FromMilliseconds(i * 15)); // 15, 30, ..., 300ms

        var signals = TestFixtures.CreateSignalsWithLatencies(latencies);

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = new ResponseTimePolicy(
                DegradedThreshold: TimeSpan.FromMilliseconds(200),
                UnhealthyThreshold: TimeSpan.FromMilliseconds(1000),
                MinimumSignals: 5),
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert
        assessment.SuccessRateStatus.Should().Be(HealthState.Healthy);
        assessment.ResponseTime.Should().NotBeNull();
        assessment.ResponseTime!.Status.Should().Be(HealthState.Degraded);
        assessment.RecommendedState.Should().Be(HealthState.Degraded,
            "worst-of-both: Healthy (rate) + Degraded (latency) = Degraded");
    }

    // ─── AC17: Latency causes Unhealthy when UnhealthyThreshold exceeded ─

    [Fact]
    public void AC17_latency_causes_circuit_open_when_unhealthy_threshold_exceeded()
    {
        // Arrange — all signals succeed but with very high latency
        var latencies = Enumerable.Range(1, 20)
            .Select(i => TimeSpan.FromMilliseconds(i * 100)); // 100, 200, ..., 2000ms
        var signals = TestFixtures.CreateSignalsWithLatencies(latencies);

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = new ResponseTimePolicy(
                DegradedThreshold: TimeSpan.FromMilliseconds(200),
                UnhealthyThreshold: TimeSpan.FromMilliseconds(1000),
                MinimumSignals: 5),
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert — p95 of [100..2000] = 1900ms > UnhealthyThreshold (1000ms)
        assessment.ResponseTime.Should().NotBeNull();
        assessment.ResponseTime!.Status.Should().Be(HealthState.CircuitOpen);
        assessment.RecommendedState.Should().Be(HealthState.CircuitOpen);
    }

    // ─── AC18: Latency skipped when ResponseTimePolicy is null ────────────

    [Fact]
    public void AC18_latency_not_evaluated_when_policy_is_null()
    {
        // Arrange — default policy without ResponseTime
        var signals = TestFixtures.CreateSignals(10, 0);

        // Act
        var assessment = _evaluator.Evaluate(
            signals, TestFixtures.DefaultPolicy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert
        assessment.ResponseTime.Should().BeNull();
        assessment.RecommendedState.Should().Be(HealthState.Healthy);
        assessment.SuccessRateStatus.Should().Be(HealthState.Healthy);
    }

    // ─── AC19: Insufficient latency signals → InsufficientDataState ──────

    [Fact]
    public void AC19_insufficient_latency_signals_returns_insufficient_data_state()
    {
        // Arrange — only 3 signals with latency, but minimum is 5
        var signals = new List<HealthSignal>
        {
            TestFixtures.CreateSignal(latency: TimeSpan.FromMilliseconds(10)),
            TestFixtures.CreateSignal(latency: TimeSpan.FromMilliseconds(20)),
            TestFixtures.CreateSignal(latency: TimeSpan.FromMilliseconds(30)),
            TestFixtures.CreateSignalWithoutLatency(),
            TestFixtures.CreateSignalWithoutLatency(),
            TestFixtures.CreateSignalWithoutLatency(),
            TestFixtures.CreateSignalWithoutLatency(),
            TestFixtures.CreateSignalWithoutLatency(),
        };

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = new ResponseTimePolicy(
                DegradedThreshold: TimeSpan.FromMilliseconds(5),
                MinimumSignals: 5,
                InsufficientDataState: HealthState.Healthy),
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert
        assessment.ResponseTime.Should().NotBeNull();
        assessment.ResponseTime!.Evaluated.Should().BeFalse();
        assessment.ResponseTime.SignalCount.Should().Be(3);
        assessment.ResponseTime.Status.Should().Be(HealthState.Healthy);
        assessment.ResponseTime.Reason.Should().Contain("Insufficient");
    }

    [Fact]
    public void AC19_insufficient_data_uses_configured_fallback_state()
    {
        // Arrange — InsufficientDataState = Degraded
        var signals = new List<HealthSignal>
        {
            TestFixtures.CreateSignal(latency: TimeSpan.FromMilliseconds(10)),
        };

        var policy = TestFixtures.DefaultPolicy with
        {
            MinSignalsForEvaluation = 1,
            ResponseTime = new ResponseTimePolicy(
                DegradedThreshold: TimeSpan.FromMilliseconds(100),
                MinimumSignals: 5,
                InsufficientDataState: HealthState.Degraded),
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert
        assessment.ResponseTime!.Status.Should().Be(HealthState.Degraded);
        assessment.ResponseTime.Evaluated.Should().BeFalse();
    }

    // ─── AC20: Signals without Duration excluded from latency ─────────────

    [Fact]
    public void AC20_signals_without_latency_excluded_from_latency_but_included_in_success_rate()
    {
        // Arrange — 8 signals total, only 5 have latency; 2 failures (no latency)
        var signals = new List<HealthSignal>
        {
            // 5 success signals with latency (all fast — 10ms)
            TestFixtures.CreateSignal(
                outcome: SignalOutcome.Success,
                latency: TimeSpan.FromMilliseconds(10),
                timestamp: TestFixtures.BaseTime.AddSeconds(1)),
            TestFixtures.CreateSignal(
                outcome: SignalOutcome.Success,
                latency: TimeSpan.FromMilliseconds(10),
                timestamp: TestFixtures.BaseTime.AddSeconds(2)),
            TestFixtures.CreateSignal(
                outcome: SignalOutcome.Success,
                latency: TimeSpan.FromMilliseconds(10),
                timestamp: TestFixtures.BaseTime.AddSeconds(3)),
            TestFixtures.CreateSignal(
                outcome: SignalOutcome.Success,
                latency: TimeSpan.FromMilliseconds(10),
                timestamp: TestFixtures.BaseTime.AddSeconds(4)),
            TestFixtures.CreateSignal(
                outcome: SignalOutcome.Success,
                latency: TimeSpan.FromMilliseconds(10),
                timestamp: TestFixtures.BaseTime.AddSeconds(5)),

            // 1 success without latency
            TestFixtures.CreateSignalWithoutLatency(
                outcome: SignalOutcome.Success,
                timestamp: TestFixtures.BaseTime.AddSeconds(6)),

            // 2 failures without latency
            TestFixtures.CreateSignalWithoutLatency(
                outcome: SignalOutcome.Failure,
                timestamp: TestFixtures.BaseTime.AddSeconds(7)),
            TestFixtures.CreateSignalWithoutLatency(
                outcome: SignalOutcome.Failure,
                timestamp: TestFixtures.BaseTime.AddSeconds(8)),
        };

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = new ResponseTimePolicy(
                DegradedThreshold: TimeSpan.FromMilliseconds(200),
                MinimumSignals: 5),
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert — success rate counts ALL 8 signals: 6/8 = 75% → Degraded
        assessment.TotalSignals.Should().Be(8);
        assessment.SuccessCount.Should().Be(6);
        assessment.SuccessRate.Should().Be(0.75);

        // Latency only counts 5 signals (those with latency)
        assessment.ResponseTime.Should().NotBeNull();
        assessment.ResponseTime!.SignalCount.Should().Be(5);
        assessment.ResponseTime.Evaluated.Should().BeTrue();
    }

    // ─── AC21: Worst-of-both composition (9 combinations) ────────────────

    [Theory]
    [MemberData(nameof(WorstOfBothCombinations))]
    public void AC21_worst_of_both_composition(
        HealthState successRateStatus,
        HealthState latencyStatus,
        HealthState expectedComposite)
    {
        // Act
        var result = PolicyEvaluator.Worst(successRateStatus, latencyStatus);

        // Assert
        result.Should().Be(expectedComposite);
    }

    public static TheoryData<HealthState, HealthState, HealthState> WorstOfBothCombinations => new()
    {
        // successRate × latency → composite
        { HealthState.Healthy,     HealthState.Healthy,     HealthState.Healthy },
        { HealthState.Healthy,     HealthState.Degraded,    HealthState.Degraded },
        { HealthState.Healthy,     HealthState.CircuitOpen, HealthState.CircuitOpen },
        { HealthState.Degraded,    HealthState.Healthy,     HealthState.Degraded },
        { HealthState.Degraded,    HealthState.Degraded,    HealthState.Degraded },
        { HealthState.Degraded,    HealthState.CircuitOpen, HealthState.CircuitOpen },
        { HealthState.CircuitOpen, HealthState.Healthy,     HealthState.CircuitOpen },
        { HealthState.CircuitOpen, HealthState.Degraded,    HealthState.CircuitOpen },
        { HealthState.CircuitOpen, HealthState.CircuitOpen, HealthState.CircuitOpen },
    };

    // ─── AC22: Percentile configurable (p50 vs p99) ──────────────────────

    [Fact]
    public void AC22_percentile_configurable_p50_triggers_degraded()
    {
        // Arrange — latencies where p50 > threshold but p95 would be below a higher threshold
        // 20 signals: first 10 at 10ms, last 10 at 300ms
        var latencies = Enumerable.Repeat(TimeSpan.FromMilliseconds(10), 10)
            .Concat(Enumerable.Repeat(TimeSpan.FromMilliseconds(300), 10));
        var signals = TestFixtures.CreateSignalsWithLatencies(latencies);

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = new ResponseTimePolicy(
                DegradedThreshold: TimeSpan.FromMilliseconds(250),
                Percentile: 0.50,
                MinimumSignals: 5),
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert — p50 of [10×10, 10×300] = rank⌈0.5×20⌉=10 → 10ms (sorted, index 9 = 10ms)
        // Actually: sorted = [10,10,...,10, 300,...,300] (10 tens, 10 three-hundreds)
        // rank = ⌈0.5 × 20⌉ = 10, index = 9 → values[9] = 10ms (the last of the 10ms batch)
        // So p50 = 10ms, which is below DegradedThreshold (250ms) → Healthy
        assessment.ResponseTime!.Status.Should().Be(HealthState.Healthy);
        assessment.ResponseTime.ConfiguredPercentile.Should().Be(0.50);
    }

    [Fact]
    public void AC22_percentile_configurable_p99_triggers_degraded()
    {
        // Arrange — 100 signals: 99 at 10ms, 1 at 500ms
        var latencies = Enumerable.Repeat(TimeSpan.FromMilliseconds(10), 99)
            .Concat(Enumerable.Repeat(TimeSpan.FromMilliseconds(500), 1));
        var signals = TestFixtures.CreateSignalsWithLatencies(latencies);

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = new ResponseTimePolicy(
                DegradedThreshold: TimeSpan.FromMilliseconds(200),
                Percentile: 0.99,
                MinimumSignals: 5),
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert — p99 = rank⌈0.99 × 100⌉ = 99 → index 98 → 10ms
        // (99 values of 10ms at indices 0-98, 1 value of 500ms at index 99)
        // So p99 = 10ms < 200ms → Healthy
        assessment.ResponseTime!.Status.Should().Be(HealthState.Healthy);
        assessment.ResponseTime.ConfiguredPercentile.Should().Be(0.99);
    }

    [Fact]
    public void AC22_high_p99_with_many_slow_requests_triggers_degraded()
    {
        // Arrange — 100 signals: 90 at 10ms, 10 at 500ms
        var latencies = Enumerable.Repeat(TimeSpan.FromMilliseconds(10), 90)
            .Concat(Enumerable.Repeat(TimeSpan.FromMilliseconds(500), 10));
        var signals = TestFixtures.CreateSignalsWithLatencies(latencies);

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = new ResponseTimePolicy(
                DegradedThreshold: TimeSpan.FromMilliseconds(200),
                Percentile: 0.99,
                MinimumSignals: 5),
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert — p99 = rank⌈0.99 × 100⌉ = 99 → index 98 → 500ms > 200ms → Degraded
        assessment.ResponseTime!.Status.Should().Be(HealthState.Degraded);
    }

    // ─── Latency-only Degraded (no Unhealthy threshold, AC20 in spec) ────

    [Fact]
    public void Latency_only_degraded_when_no_unhealthy_threshold()
    {
        // Arrange — very high latency but UnhealthyThreshold is null
        var latencies = Enumerable.Repeat(TimeSpan.FromMilliseconds(5000), 10);
        var signals = TestFixtures.CreateSignalsWithLatencies(latencies);

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = new ResponseTimePolicy(
                DegradedThreshold: TimeSpan.FromMilliseconds(200),
                UnhealthyThreshold: null,
                MinimumSignals: 5),
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert — cannot become CircuitOpen from latency alone
        assessment.ResponseTime!.Status.Should().Be(HealthState.Degraded);
        assessment.ResponseTime.ThresholdValue.Should().Be(TimeSpan.FromMilliseconds(5000));
    }

    // ─── End-to-end two-dimensional evaluation ───────────────────────────

    [Fact]
    public void TwoDimensional_healthy_rate_and_healthy_latency()
    {
        // Arrange — all success, low latency
        var latencies = Enumerable.Repeat(TimeSpan.FromMilliseconds(10), 10);
        var signals = TestFixtures.CreateSignalsWithLatencies(latencies);

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = TestFixtures.DefaultResponseTimePolicy,
        };

        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        assessment.SuccessRateStatus.Should().Be(HealthState.Healthy);
        assessment.ResponseTime!.Status.Should().Be(HealthState.Healthy);
        assessment.RecommendedState.Should().Be(HealthState.Healthy);
    }

    [Fact]
    public void TwoDimensional_degraded_rate_and_degraded_latency()
    {
        // Arrange — 80% success rate (Degraded) + high latency (Degraded)
        var signals = new List<HealthSignal>();
        for (int i = 0; i < 8; i++)
        {
            signals.Add(TestFixtures.CreateSignal(
                outcome: SignalOutcome.Success,
                latency: TimeSpan.FromMilliseconds(300),
                timestamp: TestFixtures.BaseTime.AddSeconds(i)));
        }

        for (int i = 0; i < 2; i++)
        {
            signals.Add(TestFixtures.CreateSignal(
                outcome: SignalOutcome.Failure,
                latency: TimeSpan.FromMilliseconds(300),
                timestamp: TestFixtures.BaseTime.AddSeconds(8 + i)));
        }

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = TestFixtures.DefaultResponseTimePolicy,
        };

        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // 80% success rate with Degraded threshold at 0.9 → Degraded
        assessment.SuccessRateStatus.Should().Be(HealthState.Degraded);
        // p95 of all 300ms = 300ms > 200ms threshold → Degraded
        assessment.ResponseTime!.Status.Should().Be(HealthState.Degraded);
        // worst-of-both: Degraded + Degraded = Degraded
        assessment.RecommendedState.Should().Be(HealthState.Degraded);
    }

    [Fact]
    public void TwoDimensional_circuit_open_rate_wins_over_healthy_latency()
    {
        // Arrange — 30% success rate (CircuitOpen) + low latency (Healthy)
        var signals = new List<HealthSignal>();
        for (int i = 0; i < 3; i++)
        {
            signals.Add(TestFixtures.CreateSignal(
                outcome: SignalOutcome.Success,
                latency: TimeSpan.FromMilliseconds(10),
                timestamp: TestFixtures.BaseTime.AddSeconds(i)));
        }

        for (int i = 0; i < 7; i++)
        {
            signals.Add(TestFixtures.CreateSignal(
                outcome: SignalOutcome.Failure,
                latency: TimeSpan.FromMilliseconds(10),
                timestamp: TestFixtures.BaseTime.AddSeconds(3 + i)));
        }

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = TestFixtures.DefaultResponseTimePolicy,
        };

        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // 30% < CircuitOpen threshold (0.5)
        assessment.SuccessRateStatus.Should().Be(HealthState.CircuitOpen);
        // Low latency → Healthy
        assessment.ResponseTime!.Status.Should().Be(HealthState.Healthy);
        // worst-of-both: CircuitOpen + Healthy = CircuitOpen
        assessment.RecommendedState.Should().Be(HealthState.CircuitOpen);
    }

    // ─── ResponseTimeAssessment properties ───────────────────────────────

    [Fact]
    public void ResponseTimeAssessment_populated_with_all_percentiles()
    {
        // Arrange — 100 signals with known distribution
        var latencies = Enumerable.Range(1, 100)
            .Select(i => TimeSpan.FromMilliseconds(i));
        var signals = TestFixtures.CreateSignalsWithLatencies(latencies);

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = TestFixtures.DefaultResponseTimePolicy,
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert
        var rt = assessment.ResponseTime!;
        rt.SignalCount.Should().Be(100);
        rt.Evaluated.Should().BeTrue();
        rt.ConfiguredPercentile.Should().Be(0.95);
        rt.P50.Should().Be(TimeSpan.FromMilliseconds(50));
        rt.P95.Should().Be(TimeSpan.FromMilliseconds(95));
        rt.P99.Should().Be(TimeSpan.FromMilliseconds(99));
        rt.ThresholdValue.Should().Be(TimeSpan.FromMilliseconds(95));
        rt.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ResponseTimeAssessment_threshold_value_matches_configured_percentile()
    {
        // Arrange — configure p50 instead of p95
        var latencies = Enumerable.Range(1, 100)
            .Select(i => TimeSpan.FromMilliseconds(i));
        var signals = TestFixtures.CreateSignalsWithLatencies(latencies);

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = new ResponseTimePolicy(
                DegradedThreshold: TimeSpan.FromMilliseconds(200),
                Percentile: 0.50,
                MinimumSignals: 5),
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert — ThresholdValue should be p50, not p95
        assessment.ResponseTime!.ThresholdValue.Should().Be(TimeSpan.FromMilliseconds(50));
        assessment.ResponseTime.ConfiguredPercentile.Should().Be(0.50);
    }

    // ─── SuccessRateStatus preserved independently ───────────────────────

    [Fact]
    public void SuccessRateStatus_reflects_rate_only_independent_of_latency()
    {
        // Arrange — Healthy success rate, Degraded latency
        var latencies = Enumerable.Repeat(TimeSpan.FromMilliseconds(300), 10);
        var signals = TestFixtures.CreateSignalsWithLatencies(latencies);

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = TestFixtures.DefaultResponseTimePolicy,
        };

        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Success rate = 100% → Healthy
        assessment.SuccessRateStatus.Should().Be(HealthState.Healthy);
        // Latency = 300ms > 200ms → Degraded
        assessment.ResponseTime!.Status.Should().Be(HealthState.Degraded);
        // But SuccessRateStatus is only about the rate dimension
        assessment.SuccessRateStatus.Should().NotBe(assessment.RecommendedState);
    }

    // ─── Boundary: exactly at threshold ──────────────────────────────────

    [Fact]
    public void Latency_exactly_at_degraded_threshold_stays_healthy()
    {
        // Arrange — p95 == DegradedThreshold (not greater than → Healthy)
        var latencies = Enumerable.Repeat(TimeSpan.FromMilliseconds(200), 10);
        var signals = TestFixtures.CreateSignalsWithLatencies(latencies);

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = new ResponseTimePolicy(
                DegradedThreshold: TimeSpan.FromMilliseconds(200),
                MinimumSignals: 5),
        };

        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // 200ms is NOT > 200ms, so stays Healthy
        assessment.ResponseTime!.Status.Should().Be(HealthState.Healthy);
    }

    [Fact]
    public void Latency_exactly_at_unhealthy_threshold_stays_degraded()
    {
        // Arrange — p95 == UnhealthyThreshold (not greater than → Degraded, not CircuitOpen)
        var latencies = Enumerable.Repeat(TimeSpan.FromMilliseconds(1000), 10);
        var signals = TestFixtures.CreateSignalsWithLatencies(latencies);

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = new ResponseTimePolicy(
                DegradedThreshold: TimeSpan.FromMilliseconds(200),
                UnhealthyThreshold: TimeSpan.FromMilliseconds(1000),
                MinimumSignals: 5),
        };

        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // 1000ms > 200ms (Degraded), but 1000ms is NOT > 1000ms (stays Degraded)
        assessment.ResponseTime!.Status.Should().Be(HealthState.Degraded);
    }
}

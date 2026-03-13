// <copyright file="TwoDimensionalEvaluationTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Tests.Integration;

/// <summary>
/// Integration tests for two-dimensional health evaluation (success rate × latency).
/// Verifies all 9 status combinations and edge cases for the worst-of-both composition
/// through the full <see cref="PolicyEvaluator.Evaluate"/> pipeline.
/// </summary>
/// <remarks>
/// Acceptance criteria from issue #11.
/// Policy defaults used throughout:
///   Success rate — DegradedThreshold = 0.9, CircuitOpenThreshold = 0.5
///   Latency — DegradedThreshold = 200 ms, UnhealthyThreshold = 1000 ms, P95, MinSignals = 5.
/// </remarks>
public sealed class TwoDimensionalEvaluationTests
{
    private readonly IPolicyEvaluator _evaluator = new PolicyEvaluator();

    // ─── All 9 combinations (success rate × latency → composite) ─────────

    /// <summary>
    /// Verifies every cell of the 3×3 matrix (Healthy/Degraded/CircuitOpen for each dimension)
    /// produces the correct worst-of-both composite through the full Evaluate pipeline.
    /// </summary>
    [Theory]
    [MemberData(nameof(NineCombinationMatrix))]
    public void Evaluate_all_nine_combinations_produce_correct_composite(
        int successCount,
        int failureCount,
        TimeSpan latency,
        HealthState expectedSuccessRate,
        HealthState expectedLatency,
        HealthState expectedComposite,
        string scenario)
    {
        // Arrange — uniform latency so p95 == latency value
        var signals = TestFixtures.CreateMixedSignals(successCount, failureCount, latency);

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = TestFixtures.DefaultResponseTimePolicy,
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert — each dimension independently, then the composite
        assessment.SuccessRateStatus.Should().Be(expectedSuccessRate,
            $"success rate dimension should be {expectedSuccessRate} ({scenario})");
        assessment.ResponseTime.Should().NotBeNull(scenario);
        assessment.ResponseTime!.Status.Should().Be(expectedLatency,
            $"latency dimension should be {expectedLatency} ({scenario})");
        assessment.RecommendedState.Should().Be(expectedComposite,
            $"composite worst-of-both should be {expectedComposite} ({scenario})");
    }

    /// <summary>
    /// 3 × 3 matrix: success-rate status × latency status → expected composite.
    /// Healthy rate = 100 % (10/10), Degraded rate = 80 % (8/10), CircuitOpen rate = 30 % (3/10).
    /// Healthy latency = 50 ms, Degraded latency = 500 ms, CircuitOpen latency = 2000 ms.
    /// </summary>
    public static TheoryData<int, int, TimeSpan, HealthState, HealthState, HealthState, string>
        NineCombinationMatrix => new()
    {
        { 10, 0, TimeSpan.FromMilliseconds(50),   HealthState.Healthy,     HealthState.Healthy,     HealthState.Healthy,     "Healthy + Healthy → Healthy" },
        { 10, 0, TimeSpan.FromMilliseconds(500),  HealthState.Healthy,     HealthState.Degraded,    HealthState.Degraded,    "Healthy + Degraded → Degraded" },
        { 10, 0, TimeSpan.FromMilliseconds(2000), HealthState.Healthy,     HealthState.CircuitOpen, HealthState.CircuitOpen, "Healthy + CircuitOpen → CircuitOpen" },
        {  8, 2, TimeSpan.FromMilliseconds(50),   HealthState.Degraded,    HealthState.Healthy,     HealthState.Degraded,    "Degraded + Healthy → Degraded" },
        {  8, 2, TimeSpan.FromMilliseconds(500),  HealthState.Degraded,    HealthState.Degraded,    HealthState.Degraded,    "Degraded + Degraded → Degraded" },
        {  8, 2, TimeSpan.FromMilliseconds(2000), HealthState.Degraded,    HealthState.CircuitOpen, HealthState.CircuitOpen, "Degraded + CircuitOpen → CircuitOpen" },
        {  3, 7, TimeSpan.FromMilliseconds(50),   HealthState.CircuitOpen, HealthState.Healthy,     HealthState.CircuitOpen, "CircuitOpen + Healthy → CircuitOpen" },
        {  3, 7, TimeSpan.FromMilliseconds(500),  HealthState.CircuitOpen, HealthState.Degraded,    HealthState.CircuitOpen, "CircuitOpen + Degraded → CircuitOpen" },
        {  3, 7, TimeSpan.FromMilliseconds(2000), HealthState.CircuitOpen, HealthState.CircuitOpen, HealthState.CircuitOpen, "CircuitOpen + CircuitOpen → CircuitOpen" },
    };

    // ─── Both dimensions at exact threshold boundaries ───────────────────

    [Fact]
    public void Both_dimensions_at_degraded_threshold_boundary_stay_healthy()
    {
        // Arrange — success rate = 9/10 = 0.9 (exactly at DegradedThreshold → Healthy)
        //           latency = 200 ms (exactly at DegradedThreshold → Healthy)
        var signals = TestFixtures.CreateMixedSignals(
            successCount: 9, failureCount: 1,
            latency: TimeSpan.FromMilliseconds(200));

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = TestFixtures.DefaultResponseTimePolicy,
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert — both use >= / > semantics: "at threshold" stays in lower state
        assessment.SuccessRateStatus.Should().Be(HealthState.Healthy,
            "success rate >= DegradedThreshold (0.9) → Healthy");
        assessment.ResponseTime!.Status.Should().Be(HealthState.Healthy,
            "latency not > DegradedThreshold (200 ms) → Healthy");
        assessment.RecommendedState.Should().Be(HealthState.Healthy);
    }

    [Fact]
    public void Both_dimensions_at_circuit_open_threshold_boundary_stay_degraded()
    {
        // Arrange — success rate = 5/10 = 0.5 (exactly at CircuitOpenThreshold → Degraded)
        //           latency = 1000 ms (exactly at UnhealthyThreshold → Degraded)
        var signals = TestFixtures.CreateMixedSignals(
            successCount: 5, failureCount: 5,
            latency: TimeSpan.FromMilliseconds(1000));

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = TestFixtures.DefaultResponseTimePolicy,
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert — at threshold but not exceeding, both stay Degraded
        assessment.SuccessRateStatus.Should().Be(HealthState.Degraded,
            "success rate >= CircuitOpenThreshold (0.5) but < DegradedThreshold → Degraded");
        assessment.ResponseTime!.Status.Should().Be(HealthState.Degraded,
            "latency > DegradedThreshold (200 ms) but not > UnhealthyThreshold (1000 ms) → Degraded");
        assessment.RecommendedState.Should().Be(HealthState.Degraded);
    }

    // ─── Latency insufficient data does not override success rate ─────────

    [Fact]
    public void Latency_insufficient_data_with_degraded_success_rate_results_in_degraded()
    {
        // Arrange — 80% success rate → Degraded
        //           only 3 latency signals < MinimumSignals (5) → InsufficientDataState (Healthy)
        //           Worst(Degraded, Healthy) = Degraded
        var signals = new List<HealthSignal>();

        // 3 success signals WITH latency
        for (int i = 0; i < 3; i++)
        {
            signals.Add(TestFixtures.CreateSignal(
                outcome: SignalOutcome.Success,
                latency: TimeSpan.FromMilliseconds(10),
                timestamp: TestFixtures.BaseTime.AddSeconds(i)));
        }

        // 5 success signals WITHOUT latency
        for (int i = 0; i < 5; i++)
        {
            signals.Add(TestFixtures.CreateSignalWithoutLatency(
                outcome: SignalOutcome.Success,
                timestamp: TestFixtures.BaseTime.AddSeconds(3 + i)));
        }

        // 2 failure signals WITHOUT latency
        for (int i = 0; i < 2; i++)
        {
            signals.Add(TestFixtures.CreateSignalWithoutLatency(
                outcome: SignalOutcome.Failure,
                timestamp: TestFixtures.BaseTime.AddSeconds(8 + i)));
        }

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = TestFixtures.DefaultResponseTimePolicy,
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert
        assessment.TotalSignals.Should().Be(10);
        assessment.SuccessRate.Should().Be(0.8);
        assessment.SuccessRateStatus.Should().Be(HealthState.Degraded);
        assessment.ResponseTime!.Evaluated.Should().BeFalse();
        assessment.ResponseTime.SignalCount.Should().Be(3);
        assessment.ResponseTime.Status.Should().Be(HealthState.Healthy,
            "InsufficientDataState defaults to Healthy");
        assessment.RecommendedState.Should().Be(HealthState.Degraded,
            "Worst(Degraded, Healthy) = Degraded — latency insufficient data does not mask success rate");
    }

    [Fact]
    public void Latency_insufficient_data_with_healthy_success_rate_results_in_healthy()
    {
        // Arrange — 100% success rate → Healthy
        //           only 3 latency signals → InsufficientDataState (Healthy)
        //           Worst(Healthy, Healthy) = Healthy
        var signals = new List<HealthSignal>();

        // 3 success signals WITH latency
        for (int i = 0; i < 3; i++)
        {
            signals.Add(TestFixtures.CreateSignal(
                outcome: SignalOutcome.Success,
                latency: TimeSpan.FromMilliseconds(10),
                timestamp: TestFixtures.BaseTime.AddSeconds(i)));
        }

        // 7 success signals WITHOUT latency
        for (int i = 0; i < 7; i++)
        {
            signals.Add(TestFixtures.CreateSignalWithoutLatency(
                outcome: SignalOutcome.Success,
                timestamp: TestFixtures.BaseTime.AddSeconds(3 + i)));
        }

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = TestFixtures.DefaultResponseTimePolicy,
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert
        assessment.SuccessRateStatus.Should().Be(HealthState.Healthy);
        assessment.ResponseTime!.Evaluated.Should().BeFalse();
        assessment.ResponseTime.Status.Should().Be(HealthState.Healthy);
        assessment.RecommendedState.Should().Be(HealthState.Healthy);
    }

    [Fact]
    public void Latency_insufficient_data_configured_as_degraded_escalates_composite()
    {
        // Arrange — 100% success rate → Healthy
        //           InsufficientDataState = Degraded
        //           Worst(Healthy, Degraded) = Degraded
        var signals = new List<HealthSignal>();

        // 3 success signals WITH latency (insufficient for MinimumSignals = 5)
        for (int i = 0; i < 3; i++)
        {
            signals.Add(TestFixtures.CreateSignal(
                outcome: SignalOutcome.Success,
                latency: TimeSpan.FromMilliseconds(10),
                timestamp: TestFixtures.BaseTime.AddSeconds(i)));
        }

        // 7 success signals WITHOUT latency
        for (int i = 0; i < 7; i++)
        {
            signals.Add(TestFixtures.CreateSignalWithoutLatency(
                outcome: SignalOutcome.Success,
                timestamp: TestFixtures.BaseTime.AddSeconds(3 + i)));
        }

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = new ResponseTimePolicy(
                DegradedThreshold: TimeSpan.FromMilliseconds(200),
                UnhealthyThreshold: TimeSpan.FromMilliseconds(1000),
                MinimumSignals: 5,
                InsufficientDataState: HealthState.Degraded),
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert — InsufficientDataState = Degraded escalates composite
        assessment.SuccessRateStatus.Should().Be(HealthState.Healthy);
        assessment.ResponseTime!.Status.Should().Be(HealthState.Degraded);
        assessment.RecommendedState.Should().Be(HealthState.Degraded,
            "Worst(Healthy, Degraded) = Degraded — InsufficientDataState escalates");
    }

    // ─── Zero signals → InsufficientData for both dimensions ─────────────

    [Fact]
    public void Zero_signals_with_response_time_policy_returns_insufficient_data_for_both()
    {
        // Arrange — no signals at all
        var signals = new List<HealthSignal>();

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = TestFixtures.DefaultResponseTimePolicy,
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert — success rate: 0 signals < MinSignalsForEvaluation → currentState (Healthy)
        //          latency: 0 signals < MinimumSignals → InsufficientDataState (Healthy)
        assessment.TotalSignals.Should().Be(0);
        assessment.SuccessRate.Should().Be(0.0);
        assessment.SuccessRateStatus.Should().Be(HealthState.Healthy,
            "insufficient signals falls back to currentState");
        assessment.ResponseTime.Should().NotBeNull();
        assessment.ResponseTime!.Evaluated.Should().BeFalse();
        assessment.ResponseTime.SignalCount.Should().Be(0);
        assessment.ResponseTime.Status.Should().Be(HealthState.Healthy,
            "InsufficientDataState defaults to Healthy");
        assessment.RecommendedState.Should().Be(HealthState.Healthy);
    }

    [Fact]
    public void Zero_signals_with_degraded_current_state_preserves_degraded()
    {
        // Arrange — no signals, currentState = Degraded
        var signals = new List<HealthSignal>();

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = TestFixtures.DefaultResponseTimePolicy,
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Degraded, TestFixtures.BaseTime);

        // Assert — success rate: currentState = Degraded (pass through)
        //          latency: InsufficientDataState = Healthy
        //          Worst(Degraded, Healthy) = Degraded
        assessment.SuccessRateStatus.Should().Be(HealthState.Degraded);
        assessment.ResponseTime!.Status.Should().Be(HealthState.Healthy);
        assessment.RecommendedState.Should().Be(HealthState.Degraded);
    }

    // ─── 100 signals all successful but high p95 → Degraded ──────────────

    [Fact]
    public void Hundred_successful_signals_with_high_p95_latency_results_in_degraded()
    {
        // Arrange — 100 success signals, all at 2000 ms
        //           success rate = 100% → Healthy
        //           p95 = 2000 ms > DegradedThreshold (500 ms) → Degraded
        var latencies = Enumerable.Repeat(TimeSpan.FromMilliseconds(2000), 100);
        var signals = TestFixtures.CreateSignalsWithLatencies(latencies);

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = new ResponseTimePolicy(
                DegradedThreshold: TimeSpan.FromMilliseconds(500),
                Percentile: 0.95,
                MinimumSignals: 5),
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert
        assessment.SuccessRateStatus.Should().Be(HealthState.Healthy);
        assessment.ResponseTime!.Status.Should().Be(HealthState.Degraded,
            "p95 (2000 ms) exceeds DegradedThreshold (500 ms)");
        assessment.RecommendedState.Should().Be(HealthState.Degraded,
            "Worst(Healthy, Degraded) = Degraded");
        assessment.ResponseTime.ThresholdValue.Should().Be(TimeSpan.FromMilliseconds(2000));
    }

    // ─── Dimension independence verification ─────────────────────────────

    [Fact]
    public void Success_rate_status_stays_independent_when_latency_is_worse()
    {
        // Arrange — Healthy success rate (100%), CircuitOpen latency (2000 ms > 1000 ms)
        var signals = TestFixtures.CreateMixedSignals(
            successCount: 10, failureCount: 0,
            latency: TimeSpan.FromMilliseconds(2000));

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = TestFixtures.DefaultResponseTimePolicy,
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert — SuccessRateStatus stays Healthy even though composite is CircuitOpen
        assessment.SuccessRateStatus.Should().Be(HealthState.Healthy);
        assessment.ResponseTime!.Status.Should().Be(HealthState.CircuitOpen);
        assessment.RecommendedState.Should().Be(HealthState.CircuitOpen);
        assessment.SuccessRateStatus.Should().NotBe(assessment.RecommendedState,
            "SuccessRateStatus reflects only the success rate dimension");
    }

    [Fact]
    public void Latency_status_stays_independent_when_success_rate_is_worse()
    {
        // Arrange — CircuitOpen success rate (30%), Healthy latency (50 ms)
        var signals = TestFixtures.CreateMixedSignals(
            successCount: 3, failureCount: 7,
            latency: TimeSpan.FromMilliseconds(50));

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = TestFixtures.DefaultResponseTimePolicy,
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert — ResponseTime.Status stays Healthy even though composite is CircuitOpen
        assessment.SuccessRateStatus.Should().Be(HealthState.CircuitOpen);
        assessment.ResponseTime!.Status.Should().Be(HealthState.Healthy);
        assessment.RecommendedState.Should().Be(HealthState.CircuitOpen);
        assessment.ResponseTime.Status.Should().NotBe(assessment.RecommendedState,
            "ResponseTime.Status reflects only the latency dimension");
    }

    // ─── Composite assessment metadata ───────────────────────────────────

    [Fact]
    public void Composite_assessment_contains_both_dimension_details()
    {
        // Arrange — Degraded success rate + Degraded latency
        var signals = TestFixtures.CreateMixedSignals(
            successCount: 8, failureCount: 2,
            latency: TimeSpan.FromMilliseconds(500));

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = TestFixtures.DefaultResponseTimePolicy,
        };

        // Act
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert — all metadata present and correct
        assessment.TotalSignals.Should().Be(10);
        assessment.SuccessCount.Should().Be(8);
        assessment.FailureCount.Should().Be(2);
        assessment.SuccessRate.Should().Be(0.8);
        assessment.DependencyId.Should().Be(TestFixtures.DefaultDependencyId);
        assessment.WindowDuration.Should().Be(TestFixtures.DefaultPolicy.SlidingWindow);
        assessment.EvaluatedAt.Should().Be(TestFixtures.BaseTime);

        // Response time details
        var rt = assessment.ResponseTime!;
        rt.SignalCount.Should().Be(10);
        rt.Evaluated.Should().BeTrue();
        rt.ConfiguredPercentile.Should().Be(0.95);
        rt.P50.Should().NotBeNull();
        rt.P95.Should().NotBeNull();
        rt.P99.Should().NotBeNull();
        rt.ThresholdValue.Should().Be(TimeSpan.FromMilliseconds(500));
        rt.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Reason_strings_differ_by_latency_status()
    {
        // Healthy latency
        var healthySignals = TestFixtures.CreateMixedSignals(10, 0, TimeSpan.FromMilliseconds(50));
        var degradedSignals = TestFixtures.CreateMixedSignals(10, 0, TimeSpan.FromMilliseconds(500));
        var circuitOpenSignals = TestFixtures.CreateMixedSignals(10, 0, TimeSpan.FromMilliseconds(2000));

        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = TestFixtures.DefaultResponseTimePolicy,
        };

        var healthyAssessment = _evaluator.Evaluate(
            healthySignals, policy, HealthState.Healthy, TestFixtures.BaseTime);
        var degradedAssessment = _evaluator.Evaluate(
            degradedSignals, policy, HealthState.Healthy, TestFixtures.BaseTime);
        var circuitOpenAssessment = _evaluator.Evaluate(
            circuitOpenSignals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        // Assert — each status has a different reason
        healthyAssessment.ResponseTime!.Reason.Should().Contain("within thresholds");
        degradedAssessment.ResponseTime!.Reason.Should().Contain("exceeds degraded threshold");
        circuitOpenAssessment.ResponseTime!.Reason.Should().Contain("exceeds unhealthy threshold");
    }

    // ─── Worst symmetry: order should not matter ─────────────────────────

    [Theory]
    [InlineData(HealthState.Healthy, HealthState.Degraded, HealthState.Degraded)]
    [InlineData(HealthState.Degraded, HealthState.Healthy, HealthState.Degraded)]
    [InlineData(HealthState.Healthy, HealthState.CircuitOpen, HealthState.CircuitOpen)]
    [InlineData(HealthState.CircuitOpen, HealthState.Healthy, HealthState.CircuitOpen)]
    [InlineData(HealthState.Degraded, HealthState.CircuitOpen, HealthState.CircuitOpen)]
    [InlineData(HealthState.CircuitOpen, HealthState.Degraded, HealthState.CircuitOpen)]
    public void Worst_is_commutative(HealthState a, HealthState b, HealthState expected)
    {
        PolicyEvaluator.Worst(a, b).Should().Be(expected);
        PolicyEvaluator.Worst(b, a).Should().Be(expected);
    }
}

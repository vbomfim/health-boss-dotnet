using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Tests;

public sealed class PolicyEvaluatorTests
{
    private readonly IPolicyEvaluator _evaluator = new PolicyEvaluator();
    private readonly HealthPolicy _policy = TestFixtures.DefaultPolicy;

    [Fact]
    public void All_success_signals_returns_Healthy()
    {
        var signals = TestFixtures.CreateSignals(successCount: 10, failureCount: 0);

        var result = _evaluator.Evaluate(
            signals, _policy, HealthState.Healthy, TestFixtures.BaseTime);

        result.RecommendedState.Should().Be(HealthState.Healthy);
        result.SuccessRate.Should().Be(1.0);
        result.TotalSignals.Should().Be(10);
        result.SuccessCount.Should().Be(10);
        result.FailureCount.Should().Be(0);
    }

    [Fact]
    public void All_failure_signals_returns_CircuitOpen()
    {
        var signals = TestFixtures.CreateSignals(successCount: 0, failureCount: 10);

        var result = _evaluator.Evaluate(
            signals, _policy, HealthState.Healthy, TestFixtures.BaseTime);

        result.RecommendedState.Should().Be(HealthState.CircuitOpen);
        result.SuccessRate.Should().Be(0.0);
        result.FailureCount.Should().Be(10);
    }

    [Fact]
    public void Success_rate_at_degraded_threshold_returns_Healthy()
    {
        // DegradedThreshold = 0.9, so exactly 0.9 should be Healthy
        var signals = TestFixtures.CreateSignals(successCount: 9, failureCount: 1);

        var result = _evaluator.Evaluate(
            signals, _policy, HealthState.Healthy, TestFixtures.BaseTime);

        result.RecommendedState.Should().Be(HealthState.Healthy);
        result.SuccessRate.Should().Be(0.9);
    }

    [Fact]
    public void Success_rate_below_degraded_threshold_returns_Degraded()
    {
        // 8/10 = 0.8, below DegradedThreshold of 0.9
        var signals = TestFixtures.CreateSignals(successCount: 8, failureCount: 2);

        var result = _evaluator.Evaluate(
            signals, _policy, HealthState.Healthy, TestFixtures.BaseTime);

        result.RecommendedState.Should().Be(HealthState.Degraded);
        result.SuccessRate.Should().Be(0.8);
    }

    [Fact]
    public void Success_rate_at_circuit_open_threshold_returns_Degraded()
    {
        // CircuitOpenThreshold = 0.5, exactly 0.5 should be Degraded (not CircuitOpen)
        var signals = TestFixtures.CreateSignals(successCount: 5, failureCount: 5);

        var result = _evaluator.Evaluate(
            signals, _policy, HealthState.Healthy, TestFixtures.BaseTime);

        result.RecommendedState.Should().Be(HealthState.Degraded);
        result.SuccessRate.Should().Be(0.5);
    }

    [Fact]
    public void Success_rate_below_circuit_open_threshold_returns_CircuitOpen()
    {
        // 4/10 = 0.4, below CircuitOpenThreshold of 0.5
        var signals = TestFixtures.CreateSignals(successCount: 4, failureCount: 6);

        var result = _evaluator.Evaluate(
            signals, _policy, HealthState.Healthy, TestFixtures.BaseTime);

        result.RecommendedState.Should().Be(HealthState.CircuitOpen);
        result.SuccessRate.Should().BeApproximately(0.4, 0.001);
    }

    [Fact]
    public void Insufficient_signals_returns_current_state()
    {
        // MinSignalsForEvaluation = 5, only 3 provided
        var signals = TestFixtures.CreateSignals(successCount: 1, failureCount: 2);

        var result = _evaluator.Evaluate(
            signals, _policy, HealthState.Degraded, TestFixtures.BaseTime);

        result.RecommendedState.Should().Be(HealthState.Degraded);
        result.TotalSignals.Should().Be(3);
    }

    [Fact]
    public void Zero_signals_returns_current_state()
    {
        var signals = new List<HealthSignal>();

        var result = _evaluator.Evaluate(
            signals, _policy, HealthState.Healthy, TestFixtures.BaseTime);

        result.RecommendedState.Should().Be(HealthState.Healthy);
        result.TotalSignals.Should().Be(0);
        result.SuccessRate.Should().Be(0.0);
    }

    [Fact]
    public void Timeout_and_rejected_count_as_failures()
    {
        var signals = new List<HealthSignal>
        {
            TestFixtures.CreateSignal(SignalOutcome.Success),
            TestFixtures.CreateSignal(SignalOutcome.Success),
            TestFixtures.CreateSignal(SignalOutcome.Success),
            TestFixtures.CreateSignal(SignalOutcome.Success),
            TestFixtures.CreateSignal(SignalOutcome.Success),
            TestFixtures.CreateSignal(SignalOutcome.Timeout),
            TestFixtures.CreateSignal(SignalOutcome.Rejected),
            TestFixtures.CreateSignal(SignalOutcome.Failure),
            TestFixtures.CreateSignal(SignalOutcome.Timeout),
            TestFixtures.CreateSignal(SignalOutcome.Rejected),
        };

        var result = _evaluator.Evaluate(
            signals, _policy, HealthState.Healthy, TestFixtures.BaseTime);

        result.SuccessCount.Should().Be(5);
        result.FailureCount.Should().Be(5);
        result.SuccessRate.Should().Be(0.5);
    }

    [Fact]
    public void Assessment_includes_correct_metadata()
    {
        var signals = TestFixtures.CreateSignals(successCount: 10, failureCount: 0);
        var evaluatedAt = TestFixtures.BaseTime.AddMinutes(3);

        var result = _evaluator.Evaluate(
            signals, _policy, HealthState.Healthy, evaluatedAt);

        result.DependencyId.Should().Be(TestFixtures.DefaultDependencyId);
        result.WindowDuration.Should().Be(_policy.SlidingWindow);
        result.EvaluatedAt.Should().Be(evaluatedAt);
    }
}

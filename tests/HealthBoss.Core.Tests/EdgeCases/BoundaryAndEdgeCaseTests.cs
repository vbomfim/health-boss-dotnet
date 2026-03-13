// <copyright file="BoundaryAndEdgeCaseTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace HealthBoss.Core.Tests.EdgeCases;

/// <summary>
/// Boundary value tests and edge cases for Sprint 1 components.
/// Tests exact threshold values, off-by-one conditions, and unusual inputs.
/// </summary>
public sealed class BoundaryAndEdgeCaseTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ISystemClock _clock;
    private readonly IPolicyEvaluator _evaluator = new PolicyEvaluator();
    private readonly IStateGraph _graph = new DefaultStateGraph();
    private readonly ITransitionEngine _engine;

    public BoundaryAndEdgeCaseTests()
    {
        _timeProvider.SetUtcNow(TestFixtures.BaseTime);
        _clock = new SystemClock(_timeProvider);
        _engine = new TransitionEngine(_graph);
    }

    // ──────────────────────────────────────────────────────────────────
    // PolicyEvaluator boundary values
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// [BOUNDARY] Success rate just below DegradedThreshold (89/100 = 0.89).
    /// Existing tests only use 8/10; this verifies precision with larger sample.
    /// </summary>
    [Fact]
    public void PolicyEvaluator_89_percent_is_degraded_with_large_sample()
    {
        var signals = TestFixtures.CreateSignals(successCount: 89, failureCount: 11);
        var policy = TestFixtures.DefaultPolicy; // DegradedThreshold = 0.9

        var result = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        result.SuccessRate.Should().Be(0.89);
        result.RecommendedState.Should().Be(HealthState.Degraded);
    }

    /// <summary>
    /// [BOUNDARY] Success rate just above CircuitOpenThreshold (51/100 = 0.51).
    /// Existing tests only use 5/10; this verifies precision.
    /// </summary>
    [Fact]
    public void PolicyEvaluator_51_percent_is_degraded_not_circuit_open()
    {
        var signals = TestFixtures.CreateSignals(successCount: 51, failureCount: 49);
        var policy = TestFixtures.DefaultPolicy; // CircuitOpenThreshold = 0.5

        var result = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        result.SuccessRate.Should().Be(0.51);
        result.RecommendedState.Should().Be(HealthState.Degraded);
    }

    /// <summary>
    /// [BOUNDARY] Success rate just below CircuitOpenThreshold (49/100 = 0.49).
    /// </summary>
    [Fact]
    public void PolicyEvaluator_49_percent_is_circuit_open()
    {
        var signals = TestFixtures.CreateSignals(successCount: 49, failureCount: 51);
        var policy = TestFixtures.DefaultPolicy; // CircuitOpenThreshold = 0.5

        var result = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, TestFixtures.BaseTime);

        result.SuccessRate.Should().Be(0.49);
        result.RecommendedState.Should().Be(HealthState.CircuitOpen);
    }

    /// <summary>
    /// [BOUNDARY][AC-3] Exactly at MinSignalsForEvaluation-1: evaluation returns
    /// current state (not enough data).
    /// </summary>
    [Fact]
    public void PolicyEvaluator_one_below_min_signals_returns_current_state()
    {
        var signals = TestFixtures.CreateSignals(successCount: 0, failureCount: 4);
        var policy = TestFixtures.DefaultPolicy; // MinSignals = 5

        var result = _evaluator.Evaluate(
            signals, policy, HealthState.CircuitOpen, TestFixtures.BaseTime);

        // Despite 0% success, insufficient signals → keep CircuitOpen
        result.RecommendedState.Should().Be(HealthState.CircuitOpen);
    }

    /// <summary>
    /// [BOUNDARY][AC-3] Exactly at MinSignalsForEvaluation: evaluation proceeds.
    /// </summary>
    [Fact]
    public void PolicyEvaluator_exactly_at_min_signals_evaluates()
    {
        var signals = TestFixtures.CreateSignals(successCount: 5, failureCount: 0);
        var policy = TestFixtures.DefaultPolicy; // MinSignals = 5

        var result = _evaluator.Evaluate(
            signals, policy, HealthState.CircuitOpen, TestFixtures.BaseTime);

        // 5/5 = 100% → Healthy (evaluation proceeds at exact threshold)
        result.TotalSignals.Should().Be(5);
        result.RecommendedState.Should().Be(HealthState.Healthy);
    }

    /// <summary>
    /// [BOUNDARY] DegradedThreshold exactly from Degraded state — should recover to Healthy.
    /// Existing test only checks from Healthy state.
    /// </summary>
    [Fact]
    public void PolicyEvaluator_at_threshold_from_degraded_recovers_to_healthy()
    {
        var signals = TestFixtures.CreateSignals(successCount: 9, failureCount: 1);
        var policy = TestFixtures.DefaultPolicy; // DegradedThreshold = 0.9

        var result = _evaluator.Evaluate(
            signals, policy, HealthState.Degraded, TestFixtures.BaseTime);

        // 0.9 >= 0.9 → Healthy (recovery from Degraded)
        result.SuccessRate.Should().Be(0.9);
        result.RecommendedState.Should().Be(HealthState.Healthy);
    }

    /// <summary>
    /// [BOUNDARY] DegradedThreshold exactly from CircuitOpen state — should recover to Healthy.
    /// </summary>
    [Fact]
    public void PolicyEvaluator_at_threshold_from_circuit_open_recovers_to_healthy()
    {
        var signals = TestFixtures.CreateSignals(successCount: 9, failureCount: 1);
        var policy = TestFixtures.DefaultPolicy;

        var result = _evaluator.Evaluate(
            signals, policy, HealthState.CircuitOpen, TestFixtures.BaseTime);

        result.SuccessRate.Should().Be(0.9);
        result.RecommendedState.Should().Be(HealthState.Healthy);
    }

    /// <summary>
    /// [EDGE] Single success signal with MinSignals=1 → evaluates to Healthy.
    /// </summary>
    [Fact]
    public void PolicyEvaluator_single_signal_with_min_one_evaluates()
    {
        var signals = TestFixtures.CreateSignals(successCount: 1, failureCount: 0);
        var policy = TestFixtures.DefaultPolicy with { MinSignalsForEvaluation = 1 };

        var result = _evaluator.Evaluate(
            signals, policy, HealthState.Degraded, TestFixtures.BaseTime);

        result.RecommendedState.Should().Be(HealthState.Healthy);
    }

    /// <summary>
    /// [EDGE] Signals from mixed DependencyIds — evaluator uses first signal's DependencyId.
    /// </summary>
    [Fact]
    public void PolicyEvaluator_mixed_dependency_ids_uses_first_signals_id()
    {
        var depA = new DependencyId("dep-a");
        var depB = new DependencyId("dep-b");

        var signals = new List<HealthSignal>
        {
            TestFixtures.CreateSignal(SignalOutcome.Success, dependencyId: depA),
            TestFixtures.CreateSignal(SignalOutcome.Success, dependencyId: depB),
            TestFixtures.CreateSignal(SignalOutcome.Success, dependencyId: depA),
            TestFixtures.CreateSignal(SignalOutcome.Success, dependencyId: depB),
            TestFixtures.CreateSignal(SignalOutcome.Success, dependencyId: depB),
        };

        var result = _evaluator.Evaluate(
            signals, TestFixtures.DefaultPolicy, HealthState.Healthy, TestFixtures.BaseTime);

        result.DependencyId.Should().Be(depA);
    }

    /// <summary>
    /// [EDGE] Zero signals → default DependencyId and 0.0 success rate.
    /// </summary>
    [Fact]
    public void PolicyEvaluator_zero_signals_returns_default_dependency_id()
    {
        var signals = new List<HealthSignal>();

        var result = _evaluator.Evaluate(
            signals, TestFixtures.DefaultPolicy, HealthState.Healthy, TestFixtures.BaseTime);

        result.DependencyId.Should().Be(default(DependencyId));
        result.SuccessRate.Should().Be(0.0);
        result.TotalSignals.Should().Be(0);
    }

    // ──────────────────────────────────────────────────────────────────
    // TransitionEngine boundary values
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// [BOUNDARY] Cooldown exactly at threshold: timeSinceLastTransition == CooldownBeforeTransition.
    /// Should allow transition (>= comparison).
    /// </summary>
    [Fact]
    public void TransitionEngine_cooldown_exactly_at_threshold_allows_transition()
    {
        var policy = TestFixtures.ZeroJitterPolicy; // Cooldown = 30s
        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.Degraded,
            successRate: 0.8,
            evaluatedAt: TestFixtures.BaseTime);

        // Last transition exactly 30s ago (== cooldown)
        var lastTransition = TestFixtures.BaseTime.AddSeconds(-30);

        var decision = _engine.Evaluate(HealthState.Healthy, assessment, policy, lastTransition);

        decision.ShouldTransition.Should().BeTrue();
        decision.TargetState.Should().Be(HealthState.Degraded);
    }

    /// <summary>
    /// [BOUNDARY] Cooldown 1ms below threshold: blocked.
    /// </summary>
    [Fact]
    public void TransitionEngine_cooldown_1ms_below_threshold_blocks_transition()
    {
        var policy = TestFixtures.ZeroJitterPolicy; // Cooldown = 30s
        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.Degraded,
            successRate: 0.8,
            evaluatedAt: TestFixtures.BaseTime);

        // Last transition at 29.999s ago (< cooldown by 1ms)
        var lastTransition = TestFixtures.BaseTime.Add(-policy.CooldownBeforeTransition).AddMilliseconds(1);

        var decision = _engine.Evaluate(HealthState.Healthy, assessment, policy, lastTransition);

        decision.ShouldTransition.Should().BeFalse();
        decision.Reason.Should().Contain("cooldown");
    }

    /// <summary>
    /// [COVERAGE] No direct Healthy→CircuitOpen transition exists.
    /// Even with CircuitOpen recommendation, Healthy must go through Degraded first.
    /// </summary>
    [Fact]
    public void TransitionEngine_healthy_to_circuit_open_goes_through_degraded()
    {
        var policy = TestFixtures.ZeroJitterPolicy;
        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.CircuitOpen,
            successRate: 0.3);

        var lastTransition = TestFixtures.BaseTime.AddMinutes(-5);

        var decision = _engine.Evaluate(HealthState.Healthy, assessment, policy, lastTransition);

        // The Healthy→Degraded guard fires on CircuitOpen recommendation too
        // (guard: a.RecommendedState is Degraded or CircuitOpen)
        decision.ShouldTransition.Should().BeTrue();
        decision.TargetState.Should().Be(HealthState.Degraded);
    }

    /// <summary>
    /// [COVERAGE] CircuitOpen has no transition to Degraded.
    /// Recovery always goes directly to Healthy.
    /// </summary>
    [Fact]
    public void TransitionEngine_circuit_open_cannot_transition_to_degraded()
    {
        var policy = TestFixtures.ZeroJitterPolicy;

        // Assessment recommends Degraded (success rate in degraded range)
        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.Degraded,
            successRate: 0.7);

        var lastTransition = TestFixtures.BaseTime.AddMinutes(-5);

        var decision = _engine.Evaluate(HealthState.CircuitOpen, assessment, policy, lastTransition);

        // No guard matches for CircuitOpen→Degraded (only CircuitOpen→Healthy exists)
        decision.ShouldTransition.Should().BeFalse();
        decision.Reason.Should().Contain("No guard matched");
    }

    // ──────────────────────────────────────────────────────────────────
    // SignalBuffer boundary values
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// [BOUNDARY] SignalBuffer with capacity=1: only one signal survives.
    /// </summary>
    [Fact]
    public void SignalBuffer_capacity_one_only_keeps_latest()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 1);

        buffer.Record(TestFixtures.CreateSignal(
            SignalOutcome.Success,
            timestamp: _clock.UtcNow));
        buffer.Record(TestFixtures.CreateSignal(
            SignalOutcome.Failure,
            timestamp: _clock.UtcNow.AddSeconds(1)));

        buffer.Count.Should().Be(1);

        var signals = buffer.GetSignals(TimeSpan.FromMinutes(5));
        signals.Should().ContainSingle();
        signals[0].Outcome.Should().Be(SignalOutcome.Failure); // Only the latest
    }

    /// <summary>
    /// [BOUNDARY] Trim with cutoff exactly at a signal's timestamp.
    /// The implementation uses strict less-than (&lt;), so the signal at exactly
    /// the cutoff should survive.
    /// </summary>
    [Fact]
    public void SignalBuffer_trim_cutoff_exactly_at_signal_timestamp_keeps_signal()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 10_000);

        var signalTime = _clock.UtcNow;
        buffer.Record(TestFixtures.CreateSignal(
            SignalOutcome.Success,
            timestamp: signalTime));

        // Trim with cutoff == signal timestamp (uses < comparison, not <=)
        buffer.Trim(signalTime);

        // Signal at exactly cutoff should survive (< means "before", not "at or before")
        buffer.Count.Should().Be(1);
    }

    /// <summary>
    /// [BOUNDARY] Trim with cutoff 1 tick after signal timestamp removes it.
    /// </summary>
    [Fact]
    public void SignalBuffer_trim_cutoff_1_tick_after_signal_removes_it()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 10_000);

        var signalTime = _clock.UtcNow;
        buffer.Record(TestFixtures.CreateSignal(
            SignalOutcome.Success,
            timestamp: signalTime));

        buffer.Trim(signalTime.AddTicks(1));

        buffer.Count.Should().Be(0);
    }

    /// <summary>
    /// [EDGE] GetSignals with TimeSpan.Zero window returns nothing
    /// (cutoff == now, and no signal has timestamp >= now unless recorded at exact now).
    /// </summary>
    [Fact]
    public void SignalBuffer_GetSignals_zero_window_returns_only_current_signals()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 10_000);

        // Record signal at exactly now
        buffer.Record(TestFixtures.CreateSignal(
            SignalOutcome.Success,
            timestamp: _clock.UtcNow));

        // Record signal 1ms in the past
        buffer.Record(TestFixtures.CreateSignal(
            SignalOutcome.Failure,
            timestamp: _clock.UtcNow.AddMilliseconds(-1)));

        var signals = buffer.GetSignals(TimeSpan.Zero);

        // cutoff = now - 0 = now, so only signals >= now survive
        signals.Should().ContainSingle();
        signals[0].Outcome.Should().Be(SignalOutcome.Success);
    }

    /// <summary>
    /// [COVERAGE] Trim with cutoff in the far future removes all signals.
    /// </summary>
    [Fact]
    public void SignalBuffer_trim_future_cutoff_removes_all_signals()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 10_000);

        for (int i = 0; i < 10; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Success,
                timestamp: _clock.UtcNow.AddSeconds(i)));
        }

        buffer.Count.Should().Be(10);

        buffer.Trim(_clock.UtcNow.AddHours(1));

        buffer.Count.Should().Be(0);
    }

    /// <summary>
    /// [EDGE] Trim on empty buffer does nothing (no exceptions).
    /// </summary>
    [Fact]
    public void SignalBuffer_trim_empty_buffer_is_safe()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 10_000);

        var act = () => buffer.Trim(_clock.UtcNow);

        act.Should().NotThrow();
        buffer.Count.Should().Be(0);
    }

    /// <summary>
    /// [EDGE] Constructor rejects negative capacity.
    /// </summary>
    [Fact]
    public void SignalBuffer_constructor_rejects_negative_capacity()
    {
        var act = () => new SignalBuffer(_clock, maxCapacity: -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// [EDGE] Constructor rejects null clock.
    /// </summary>
    [Fact]
    public void SignalBuffer_constructor_rejects_null_clock()
    {
        var act = () => new SignalBuffer(null!, maxCapacity: 100);
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// [EDGE] TransitionEngine constructor rejects null state graph.
    /// </summary>
    [Fact]
    public void TransitionEngine_constructor_rejects_null_graph()
    {
        var act = () => new TransitionEngine(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// [EDGE] SystemClock constructor rejects null time provider.
    /// </summary>
    [Fact]
    public void SystemClock_constructor_rejects_null_time_provider()
    {
        var act = () => new SystemClock(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ──────────────────────────────────────────────────────────────────
    // Validation boundary values
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// [BOUNDARY] ValidateHealthPolicy: equal thresholds (DegradedThreshold ==
    /// CircuitOpenThreshold) should be rejected.
    /// </summary>
    [Fact]
    public void Validation_equal_thresholds_rejected()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            DegradedThreshold = 0.5,
            CircuitOpenThreshold = 0.5,
        };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// [BOUNDARY] ValidateHealthPolicy: threshold at exact boundary 0.0.
    /// </summary>
    [Fact]
    public void Validation_threshold_at_zero_accepted_if_ordered()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            DegradedThreshold = 0.1,
            CircuitOpenThreshold = 0.0,
        };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().NotThrow();
    }

    /// <summary>
    /// [BOUNDARY] ValidateHealthPolicy: threshold at exact boundary 1.0.
    /// </summary>
    [Fact]
    public void Validation_threshold_at_one_accepted_if_ordered()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            DegradedThreshold = 1.0,
            CircuitOpenThreshold = 0.5,
        };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().NotThrow();
    }

    /// <summary>
    /// [BOUNDARY] ValidateHealthPolicy: MinSignalsForEvaluation = 0 is valid.
    /// </summary>
    [Fact]
    public void Validation_min_signals_zero_accepted()
    {
        var policy = TestFixtures.DefaultPolicy with { MinSignalsForEvaluation = 0 };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().NotThrow();
    }

    /// <summary>
    /// [BOUNDARY] ValidateHealthPolicy: negative sliding window rejected.
    /// </summary>
    [Fact]
    public void Validation_negative_sliding_window_rejected()
    {
        var policy = TestFixtures.DefaultPolicy with { SlidingWindow = TimeSpan.FromSeconds(-1) };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// [BOUNDARY] ValidateDependencyId: exactly 1 character is valid.
    /// </summary>
    [Fact]
    public void Validation_dependency_id_single_char_accepted()
    {
        var act = () => HealthBossValidator.ValidateDependencyId("x");
        act.Should().NotThrow();
    }

    /// <summary>
    /// [EDGE] ValidateDependencyId: all valid character classes in one name.
    /// </summary>
    [Fact]
    public void Validation_dependency_id_all_valid_chars()
    {
        var act = () => HealthBossValidator.ValidateDependencyId("aZ-09_test");
        act.Should().NotThrow();
    }

    // ──────────────────────────────────────────────────────────────────
    // StateGraph edge cases
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// [EDGE] Healthy→Degraded guard also fires when recommendation is CircuitOpen.
    /// This is by design: the guard uses `is Degraded or CircuitOpen`.
    /// </summary>
    [Fact]
    public void StateGraph_healthy_to_degraded_guard_fires_on_circuit_open_recommendation()
    {
        var transition = _graph.GetTransitionsFrom(HealthState.Healthy)
            .Single(t => t.To == HealthState.Degraded);

        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.CircuitOpen,
            successRate: 0.3);

        transition.Guard(assessment).Should().BeTrue();
    }

    /// <summary>
    /// [EDGE] CircuitOpen→Healthy guard does NOT fire on Degraded recommendation.
    /// Recovery from CircuitOpen requires Healthy recommendation.
    /// </summary>
    [Fact]
    public void StateGraph_circuit_open_to_healthy_guard_does_not_fire_on_degraded()
    {
        var transition = _graph.GetTransitionsFrom(HealthState.CircuitOpen)
            .Single(t => t.To == HealthState.Healthy);

        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.Degraded,
            successRate: 0.7);

        transition.Guard(assessment).Should().BeFalse();
    }

    /// <summary>
    /// [EDGE] All transitions have From states that match their parent group.
    /// </summary>
    [Fact]
    public void StateGraph_all_transitions_have_consistent_from_state()
    {
        foreach (var state in _graph.AllStates)
        {
            var transitions = _graph.GetTransitionsFrom(state);
            transitions.Should().OnlyContain(t => t.From == state);
        }
    }
}

// <copyright file="SignalPipelineIntegrationTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace HealthBoss.Core.Tests.Integration;

/// <summary>
/// Integration tests verifying the full signal → assessment → transition pipeline.
/// These test real component wiring, not mocked interfaces.
/// </summary>
public sealed class SignalPipelineIntegrationTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ISystemClock _clock;
    private readonly IPolicyEvaluator _evaluator = new PolicyEvaluator();
    private readonly IStateGraph _graph = new DefaultStateGraph();
    private readonly ITransitionEngine _engine;

    public SignalPipelineIntegrationTests()
    {
        _timeProvider.SetUtcNow(TestFixtures.BaseTime);
        _clock = new SystemClock(_timeProvider);
        _engine = new TransitionEngine(_graph);
    }

    // ──────────────────────────────────────────────────────────────────
    // [AC-1] Signal recording + policy evaluation (85% success → Degraded)
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// [AC-1][INTEGRATION] Full pipeline: record signals into buffer, retrieve via
    /// sliding window, evaluate with PolicyEvaluator, decide with TransitionEngine.
    /// Verifies 85% success rate results in Degraded recommendation.
    /// </summary>
    [Fact]
    public void AC1_Record_signals_evaluate_policy_triggers_degraded_transition()
    {
        // Arrange: buffer + 85 success + 15 failure = 85% rate
        var buffer = new SignalBuffer(_clock, maxCapacity: 10_000);
        var policy = TestFixtures.ZeroJitterPolicy; // DegradedThreshold = 0.9

        for (int i = 0; i < 85; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Success,
                timestamp: _clock.UtcNow.AddSeconds(i)));
        }

        for (int i = 0; i < 15; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Failure,
                timestamp: _clock.UtcNow.AddSeconds(85 + i)));
        }

        // Act: get signals from buffer, evaluate, decide
        var signals = buffer.GetSignals(policy.SlidingWindow);
        var assessment = _evaluator.Evaluate(signals, policy, HealthState.Healthy, _clock.UtcNow);
        var lastTransition = _clock.UtcNow.AddMinutes(-10); // Well past cooldown
        var decision = _engine.Evaluate(HealthState.Healthy, assessment, policy, lastTransition);

        // Assert: 85% < 0.9 threshold → Degraded
        signals.Should().HaveCount(100);
        assessment.SuccessRate.Should().Be(0.85);
        assessment.RecommendedState.Should().Be(HealthState.Degraded);
        decision.ShouldTransition.Should().BeTrue();
        decision.TargetState.Should().Be(HealthState.Degraded);
    }

    /// <summary>
    /// [AC-1][INTEGRATION] Full pipeline: 90% success rate is exactly at
    /// DegradedThreshold, so should remain Healthy — no transition fires.
    /// </summary>
    [Fact]
    public void AC1_Exactly_at_degraded_threshold_remains_healthy_no_transition()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 10_000);
        var policy = TestFixtures.ZeroJitterPolicy; // DegradedThreshold = 0.9

        for (int i = 0; i < 90; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Success,
                timestamp: _clock.UtcNow.AddSeconds(i)));
        }

        for (int i = 0; i < 10; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Failure,
                timestamp: _clock.UtcNow.AddSeconds(90 + i)));
        }

        var signals = buffer.GetSignals(policy.SlidingWindow);
        var assessment = _evaluator.Evaluate(signals, policy, HealthState.Healthy, _clock.UtcNow);
        var lastTransition = _clock.UtcNow.AddMinutes(-10);
        var decision = _engine.Evaluate(HealthState.Healthy, assessment, policy, lastTransition);

        assessment.SuccessRate.Should().Be(0.9);
        assessment.RecommendedState.Should().Be(HealthState.Healthy);
        decision.ShouldTransition.Should().BeFalse();
    }

    // ──────────────────────────────────────────────────────────────────
    // [AC-2] Sliding window expiration
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// [AC-2][INTEGRATION] Old signals outside the sliding window are excluded from
    /// evaluation. After time advances, only fresh signals count.
    /// </summary>
    [Fact]
    public void AC2_Sliding_window_excludes_expired_signals_from_evaluation()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 10_000);
        var policy = TestFixtures.DefaultPolicy; // 5-minute window

        // Record 10 failures at T=0 (these will expire)
        for (int i = 0; i < 10; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Failure,
                timestamp: _clock.UtcNow.AddSeconds(i)));
        }

        // Advance past the 5-minute window
        _timeProvider.Advance(TimeSpan.FromMinutes(6));

        // Record 10 successes at T=6min (these are within window)
        for (int i = 0; i < 10; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Success,
                timestamp: _clock.UtcNow.AddSeconds(i)));
        }

        // Act: get signals within window and evaluate
        var signals = buffer.GetSignals(policy.SlidingWindow);
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Degraded, _clock.UtcNow);

        // Assert: only the 10 fresh successes are visible
        signals.Should().HaveCount(10);
        assessment.SuccessRate.Should().Be(1.0);
        assessment.RecommendedState.Should().Be(HealthState.Healthy);
    }

    /// <summary>
    /// [AC-2][BOUNDARY] Signals at the exact boundary of the sliding window
    /// are included (cutoff uses >= comparison).
    /// </summary>
    [Fact]
    public void AC2_Signal_at_exact_window_boundary_is_included()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 10_000);
        var policy = TestFixtures.DefaultPolicy; // 5-minute window

        // Record signal at exactly (now - 5 minutes)
        var boundaryTimestamp = _clock.UtcNow.AddMinutes(-5);
        buffer.Record(TestFixtures.CreateSignal(
            SignalOutcome.Success,
            timestamp: boundaryTimestamp));

        // Record another signal at (now - 5min - 1ms) — just outside
        buffer.Record(TestFixtures.CreateSignal(
            SignalOutcome.Failure,
            timestamp: boundaryTimestamp.AddMilliseconds(-1)));

        var signals = buffer.GetSignals(policy.SlidingWindow);

        // The signal at exactly (now - window) should be included (>=)
        // The signal at (now - window - 1ms) should be excluded
        signals.Should().ContainSingle();
        signals[0].Timestamp.Should().Be(boundaryTimestamp);
    }

    /// <summary>
    /// [AC-2][INTEGRATION] After Trim, expired signals are physically removed.
    /// Subsequent evaluation only sees remaining signals.
    /// </summary>
    [Fact]
    public void AC2_Trim_then_evaluate_only_counts_surviving_signals()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 10_000);
        var policy = TestFixtures.DefaultPolicy;

        // Record 5 failures at T=0, then 5 successes at T=3min
        for (int i = 0; i < 5; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Failure,
                timestamp: _clock.UtcNow.AddSeconds(i)));
        }

        for (int i = 0; i < 5; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Success,
                timestamp: _clock.UtcNow.AddMinutes(3).AddSeconds(i)));
        }

        buffer.Count.Should().Be(10);

        // Trim everything before T=1min
        buffer.Trim(_clock.UtcNow.AddMinutes(1));

        buffer.Count.Should().Be(5); // Only the T=3min successes survive

        // Advance clock so all surviving signals are in window
        _timeProvider.Advance(TimeSpan.FromMinutes(3));
        var signals = buffer.GetSignals(policy.SlidingWindow);

        signals.Should().HaveCount(5);
        signals.Should().OnlyContain(s => s.Outcome == SignalOutcome.Success);
    }

    // ──────────────────────────────────────────────────────────────────
    // [AC-3] Minimum signals threshold
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// [AC-3][INTEGRATION] When buffer has fewer signals than MinSignalsForEvaluation,
    /// the assessment returns the current state regardless of success rate.
    /// </summary>
    [Fact]
    public void AC3_Below_min_signals_returns_current_state_through_full_pipeline()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 10_000);
        var policy = TestFixtures.ZeroJitterPolicy; // MinSignals = 5

        // Record only 4 failures (below min of 5)
        for (int i = 0; i < 4; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Failure,
                timestamp: _clock.UtcNow.AddSeconds(i)));
        }

        var signals = buffer.GetSignals(policy.SlidingWindow);
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, _clock.UtcNow);

        // Assert: despite 0% success rate, insufficient data → keep current state
        signals.Should().HaveCount(4);
        assessment.SuccessRate.Should().Be(0.0);
        assessment.RecommendedState.Should().Be(HealthState.Healthy);

        // TransitionEngine should not transition since recommended == current
        var lastTransition = _clock.UtcNow.AddMinutes(-10);
        var decision = _engine.Evaluate(
            HealthState.Healthy, assessment, policy, lastTransition);

        decision.ShouldTransition.Should().BeFalse();
    }

    /// <summary>
    /// [AC-3][BOUNDARY] Exactly at MinSignalsForEvaluation threshold:
    /// evaluation proceeds normally.
    /// </summary>
    [Fact]
    public void AC3_Exactly_at_min_signals_threshold_evaluates_normally()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 10_000);
        var policy = TestFixtures.ZeroJitterPolicy; // MinSignals = 5

        // Record exactly 5 failures (at min threshold)
        for (int i = 0; i < 5; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Failure,
                timestamp: _clock.UtcNow.AddSeconds(i)));
        }

        var signals = buffer.GetSignals(policy.SlidingWindow);
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, _clock.UtcNow);

        // With exactly 5 signals, evaluation should proceed → 0% → CircuitOpen
        signals.Should().HaveCount(5);
        assessment.TotalSignals.Should().Be(5);
        assessment.RecommendedState.Should().Be(HealthState.CircuitOpen);
    }

    /// <summary>
    /// [AC-3][COVERAGE] MinSignalsForEvaluation=0 means all signals are
    /// evaluated, even a single one.
    /// </summary>
    [Fact]
    public void AC3_MinSignals_zero_evaluates_even_single_signal()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 10_000);
        var policy = TestFixtures.ZeroJitterPolicy with { MinSignalsForEvaluation = 0 };

        buffer.Record(TestFixtures.CreateSignal(
            SignalOutcome.Failure,
            timestamp: _clock.UtcNow));

        var signals = buffer.GetSignals(policy.SlidingWindow);
        var assessment = _evaluator.Evaluate(
            signals, policy, HealthState.Healthy, _clock.UtcNow);

        assessment.TotalSignals.Should().Be(1);
        assessment.RecommendedState.Should().Be(HealthState.CircuitOpen);
    }

    // ──────────────────────────────────────────────────────────────────
    // [AC-1] Full state lifecycle
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// [FIX-7] Two-step degradation: feeding CircuitOpen-level signals from Healthy
    /// state must go Healthy→Degraded first, not skip to CircuitOpen directly.
    /// Then from Degraded, the same assessment triggers Degraded→CircuitOpen.
    /// </summary>
    [Fact]
    public void Fix7_Two_step_degradation_healthy_to_degraded_then_to_circuit_open()
    {
        var policy = TestFixtures.ZeroJitterPolicy;
        var lastTransition = TestFixtures.BaseTime.AddMinutes(-10); // Well past cooldown

        // Create an assessment that recommends CircuitOpen (success rate below 0.5)
        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.CircuitOpen,
            successRate: 0.3,
            evaluatedAt: TestFixtures.BaseTime);

        // Step 1: From Healthy, should transition to Degraded (not CircuitOpen)
        var decision1 = _engine.Evaluate(HealthState.Healthy, assessment, policy, lastTransition);

        decision1.ShouldTransition.Should().BeTrue();
        decision1.TargetState.Should().Be(HealthState.Degraded,
            "Healthy must step through Degraded first — no direct jump to CircuitOpen");

        // Simulate the transition
        lastTransition = TestFixtures.BaseTime;

        // Advance time past cooldown
        var assessment2 = assessment with
        {
            EvaluatedAt = TestFixtures.BaseTime.AddMinutes(1),
        };

        // Step 2: From Degraded, should now transition to CircuitOpen
        var decision2 = _engine.Evaluate(HealthState.Degraded, assessment2, policy, lastTransition);

        decision2.ShouldTransition.Should().BeTrue();
        decision2.TargetState.Should().Be(HealthState.CircuitOpen,
            "Degraded with CircuitOpen recommendation should transition to CircuitOpen");
    }

    /// <summary>
    /// [AC-1][INTEGRATION] Complete lifecycle: system degrades under failures,
    /// opens circuit, then recovers when successes return.
    /// </summary>
    [Fact]
    public void AC1_Full_lifecycle_healthy_to_degraded_to_circuit_open_to_recovery()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 10_000);
        var policy = TestFixtures.ZeroJitterPolicy; // Deterministic delays

        var currentState = HealthState.Healthy;
        var lastTransitionTime = _clock.UtcNow.AddMinutes(-10); // Past cooldown

        // Phase 1: Record 80% success → Degraded (below 0.9 threshold)
        RecordSignals(buffer, successCount: 80, failureCount: 20);

        var assessment1 = EvaluatePipeline(buffer, policy, currentState);
        assessment1.RecommendedState.Should().Be(HealthState.Degraded);

        var decision1 = _engine.Evaluate(currentState, assessment1, policy, lastTransitionTime);
        decision1.ShouldTransition.Should().BeTrue();
        decision1.TargetState.Should().Be(HealthState.Degraded);

        currentState = HealthState.Degraded;
        lastTransitionTime = _clock.UtcNow;

        // Phase 2: Advance time past BOTH window and cooldown so Phase 1 signals expire
        _timeProvider.Advance(policy.SlidingWindow.Add(TimeSpan.FromSeconds(1)));
        buffer.Trim(_clock.UtcNow.Add(-policy.SlidingWindow));

        RecordSignals(buffer, successCount: 3, failureCount: 7);

        var assessment2 = EvaluatePipeline(buffer, policy, currentState);
        assessment2.RecommendedState.Should().Be(HealthState.CircuitOpen);

        var decision2 = _engine.Evaluate(currentState, assessment2, policy, lastTransitionTime);
        decision2.ShouldTransition.Should().BeTrue();
        decision2.TargetState.Should().Be(HealthState.CircuitOpen);

        currentState = HealthState.CircuitOpen;
        lastTransitionTime = _clock.UtcNow;

        // Phase 3: Advance time past window so Phase 2 signals expire
        _timeProvider.Advance(policy.SlidingWindow.Add(TimeSpan.FromSeconds(1)));
        buffer.Trim(_clock.UtcNow.Add(-policy.SlidingWindow));

        RecordSignals(buffer, successCount: 10, failureCount: 0);

        var assessment3 = EvaluatePipeline(buffer, policy, currentState);
        assessment3.RecommendedState.Should().Be(HealthState.Healthy);

        var decision3 = _engine.Evaluate(currentState, assessment3, policy, lastTransitionTime);
        decision3.ShouldTransition.Should().BeTrue();
        decision3.TargetState.Should().Be(HealthState.Healthy);
    }

    /// <summary>
    /// [AC-1][INTEGRATION] Cooldown prevents rapid state flapping:
    /// two consecutive assessments within cooldown → second transition blocked.
    /// </summary>
    [Fact]
    public void AC1_Cooldown_prevents_rapid_flapping()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 10_000);
        var policy = TestFixtures.ZeroJitterPolicy;

        var currentState = HealthState.Healthy;
        var lastTransitionTime = _clock.UtcNow.AddMinutes(-10);

        // First transition: 80% → Degraded (allowed)
        RecordSignals(buffer, successCount: 8, failureCount: 2);
        var assessment1 = EvaluatePipeline(buffer, policy, currentState);
        var decision1 = _engine.Evaluate(currentState, assessment1, policy, lastTransitionTime);
        decision1.ShouldTransition.Should().BeTrue();

        currentState = HealthState.Degraded;
        lastTransitionTime = _clock.UtcNow;

        // Advance only 5 seconds (cooldown is 30s)
        _timeProvider.Advance(TimeSpan.FromSeconds(5));

        // Second transition attempt: recovery → Healthy (blocked by cooldown)
        buffer.Trim(_clock.UtcNow.Add(-policy.SlidingWindow));
        RecordSignals(buffer, successCount: 10, failureCount: 0);
        var assessment2 = EvaluatePipeline(buffer, policy, currentState);
        assessment2.RecommendedState.Should().Be(HealthState.Healthy);

        var decision2 = _engine.Evaluate(currentState, assessment2, policy, lastTransitionTime);
        decision2.ShouldTransition.Should().BeFalse();
        decision2.Reason.Should().Contain("cooldown");
    }

    // ──────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────

    private void RecordSignals(SignalBuffer buffer, int successCount, int failureCount)
    {
        for (int i = 0; i < successCount; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Success,
                timestamp: _clock.UtcNow.AddMilliseconds(i)));
        }

        for (int i = 0; i < failureCount; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Failure,
                timestamp: _clock.UtcNow.AddMilliseconds(successCount + i)));
        }
    }

    private HealthAssessment EvaluatePipeline(
        SignalBuffer buffer, HealthPolicy policy, HealthState currentState)
    {
        var signals = buffer.GetSignals(policy.SlidingWindow);
        return _evaluator.Evaluate(signals, policy, currentState, _clock.UtcNow);
    }
}

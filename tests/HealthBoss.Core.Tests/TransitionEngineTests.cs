using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Tests;

public sealed class TransitionEngineTests
{
    private readonly IStateGraph _graph = new DefaultStateGraph();

    private TransitionEngine CreateEngine() => new(_graph);

    [Fact]
    public void Guard_passes_and_cooldown_elapsed_transitions()
    {
        var engine = CreateEngine();
        var policy = TestFixtures.ZeroJitterPolicy;

        // Assessment recommends Degraded (below 0.9)
        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.Degraded,
            successRate: 0.8);

        // Last transition was 60 seconds ago (cooldown is 30s)
        var lastTransition = TestFixtures.BaseTime.AddSeconds(-60);

        var decision = engine.Evaluate(
            HealthState.Healthy, assessment, policy, lastTransition);

        decision.ShouldTransition.Should().BeTrue();
        decision.TargetState.Should().Be(HealthState.Degraded);
        decision.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Guard_fails_no_transition()
    {
        var engine = CreateEngine();
        var policy = TestFixtures.ZeroJitterPolicy;

        // Assessment recommends Healthy — no guard should fire from Healthy state
        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.Healthy,
            successRate: 0.95);

        var lastTransition = TestFixtures.BaseTime.AddSeconds(-60);

        var decision = engine.Evaluate(
            HealthState.Healthy, assessment, policy, lastTransition);

        decision.ShouldTransition.Should().BeFalse();
        decision.TargetState.Should().BeNull();
    }

    [Fact]
    public void Cooldown_not_elapsed_no_transition()
    {
        var engine = CreateEngine();
        var policy = TestFixtures.ZeroJitterPolicy;

        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.Degraded,
            successRate: 0.8);

        // Last transition was 10 seconds ago (cooldown is 30s)
        var lastTransition = assessment.EvaluatedAt.AddSeconds(-10);

        var decision = engine.Evaluate(
            HealthState.Healthy, assessment, policy, lastTransition);

        decision.ShouldTransition.Should().BeFalse();
        decision.Reason.Should().Contain("cooldown");
    }

    [Fact]
    public void Jitter_applied_within_bounds()
    {
        var engine = CreateEngine();
        var policy = TestFixtures.DefaultPolicy; // Has jitter 100ms–500ms

        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.Degraded,
            successRate: 0.8);

        var lastTransition = TestFixtures.BaseTime.AddMinutes(-5);

        // Run multiple times to check jitter range
        var delays = new List<TimeSpan>();
        for (int i = 0; i < 50; i++)
        {
            var decision = engine.Evaluate(
                HealthState.Healthy, assessment, policy, lastTransition);
            if (decision.ShouldTransition)
            {
                delays.Add(decision.Delay);
            }
        }

        delays.Should().NotBeEmpty();
        delays.Should().AllSatisfy(d =>
        {
            d.Should().BeGreaterThanOrEqualTo(policy.Jitter.MinDelay);
            d.Should().BeLessThanOrEqualTo(policy.Jitter.MaxDelay);
        });
    }

    [Fact]
    public void Zero_jitter_produces_zero_delay()
    {
        var engine = CreateEngine();
        var policy = TestFixtures.ZeroJitterPolicy;

        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.Degraded,
            successRate: 0.8);

        var lastTransition = TestFixtures.BaseTime.AddMinutes(-5);

        var decision = engine.Evaluate(
            HealthState.Healthy, assessment, policy, lastTransition);

        decision.ShouldTransition.Should().BeTrue();
        decision.Delay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Degraded_to_Healthy_transition_works()
    {
        var engine = CreateEngine();
        var policy = TestFixtures.ZeroJitterPolicy;

        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.Healthy,
            successRate: 0.95);

        var lastTransition = TestFixtures.BaseTime.AddMinutes(-5);

        var decision = engine.Evaluate(
            HealthState.Degraded, assessment, policy, lastTransition);

        decision.ShouldTransition.Should().BeTrue();
        decision.TargetState.Should().Be(HealthState.Healthy);
    }

    [Fact]
    public void CircuitOpen_to_Healthy_via_recovery()
    {
        var engine = CreateEngine();
        var policy = TestFixtures.ZeroJitterPolicy;

        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.Healthy,
            successRate: 0.95);

        var lastTransition = TestFixtures.BaseTime.AddMinutes(-5);

        var decision = engine.Evaluate(
            HealthState.CircuitOpen, assessment, policy, lastTransition);

        decision.ShouldTransition.Should().BeTrue();
        decision.TargetState.Should().Be(HealthState.Healthy);
    }

    [Fact]
    public void Degraded_to_CircuitOpen_when_rate_drops()
    {
        var engine = CreateEngine();
        var policy = TestFixtures.ZeroJitterPolicy;

        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.CircuitOpen,
            successRate: 0.3);

        var lastTransition = TestFixtures.BaseTime.AddMinutes(-5);

        var decision = engine.Evaluate(
            HealthState.Degraded, assessment, policy, lastTransition);

        decision.ShouldTransition.Should().BeTrue();
        decision.TargetState.Should().Be(HealthState.CircuitOpen);
    }
}

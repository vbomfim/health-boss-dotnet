using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Tests;

public sealed class StateGraphTests
{
    private readonly IStateGraph _graph = new DefaultStateGraph();

    [Fact]
    public void InitialState_is_Healthy()
    {
        _graph.InitialState.Should().Be(HealthState.Healthy);
    }

    [Fact]
    public void AllStates_contains_all_three_states()
    {
        _graph.AllStates.Should().BeEquivalentTo(
            new[] { HealthState.Healthy, HealthState.Degraded, HealthState.CircuitOpen });
    }

    [Fact]
    public void Healthy_has_transition_to_Degraded()
    {
        var transitions = _graph.GetTransitionsFrom(HealthState.Healthy);

        transitions.Should().ContainSingle(t => t.To == HealthState.Degraded);
    }

    [Fact]
    public void Degraded_has_transition_to_CircuitOpen()
    {
        var transitions = _graph.GetTransitionsFrom(HealthState.Degraded);

        transitions.Should().Contain(t => t.To == HealthState.CircuitOpen);
    }

    [Fact]
    public void Degraded_has_transition_to_Healthy()
    {
        var transitions = _graph.GetTransitionsFrom(HealthState.Degraded);

        transitions.Should().Contain(t => t.To == HealthState.Healthy);
    }

    [Fact]
    public void CircuitOpen_has_transition_to_Healthy()
    {
        var transitions = _graph.GetTransitionsFrom(HealthState.CircuitOpen);

        transitions.Should().ContainSingle(t => t.To == HealthState.Healthy);
    }

    [Fact]
    public void Healthy_to_Degraded_guard_fires_when_recommended_Degraded()
    {
        var transition = _graph.GetTransitionsFrom(HealthState.Healthy)
            .Single(t => t.To == HealthState.Degraded);

        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.Degraded,
            successRate: 0.8);

        transition.Guard(assessment).Should().BeTrue();
    }

    [Fact]
    public void Healthy_to_Degraded_guard_does_not_fire_when_Healthy()
    {
        var transition = _graph.GetTransitionsFrom(HealthState.Healthy)
            .Single(t => t.To == HealthState.Degraded);

        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.Healthy,
            successRate: 0.95);

        transition.Guard(assessment).Should().BeFalse();
    }

    [Fact]
    public void Degraded_to_CircuitOpen_guard_fires_when_recommended_CircuitOpen()
    {
        var transition = _graph.GetTransitionsFrom(HealthState.Degraded)
            .Single(t => t.To == HealthState.CircuitOpen);

        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.CircuitOpen,
            successRate: 0.3);

        transition.Guard(assessment).Should().BeTrue();
    }

    [Fact]
    public void Degraded_to_Healthy_guard_fires_when_recommended_Healthy()
    {
        var transition = _graph.GetTransitionsFrom(HealthState.Degraded)
            .Single(t => t.To == HealthState.Healthy);

        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.Healthy,
            successRate: 0.95);

        transition.Guard(assessment).Should().BeTrue();
    }

    [Fact]
    public void CircuitOpen_to_Healthy_guard_fires_when_recommended_Healthy()
    {
        var transition = _graph.GetTransitionsFrom(HealthState.CircuitOpen)
            .Single(t => t.To == HealthState.Healthy);

        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.Healthy,
            successRate: 0.95);

        transition.Guard(assessment).Should().BeTrue();
    }

    [Fact]
    public void All_transitions_have_descriptions()
    {
        foreach (var state in _graph.AllStates)
        {
            var transitions = _graph.GetTransitionsFrom(state);
            foreach (var t in transitions)
            {
                t.Description.Should().NotBeNullOrWhiteSpace();
            }
        }
    }
}

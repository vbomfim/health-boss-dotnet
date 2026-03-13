// <copyright file="TransitionEngine.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Components;

/// <summary>
/// Evaluates state transitions by checking guards from the state graph,
/// enforcing cooldown, and applying jitter to transition delays.
/// </summary>
internal sealed class TransitionEngine : ITransitionEngine
{
    private readonly IStateGraph _stateGraph;

    /// <summary>
    /// Initializes a new instance of the <see cref="TransitionEngine"/> class.
    /// </summary>
    /// <param name="stateGraph">The state graph defining valid transitions.</param>
    public TransitionEngine(IStateGraph stateGraph)
    {
        _stateGraph = stateGraph ?? throw new ArgumentNullException(nameof(stateGraph));
    }

    /// <inheritdoc />
    public TransitionDecision Evaluate(
        HealthState currentState,
        HealthAssessment assessment,
        HealthPolicy policy,
        DateTimeOffset lastTransitionTime)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(policy);
        var timeSinceLastTransition = assessment.EvaluatedAt - lastTransitionTime;
        bool cooldownElapsed = timeSinceLastTransition >= policy.CooldownBeforeTransition;

        var transitions = _stateGraph.GetTransitionsFrom(currentState);

        foreach (var transition in transitions)
        {
            if (!transition.Guard(assessment))
            {
                continue;
            }

            if (!cooldownElapsed)
            {
                return new TransitionDecision(
                    ShouldTransition: false,
                    TargetState: null,
                    Delay: TimeSpan.Zero,
                    Reason: $"Transition to {transition.To} blocked by cooldown " +
                            $"({timeSinceLastTransition.TotalSeconds:F1}s < {policy.CooldownBeforeTransition.TotalSeconds:F1}s)");
            }

            var jitter = ComputeJitter(policy.Jitter);

            return new TransitionDecision(
                ShouldTransition: true,
                TargetState: transition.To,
                Delay: jitter,
                Reason: transition.Description);
        }

        return new TransitionDecision(
            ShouldTransition: false,
            TargetState: null,
            Delay: TimeSpan.Zero,
            Reason: "No guard matched for any transition");
    }

    private static TimeSpan ComputeJitter(JitterConfig jitter)
    {
        if (jitter.MinDelay == jitter.MaxDelay)
        {
            return jitter.MinDelay;
        }

        long minTicks = jitter.MinDelay.Ticks;
        long maxTicks = jitter.MaxDelay.Ticks;
        long randomTicks = Random.Shared.NextInt64(minTicks, maxTicks + 1);

        return TimeSpan.FromTicks(randomTicks);
    }
}

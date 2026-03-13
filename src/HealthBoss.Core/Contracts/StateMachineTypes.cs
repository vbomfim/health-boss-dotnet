// <copyright file="StateMachineTypes.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core.Contracts;

/// <summary>
/// Defines a valid state transition in the health state graph.
/// </summary>
/// <param name="From">The source state.</param>
/// <param name="To">The target state.</param>
/// <param name="Guard">A predicate that must be satisfied for the transition to fire.</param>
/// <param name="Description">A human-readable description of this transition.</param>
public sealed record StateTransition(
    HealthState From,
    HealthState To,
    Func<HealthAssessment, bool> Guard,
    string Description);

/// <summary>
/// The result of evaluating whether a state transition should occur.
/// </summary>
/// <param name="ShouldTransition">Whether a transition should fire.</param>
/// <param name="TargetState">The target state, if transitioning.</param>
/// <param name="Delay">The delay before the transition takes effect (includes jitter).</param>
/// <param name="Reason">Human-readable reason for the decision.</param>
public sealed record TransitionDecision(
    bool ShouldTransition,
    HealthState? TargetState,
    TimeSpan Delay,
    string? Reason);

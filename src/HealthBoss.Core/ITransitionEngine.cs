// <copyright file="ITransitionEngine.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Evaluates whether a state transition should occur based on the current state,
/// assessment, policy, and cooldown constraints.
/// </summary>
public interface ITransitionEngine
{
    /// <summary>
    /// Evaluates potential state transitions and returns a decision.
    /// </summary>
    /// <param name="currentState">The current health state.</param>
    /// <param name="assessment">The latest health assessment.</param>
    /// <param name="policy">The health policy governing transitions.</param>
    /// <param name="lastTransitionTime">When the last transition occurred.</param>
    /// <returns>A decision indicating whether to transition and with what delay.</returns>
    TransitionDecision Evaluate(
        HealthState currentState,
        HealthAssessment assessment,
        HealthPolicy policy,
        DateTimeOffset lastTransitionTime);
}

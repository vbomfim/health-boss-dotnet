// <copyright file="IStateGraph.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Immutable directed graph of valid health state transitions.
/// </summary>
public interface IStateGraph
{
    /// <summary>
    /// Gets the initial state for new dependencies.
    /// </summary>
    HealthState InitialState { get; }

    /// <summary>
    /// Returns all valid transitions from the given state.
    /// </summary>
    /// <param name="state">The source state to query transitions from.</param>
    /// <returns>All transitions originating from the given state.</returns>
    IReadOnlyList<StateTransition> GetTransitionsFrom(HealthState state);

    /// <summary>
    /// Gets all states in the graph.
    /// </summary>
    IReadOnlySet<HealthState> AllStates { get; }
}

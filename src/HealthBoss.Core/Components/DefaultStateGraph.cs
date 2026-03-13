// <copyright file="DefaultStateGraph.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Components;

/// <summary>
/// Default immutable directed graph of health state transitions.
/// Provides the standard transition rules for the three-state model:
/// Healthy ↔ Degraded ↔ CircuitOpen, with recovery paths.
/// </summary>
internal sealed class DefaultStateGraph : IStateGraph
{
    private readonly IReadOnlyDictionary<HealthState, IReadOnlyList<StateTransition>> _transitions;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultStateGraph"/> class
    /// with the default transition rules.
    /// </summary>
    public DefaultStateGraph()
    {
        var transitions = new Dictionary<HealthState, IReadOnlyList<StateTransition>>
        {
            [HealthState.Healthy] = new List<StateTransition>
            {
                new(
                    From: HealthState.Healthy,
                    To: HealthState.Degraded,
                    Guard: a => a.RecommendedState is HealthState.Degraded or HealthState.CircuitOpen,
                    Description: "Success rate dropped below degraded threshold"),
            },
            [HealthState.Degraded] = new List<StateTransition>
            {
                new(
                    From: HealthState.Degraded,
                    To: HealthState.CircuitOpen,
                    Guard: a => a.RecommendedState == HealthState.CircuitOpen,
                    Description: "Success rate dropped below circuit-open threshold"),
                new(
                    From: HealthState.Degraded,
                    To: HealthState.Healthy,
                    Guard: a => a.RecommendedState == HealthState.Healthy,
                    Description: "Success rate recovered above degraded threshold"),
            },
            [HealthState.CircuitOpen] = new List<StateTransition>
            {
                new(
                    From: HealthState.CircuitOpen,
                    To: HealthState.Healthy,
                    Guard: a => a.RecommendedState == HealthState.Healthy,
                    Description: "Recovery probe succeeded, circuit closing"),
            },
        };

        _transitions = transitions;
    }

    /// <inheritdoc />
    public HealthState InitialState => HealthState.Healthy;

    /// <inheritdoc />
    public IReadOnlySet<HealthState> AllStates { get; } =
        new HashSet<HealthState> { HealthState.Healthy, HealthState.Degraded, HealthState.CircuitOpen };

    /// <inheritdoc />
    public IReadOnlyList<StateTransition> GetTransitionsFrom(HealthState state) =>
        _transitions.TryGetValue(state, out var transitions)
            ? transitions
            : Array.Empty<StateTransition>();
}

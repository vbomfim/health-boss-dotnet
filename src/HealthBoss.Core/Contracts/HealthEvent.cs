// <copyright file="HealthEvent.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core.Contracts;

/// <summary>
/// Event raised when a dependency's health state transitions.
/// Dispatched to <see cref="IHealthEventSink"/> implementations for downstream processing.
/// <para>
/// <strong>No PII</strong> — contains only validated identifiers and status enums.
/// </para>
/// </summary>
/// <param name="DependencyId">The dependency that transitioned.</param>
/// <param name="PreviousState">The health state before the transition.</param>
/// <param name="NewState">The health state after the transition.</param>
/// <param name="OccurredAt">When the state transition was detected.</param>
public sealed record HealthEvent(
    DependencyId DependencyId,
    HealthState PreviousState,
    HealthState NewState,
    DateTimeOffset OccurredAt);

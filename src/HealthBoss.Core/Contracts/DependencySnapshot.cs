// <copyright file="DependencySnapshot.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core.Contracts;

/// <summary>
/// A point-in-time snapshot of a single dependency's health state.
/// </summary>
/// <param name="DependencyId">The dependency identifier.</param>
/// <param name="CurrentState">The current health state.</param>
/// <param name="LatestAssessment">The most recent health assessment.</param>
/// <param name="StateChangedAt">When the state last changed.</param>
/// <param name="ConsecutiveFailures">Number of consecutive failures.</param>
public sealed record DependencySnapshot(
    DependencyId DependencyId,
    HealthState CurrentState,
    HealthAssessment LatestAssessment,
    DateTimeOffset StateChangedAt,
    int ConsecutiveFailures);

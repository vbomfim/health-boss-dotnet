// <copyright file="Reports.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core.Contracts;

/// <summary>
/// Aggregated health report across all monitored dependencies.
/// </summary>
/// <param name="Status">The overall health status.</param>
/// <param name="Dependencies">Per-dependency snapshots.</param>
/// <param name="GeneratedAt">When this report was generated.</param>
public sealed record HealthReport(
    HealthStatus Status,
    IReadOnlyList<DependencySnapshot> Dependencies,
    DateTimeOffset GeneratedAt);

/// <summary>
/// Aggregated readiness report including startup and drain status.
/// </summary>
/// <param name="Status">The overall readiness status.</param>
/// <param name="Dependencies">Per-dependency snapshots.</param>
/// <param name="GeneratedAt">When this report was generated.</param>
/// <param name="StartupStatus">The current startup lifecycle status.</param>
/// <param name="DrainStatus">The current drain lifecycle status.</param>
public sealed record ReadinessReport(
    ReadinessStatus Status,
    IReadOnlyList<DependencySnapshot> Dependencies,
    DateTimeOffset GeneratedAt,
    StartupStatus StartupStatus,
    DrainStatus DrainStatus);

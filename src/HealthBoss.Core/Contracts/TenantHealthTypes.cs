// <copyright file="TenantHealthTypes.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core.Contracts;

/// <summary>
/// Represents the health status of a tenant for a specific component.
/// Separate from <see cref="HealthState"/> — tenant health is an isolated dimension
/// that does NOT affect service-level probes.
/// </summary>
public enum TenantHealthStatus
{
    /// <summary>The tenant is operating normally.</summary>
    Healthy,

    /// <summary>The tenant is experiencing partial failures.</summary>
    Degraded,

    /// <summary>The tenant is considered unavailable.</summary>
    Unavailable,
}

/// <summary>
/// Immutable assessment of a tenant's health for a specific component.
/// </summary>
/// <param name="TenantId">The tenant that was assessed.</param>
/// <param name="Component">The component this assessment relates to.</param>
/// <param name="Status">The computed health status.</param>
/// <param name="SuccessRate">The ratio of successful signals (0.0–1.0).</param>
/// <param name="TotalSignals">Total number of signals recorded.</param>
/// <param name="FailureCount">Number of failed signals.</param>
/// <param name="LastSignalAt">When the last signal was recorded, or null if no signals.</param>
public sealed record TenantHealthAssessment(
    TenantId TenantId,
    DependencyId Component,
    TenantHealthStatus Status,
    double SuccessRate,
    int TotalSignals,
    int FailureCount,
    DateTimeOffset? LastSignalAt);

/// <summary>
/// Event raised when a tenant's health status changes.
/// Dispatched to <see cref="HealthBoss.Core.IHealthEventSink"/> for downstream processing.
/// </summary>
/// <param name="Component">The component where the status changed.</param>
/// <param name="TenantId">The affected tenant.</param>
/// <param name="PreviousStatus">The previous health status.</param>
/// <param name="NewStatus">The new health status.</param>
/// <param name="SuccessRate">The success rate at the time of the change.</param>
/// <param name="OccurredAt">When the status change was detected.</param>
public sealed record TenantHealthEvent(
    DependencyId Component,
    TenantId TenantId,
    TenantHealthStatus PreviousStatus,
    TenantHealthStatus NewStatus,
    double SuccessRate,
    DateTimeOffset OccurredAt);

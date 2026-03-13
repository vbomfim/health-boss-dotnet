// <copyright file="ITenantHealthTracker.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Tracks per-tenant health signals for monitored components.
/// Tenant health is an isolated dimension — it does NOT affect service-level probes.
/// Provides sliding window tracking with LRU + TTL eviction.
/// </summary>
public interface ITenantHealthTracker
{
    /// <summary>
    /// Records a successful operation for the specified tenant and component.
    /// </summary>
    /// <param name="component">The component identifier.</param>
    /// <param name="tenantId">The tenant identifier.</param>
    void RecordSuccess(DependencyId component, TenantId tenantId);

    /// <summary>
    /// Records a failed operation for the specified tenant and component.
    /// </summary>
    /// <param name="component">The component identifier.</param>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="reason">Optional failure reason.</param>
    void RecordFailure(DependencyId component, TenantId tenantId, string? reason = null);

    /// <summary>
    /// Returns the current health assessment for a specific tenant and component.
    /// Returns a default healthy assessment if no signals have been recorded.
    /// </summary>
    /// <param name="component">The component identifier.</param>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <returns>The tenant's health assessment.</returns>
    TenantHealthAssessment GetTenantHealth(DependencyId component, TenantId tenantId);

    /// <summary>
    /// Returns health assessments for all active tenants of a component.
    /// Only includes tenants that have not been evicted.
    /// </summary>
    /// <param name="component">The component identifier.</param>
    /// <returns>A dictionary of tenant assessments keyed by tenant identifier.</returns>
    IReadOnlyDictionary<TenantId, TenantHealthAssessment> GetAllTenantHealth(DependencyId component);

    /// <summary>
    /// Returns the number of active (non-evicted) tenants for a component.
    /// </summary>
    /// <param name="component">The component identifier.</param>
    /// <returns>The count of active tenants.</returns>
    int ActiveTenantCount(DependencyId component);
}

// <copyright file="ITenantHealthProvider.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Provides per-tenant health data for a monitored dependency.
/// Implementations aggregate tenant-level signals and expose them for the tenant health endpoint.
/// </summary>
/// <remarks>
/// This interface is optional. When not registered in DI, the tenant health endpoint
/// returns an empty response. Implementations must be thread-safe for concurrent reads.
/// </remarks>
public interface ITenantHealthProvider
{
    /// <summary>
    /// Returns all tenant health assessments for the specified component,
    /// or <c>null</c> if the component is not tracked.
    /// </summary>
    /// <param name="component">The dependency component to query.</param>
    /// <returns>A read-only dictionary keyed by <see cref="TenantId"/>, or <c>null</c>.</returns>
    IReadOnlyDictionary<TenantId, TenantHealthAssessment>? GetAllTenantHealth(DependencyId component);

    /// <summary>
    /// Returns the number of active tenants for the specified component.
    /// </summary>
    /// <param name="component">The dependency component to query.</param>
    /// <returns>The count of active tenants, or 0 if the component is not tracked.</returns>
    int ActiveTenantCount(DependencyId component);
}

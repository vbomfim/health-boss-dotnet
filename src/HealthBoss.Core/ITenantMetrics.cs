// <copyright file="ITenantMetrics.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core;

/// <summary>
/// Metrics contract for tenant-level tracking: tenant count gauge.
/// <para>
/// Consumers: <c>TenantHealthStore</c>.
/// </para>
/// </summary>
/// <remarks>
/// Split from <see cref="IHealthBossMetrics"/> per Interface Segregation Principle (ISP).
/// See GitHub Issue #61.
/// </remarks>
public interface ITenantMetrics
{
    /// <summary>
    /// Records a tenant health status change for a component.
    /// Instrument: <c>healthboss.tenant.status_changes</c>.
    /// </summary>
    /// <param name="component">The component name (dependency ID).</param>
    /// <param name="tenantId">The affected tenant identifier.</param>
    /// <param name="fromStatus">The previous health status.</param>
    /// <param name="toStatus">The new health status.</param>
    /// <remarks>
    /// <para><b>Cardinality warning:</b> This method produces one time series per unique
    /// combination of <paramref name="component"/> × <paramref name="tenantId"/> ×
    /// <paramref name="fromStatus"/> × <paramref name="toStatus"/>. In multi-tenant systems
    /// with thousands of tenants, this can cause <b>cardinality explosion</b> in your TSDB
    /// (Prometheus, Azure Monitor, etc.).</para>
    /// <para>Mitigations: cap the number of tracked tenants, use an allow-list, or aggregate
    /// tenant metrics into bucketed groups. See <c>docs/METRICS-CARDINALITY.md</c>.</para>
    /// </remarks>
    void RecordTenantStatusChange(string component, string tenantId, string fromStatus, string toStatus);

    /// <summary>
    /// Sets the tenant count for a component (observable gauge).
    /// Instrument: <c>healthboss.tenant_count</c>.
    /// </summary>
    /// <param name="component">The component name (dependency ID).</param>
    /// <param name="count">The number of tenants.</param>
    /// <remarks>
    /// <b>Cardinality warning:</b> The <paramref name="component"/> tag creates one time series
    /// per unique dependency ID. See <c>docs/METRICS-CARDINALITY.md</c> for best practices.
    /// </remarks>
    void SetTenantCount(string component, int count);
}

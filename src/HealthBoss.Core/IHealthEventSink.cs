// <copyright file="IHealthEventSink.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Receives health-related events for downstream processing (logging, metrics, alerting).
/// <para>
/// <strong>Trust model:</strong> Sinks are privileged code running in-process.
/// They receive validated, non-PII event data (identifiers + status enums only).
/// The <see cref="IEventSinkDispatcher"/> wraps each sink call in error isolation
/// and rate limiting to prevent a misbehaving sink from affecting health evaluation.
/// </para>
/// </summary>
public interface IHealthEventSink
{
    /// <summary>
    /// Called when a dependency health state transitions (e.g., Healthy → Degraded).
    /// </summary>
    /// <param name="healthEvent">The state transition event.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task OnHealthStateChanged(HealthEvent healthEvent, CancellationToken ct = default);

    /// <summary>
    /// Called when a tenant's health status changes (e.g., Healthy → Unavailable).
    /// </summary>
    /// <param name="tenantEvent">The tenant health change event.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task OnTenantHealthChanged(TenantHealthEvent tenantEvent, CancellationToken ct = default);
}

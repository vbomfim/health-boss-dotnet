// <copyright file="OpenTelemetryMetricEventSink.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Components;

/// <summary>
/// Event sink that bridges <see cref="IHealthEventSink"/> events to the consolidated
/// <c>HealthBoss</c> meter via <see cref="IStateMachineMetrics"/> and <see cref="ITenantMetrics"/>.
/// <para>
/// This is a thin adapter — it owns no <see cref="System.Diagnostics.Metrics.Meter"/>
/// and creates no instruments. All metric recording is delegated to the canonical
/// <see cref="HealthBossMetrics"/> singleton, ensuring a single meter ("HealthBoss")
/// is the sole source of truth for all instruments.
/// </para>
/// <para>
/// <strong>Mapped events:</strong>
/// <list type="bullet">
///   <item><see cref="OnHealthStateChanged"/> → <see cref="IStateMachineMetrics.RecordStateTransition"/>
///         (instrument: <c>healthboss.state_transitions</c>)</item>
///   <item><see cref="OnTenantHealthChanged"/> → <see cref="ITenantMetrics.RecordTenantStatusChange"/>
///         (instrument: <c>healthboss.tenant.status_changes</c>)</item>
/// </list>
/// </para>
/// <para>
/// <strong>Trust model:</strong> This sink is privileged code running in-process.
/// It receives validated, non-PII data (identifiers + status enums only).
/// Metric tags contain only DependencyId, TenantId, and status values.
/// </para>
/// </summary>
internal sealed class OpenTelemetryMetricEventSink : IHealthEventSink
{
    private readonly IStateMachineMetrics _stateMachineMetrics;
    private readonly ITenantMetrics _tenantMetrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenTelemetryMetricEventSink"/> class.
    /// </summary>
    /// <param name="stateMachineMetrics">State machine metrics for recording state transitions.</param>
    /// <param name="tenantMetrics">Tenant metrics for recording tenant status changes.</param>
    internal OpenTelemetryMetricEventSink(IStateMachineMetrics stateMachineMetrics, ITenantMetrics tenantMetrics)
    {
        ArgumentNullException.ThrowIfNull(stateMachineMetrics);
        ArgumentNullException.ThrowIfNull(tenantMetrics);

        _stateMachineMetrics = stateMachineMetrics;
        _tenantMetrics = tenantMetrics;
    }

    /// <inheritdoc />
    public Task OnHealthStateChanged(HealthEvent healthEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(healthEvent);

        _stateMachineMetrics.RecordStateTransition(
            healthEvent.DependencyId.Value,
            EnumStringCache.HealthStateNames[healthEvent.PreviousState],
            EnumStringCache.HealthStateNames[healthEvent.NewState]);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnTenantHealthChanged(TenantHealthEvent tenantEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenantEvent);

        _tenantMetrics.RecordTenantStatusChange(
            tenantEvent.Component.Value,
            tenantEvent.TenantId.Value,
            EnumStringCache.TenantHealthStatusNames[tenantEvent.PreviousStatus],
            EnumStringCache.TenantHealthStatusNames[tenantEvent.NewStatus]);

        return Task.CompletedTask;
    }
}

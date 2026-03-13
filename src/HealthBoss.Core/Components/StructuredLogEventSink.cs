// <copyright file="StructuredLogEventSink.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;
using Microsoft.Extensions.Logging;

namespace HealthBoss.Core.Components;

/// <summary>
/// Event sink that logs health events via <see cref="ILogger"/> at appropriate severity levels.
/// <para>
/// Log level mapping:
/// <list type="bullet">
///   <item><strong>State transitions</strong>: <see cref="LogLevel.Information"/> for all transitions.</item>
///   <item><strong>Tenant Healthy</strong>: <see cref="LogLevel.Information"/>.</item>
///   <item><strong>Tenant Degraded</strong>: <see cref="LogLevel.Warning"/>.</item>
///   <item><strong>Tenant Unavailable</strong>: <see cref="LogLevel.Error"/>.</item>
/// </list>
/// </para>
/// <para>
/// <strong>Trust model:</strong> This sink is privileged code running in-process.
/// It receives validated, non-PII data (identifiers + status enums only).
/// </para>
/// </summary>
internal sealed class StructuredLogEventSink : IHealthEventSink
{
    private readonly ILogger<StructuredLogEventSink> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StructuredLogEventSink"/> class.
    /// </summary>
    /// <param name="logger">The logger to write health events to.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="logger"/> is <c>null</c>.</exception>
    internal StructuredLogEventSink(ILogger<StructuredLogEventSink> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task OnHealthStateChanged(HealthEvent healthEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(healthEvent);

        _logger.LogInformation(
            "Dependency {DependencyId} transitioned from {PreviousState} to {NewState} at {OccurredAt}",
            healthEvent.DependencyId,
            healthEvent.PreviousState,
            healthEvent.NewState,
            healthEvent.OccurredAt);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnTenantHealthChanged(TenantHealthEvent tenantEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenantEvent);

        switch (tenantEvent.NewStatus)
        {
            case TenantHealthStatus.Healthy:
                _logger.LogInformation(
                    "Tenant {TenantId} on {Component} recovered to {NewStatus} from {PreviousStatus} (success rate: {SuccessRate:P1})",
                    tenantEvent.TenantId,
                    tenantEvent.Component,
                    tenantEvent.NewStatus,
                    tenantEvent.PreviousStatus,
                    tenantEvent.SuccessRate);
                break;

            case TenantHealthStatus.Degraded:
                _logger.LogWarning(
                    "Tenant {TenantId} on {Component} degraded to {NewStatus} from {PreviousStatus} (success rate: {SuccessRate:P1})",
                    tenantEvent.TenantId,
                    tenantEvent.Component,
                    tenantEvent.NewStatus,
                    tenantEvent.PreviousStatus,
                    tenantEvent.SuccessRate);
                break;

            case TenantHealthStatus.Unavailable:
                _logger.LogError(
                    "Tenant {TenantId} on {Component} is {NewStatus} from {PreviousStatus} (success rate: {SuccessRate:P1})",
                    tenantEvent.TenantId,
                    tenantEvent.Component,
                    tenantEvent.NewStatus,
                    tenantEvent.PreviousStatus,
                    tenantEvent.SuccessRate);
                break;
        }

        return Task.CompletedTask;
    }
}

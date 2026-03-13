// <copyright file="TenantHealthResponseWriter.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using System.Text.Json;
using HealthBoss.Core;
using HealthBoss.Core.Contracts;

namespace HealthBoss.AspNetCore;

/// <summary>
/// Serializes tenant health data to JSON with snake_case keys.
/// </summary>
internal static class TenantHealthResponseWriter
{
    // ── Tenant Health ─────────────────────────────────────────

    /// <summary>
    /// Writes the tenant health response for all components in the health report.
    /// Each component includes its active tenant count and per-tenant breakdown.
    /// </summary>
    /// <param name="report">The current health report for component enumeration.</param>
    /// <param name="provider">The optional tenant health provider. When null, returns empty tenant data.</param>
    /// <returns>A JSON string with tenant health data.</returns>
    internal static string WriteTenantHealthResponse(
        HealthReport report,
        ITenantHealthProvider? provider)
    {
        var components = report.Dependencies.Select(dep =>
            CreateComponentTenantResponse(dep.DependencyId, provider)).ToList();

        var body = new TenantHealthResponse(components);
        return JsonSerializer.Serialize(body, body.GetType(), ProbeResponseWriter.JsonOptions);
    }

    /// <summary>
    /// Writes the 403 Forbidden response for non-Full detail levels.
    /// </summary>
    /// <returns>A JSON string with the error message.</returns>
    internal static string WriteForbiddenResponse()
    {
        var body = new ForbiddenResponse("tenant_health_requires_full_detail_level");
        return JsonSerializer.Serialize(body, body.GetType(), ProbeResponseWriter.JsonOptions);
    }

    // ── Helpers ───────────────────────────────────────────────

    private static string ToSnakeCaseString<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static ComponentTenantEntry CreateComponentTenantResponse(
        DependencyId component,
        ITenantHealthProvider? provider)
    {
        if (provider is null)
        {
            return new ComponentTenantEntry(
                component.ToString(),
                ActiveTenants: 0,
                Tenants: []);
        }

        var activeTenantCount = provider.ActiveTenantCount(component);
        var tenantHealth = provider.GetAllTenantHealth(component);

        var tenantEntries = tenantHealth?.Select(kvp => new TenantEntry(
            kvp.Key.ToString(),
            ToSnakeCaseString(kvp.Value.Status),
            kvp.Value.SuccessRate,
            kvp.Value.TotalSignals,
            kvp.Value.FailureCount)).ToList()
            ?? [];

        return new ComponentTenantEntry(
            component.ToString(),
            activeTenantCount,
            tenantEntries);
    }

    // ── Response DTOs (internal for testability) ──────────────

    internal sealed record TenantHealthResponse(
        IReadOnlyList<ComponentTenantEntry> Components);

    internal sealed record ComponentTenantEntry(
        string Component,
        int ActiveTenants,
        IReadOnlyList<TenantEntry> Tenants);

    internal sealed record TenantEntry(
        string TenantId,
        string Status,
        double SuccessRate,
        int TotalSignals,
        int FailureCount);

    internal sealed record ForbiddenResponse(string Error);
}

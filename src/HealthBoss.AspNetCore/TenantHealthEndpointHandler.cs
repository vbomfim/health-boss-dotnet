// <copyright file="TenantHealthEndpointHandler.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core;
using HealthBoss.Core.Contracts;
using Microsoft.AspNetCore.Http;

namespace HealthBoss.AspNetCore;

/// <summary>
/// Handles tenant health HTTP requests.
/// Returns per-tenant health data only at <see cref="DetailLevel.Full"/>;
/// returns 403 Forbidden at lower detail levels (Security Finding #6).
/// </summary>
internal static class TenantHealthEndpointHandler
{
    private const string JsonContentType = "application/json";

    /// <summary>
    /// Handles a tenant health request for all registered components.
    /// </summary>
    /// <param name="provider">The optional tenant health provider (may be null if not registered).</param>
    /// <param name="reportProvider">The health report provider for component enumeration.</param>
    /// <param name="detailLevel">The configured detail level.</param>
    /// <returns>An <see cref="IResult"/> containing the tenant health response.</returns>
    internal static IResult HandleTenantHealth(
        ITenantHealthProvider? provider,
        HealthBoss.Core.IHealthReportProvider reportProvider,
        DetailLevel detailLevel)
    {
        if (detailLevel != DetailLevel.Full)
        {
            return Results.Text(
                TenantHealthResponseWriter.WriteForbiddenResponse(),
                JsonContentType,
                statusCode: StatusCodes.Status403Forbidden);
        }

        try
        {
            var report = reportProvider.GetHealthReport();
            var json = TenantHealthResponseWriter.WriteTenantHealthResponse(report, provider);
            return Results.Text(json, JsonContentType, statusCode: StatusCodes.Status200OK);
        }
        catch (OperationCanceledException)
        {
            // Let request cancellation propagate — the host pipeline handles it.
            throw;
        }
        catch (Exception)
        {
            var json = ProbeResponseWriter.WriteErrorResponse();
            return Results.Text(json, JsonContentType, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}

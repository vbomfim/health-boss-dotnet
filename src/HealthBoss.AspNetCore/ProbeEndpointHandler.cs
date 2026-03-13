using HealthBoss.Core;
using HealthBoss.Core.Contracts;
using Microsoft.AspNetCore.Http;

namespace HealthBoss.AspNetCore;

/// <summary>
/// Handles K8s health probe HTTP requests.
/// Maps health data to HTTP status codes and JSON responses.
/// </summary>
internal static class ProbeEndpointHandler
{
    private const string JsonContentType = "application/json";

    internal static IResult HandleLiveness(
        HealthBoss.Core.IHealthReportProvider provider,
        DetailLevel detailLevel)
    {
        try
        {
            var report = provider.GetHealthReport();
            var statusCode = ProbeResponseWriter.GetLivenessStatusCode(report.Status);
            var json = ProbeResponseWriter.WriteLivenessResponse(report, detailLevel);
            return Results.Text(json, JsonContentType, statusCode: statusCode);
        }
        catch
        {
            var json = ProbeResponseWriter.WriteErrorResponse();
            return Results.Text(json, JsonContentType, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    internal static IResult HandleReadiness(
        HealthBoss.Core.IHealthReportProvider provider,
        DetailLevel detailLevel)
    {
        try
        {
            var report = provider.GetReadinessReport();
            var statusCode = ProbeResponseWriter.GetReadinessStatusCode(report.Status);
            var json = ProbeResponseWriter.WriteReadinessResponse(report, detailLevel);
            return Results.Text(json, JsonContentType, statusCode: statusCode);
        }
        catch
        {
            var json = ProbeResponseWriter.WriteErrorResponse();
            return Results.Text(json, JsonContentType, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    internal static IResult HandleStartup(HealthBoss.Core.IStartupTracker tracker)
    {
        try
        {
            var status = tracker.Status;
            var statusCode = ProbeResponseWriter.GetStartupStatusCode(status);
            var json = ProbeResponseWriter.WriteStartupResponse(status);
            return Results.Text(json, JsonContentType, statusCode: statusCode);
        }
        catch
        {
            var json = ProbeResponseWriter.WriteErrorResponse();
            return Results.Text(json, JsonContentType, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}

using System.Text.Json;
using HealthBoss.Core.Contracts;

namespace HealthBoss.AspNetCore;

/// <summary>
/// Serializes health probe data to JSON with snake_case keys.
/// </summary>
internal static class ProbeResponseWriter
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    // ── Liveness ──────────────────────────────────────────────

    internal static string WriteLivenessResponse(HealthReport report, DetailLevel level)
    {
        object body = level switch
        {
            DetailLevel.Summary => CreateLivenessSummary(report),
            DetailLevel.Full => CreateLivenessFull(report),
            _ => new StatusOnlyResponse(ToSnakeCaseString(report.Status)),
        };

        return JsonSerializer.Serialize(body, body.GetType(), JsonOptions);
    }

    internal static int GetLivenessStatusCode(HealthStatus status) =>
        status == HealthStatus.Unhealthy ? 503 : 200;

    // ── Readiness ─────────────────────────────────────────────

    internal static string WriteReadinessResponse(ReadinessReport report, DetailLevel level)
    {
        object body = level switch
        {
            DetailLevel.Summary => CreateReadinessSummary(report),
            DetailLevel.Full => CreateReadinessFull(report),
            _ => new StatusOnlyResponse(ToSnakeCaseString(report.Status)),
        };

        return JsonSerializer.Serialize(body, body.GetType(), JsonOptions);
    }

    internal static int GetReadinessStatusCode(ReadinessStatus status) =>
        status == ReadinessStatus.NotReady ? 503 : 200;

    // ── Startup ───────────────────────────────────────────────

    internal static string WriteStartupResponse(StartupStatus status)
    {
        var body = new StatusOnlyResponse(ToSnakeCaseString(status));
        return JsonSerializer.Serialize(body, JsonOptions);
    }

    internal static int GetStartupStatusCode(StartupStatus status) =>
        status == StartupStatus.Failed ? 503 : 200;

    // ── Error ─────────────────────────────────────────────────

    internal static string WriteErrorResponse()
    {
        var body = new StatusOnlyResponse("unhealthy");
        return JsonSerializer.Serialize(body, JsonOptions);
    }

    // ── Helpers ───────────────────────────────────────────────

    private static string ToSnakeCaseString<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static LivenessSummaryResponse CreateLivenessSummary(HealthReport report) =>
        new(
            ToSnakeCaseString(report.Status),
            report.Dependencies.Select(d => new DependencySummaryEntry(
                d.DependencyId.ToString(),
                ToSnakeCaseString(d.CurrentState))).ToList());

    private static LivenessFullResponse CreateLivenessFull(HealthReport report) =>
        new(
            ToSnakeCaseString(report.Status),
            report.Dependencies.Select(d => new DependencyFullEntry(
                d.DependencyId.ToString(),
                ToSnakeCaseString(d.CurrentState),
                d.LatestAssessment.SuccessRate,
                d.LatestAssessment.TotalSignals,
                d.LatestAssessment.FailureCount,
                d.LatestAssessment.SuccessCount)).ToList());

    private static ReadinessSummaryResponse CreateReadinessSummary(ReadinessReport report) =>
        new(
            ToSnakeCaseString(report.Status),
            ToSnakeCaseString(report.StartupStatus),
            ToSnakeCaseString(report.DrainStatus),
            report.Dependencies.Select(d => new DependencySummaryEntry(
                d.DependencyId.ToString(),
                ToSnakeCaseString(d.CurrentState))).ToList());

    private static ReadinessFullResponse CreateReadinessFull(ReadinessReport report) =>
        new(
            ToSnakeCaseString(report.Status),
            ToSnakeCaseString(report.StartupStatus),
            ToSnakeCaseString(report.DrainStatus),
            report.Dependencies.Select(d => new DependencyFullEntry(
                d.DependencyId.ToString(),
                ToSnakeCaseString(d.CurrentState),
                d.LatestAssessment.SuccessRate,
                d.LatestAssessment.TotalSignals,
                d.LatestAssessment.FailureCount,
                d.LatestAssessment.SuccessCount)).ToList());

    // ── Response DTOs (internal for testability) ──────────────

    internal sealed record StatusOnlyResponse(string Status);

    internal sealed record LivenessSummaryResponse(
        string Status,
        IReadOnlyList<DependencySummaryEntry> Dependencies);

    internal sealed record LivenessFullResponse(
        string Status,
        IReadOnlyList<DependencyFullEntry> Dependencies);

    internal sealed record ReadinessSummaryResponse(
        string Status,
        string StartupStatus,
        string DrainStatus,
        IReadOnlyList<DependencySummaryEntry> Dependencies);

    internal sealed record ReadinessFullResponse(
        string Status,
        string StartupStatus,
        string DrainStatus,
        IReadOnlyList<DependencyFullEntry> Dependencies);

    internal sealed record DependencySummaryEntry(string Name, string State);

    internal sealed record DependencyFullEntry(
        string Name,
        string State,
        double SuccessRate,
        int TotalSignals,
        int FailureCount,
        int SuccessCount);
}

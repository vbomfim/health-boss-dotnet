using HealthBoss.Core.Contracts;

namespace HealthBoss.AspNetCore;

/// <summary>
/// Configuration options for HealthBoss probe endpoints.
/// </summary>
public sealed class HealthBossEndpointOptions
{
    /// <summary>
    /// Path for the liveness probe. Default: <c>/healthz/live</c>.
    /// </summary>
    public string LivenessPath { get; set; } = "/healthz/live";

    /// <summary>
    /// Path for the readiness probe. Default: <c>/healthz/ready</c>.
    /// </summary>
    public string ReadinessPath { get; set; } = "/healthz/ready";

    /// <summary>
    /// Path for the startup probe. Default: <c>/healthz/startup</c>.
    /// </summary>
    public string StartupPath { get; set; } = "/healthz/startup";

    /// <summary>
    /// Path for the tenant health endpoint. Default: <c>/healthz/tenants</c>.
    /// <para>
    /// This endpoint is only accessible at <see cref="DetailLevel.Full"/>.
    /// Lower detail levels return 403 Forbidden (Security Finding #6).
    /// <b>This endpoint should be behind authentication in production.</b>
    /// </para>
    /// </summary>
    public string TenantHealthPath { get; set; } = "/healthz/tenants";

    /// <summary>
    /// Default detail level for probe responses.
    /// <see cref="DetailLevel.StatusOnly"/> is the default (Security Finding #1 — CRITICAL).
    /// K8s probes only need HTTP status codes; minimal bodies reduce information leakage.
    /// </summary>
    /// <remarks>
    /// <b>Production hardening:</b> All HealthBoss endpoints are registered without
    /// authorization by default. In production, apply authentication and authorization
    /// by chaining <c>RequireAuthorization()</c> on the endpoint group:
    /// <code>
    /// app.MapHealthBoss(options)
    ///    .RequireAuthorization("HealthOpsPolicy");
    /// </code>
    /// For Kubernetes liveness/readiness probes, consider excluding those paths
    /// from the authorization policy via a tag or separate mapping.
    /// </remarks>
    public DetailLevel DefaultDetailLevel { get; set; } = DetailLevel.StatusOnly;
}

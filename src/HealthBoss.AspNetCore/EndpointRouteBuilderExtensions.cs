using HealthBoss.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HealthBoss.AspNetCore;

/// <summary>
/// Extension methods for mapping HealthBoss probe endpoints.
/// </summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps HealthBoss K8s health probe endpoints (liveness, readiness, startup).
    /// <para>
    /// Requires <see cref="HealthBoss.Core.IHealthReportProvider"/> and <see cref="HealthBoss.Core.IStartupTracker"/>
    /// to be registered in the dependency injection container (via <c>AddHealthBoss()</c>).
    /// </para>
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="configure">Optional configuration for endpoint paths and default detail level.</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapHealthBossEndpoints(
        this IEndpointRouteBuilder endpoints,
        Action<HealthBossEndpointOptions>? configure = null)
    {
        var options = new HealthBossEndpointOptions();
        configure?.Invoke(options);

        var detailLevel = options.DefaultDetailLevel;

        endpoints.MapGet(options.LivenessPath, (HealthBoss.Core.IHealthReportProvider provider) =>
            ProbeEndpointHandler.HandleLiveness(provider, detailLevel))
            .ExcludeFromDescription();

        endpoints.MapGet(options.ReadinessPath, (HealthBoss.Core.IHealthReportProvider provider) =>
            ProbeEndpointHandler.HandleReadiness(provider, detailLevel))
            .ExcludeFromDescription();

        endpoints.MapGet(options.StartupPath, (HealthBoss.Core.IStartupTracker tracker) =>
            ProbeEndpointHandler.HandleStartup(tracker))
            .ExcludeFromDescription();

        endpoints.MapGet(options.TenantHealthPath, (
            HealthBoss.Core.IHealthReportProvider provider,
            HttpContext context) =>
        {
            var tenantProvider = context.RequestServices.GetService<ITenantHealthProvider>();
            return TenantHealthEndpointHandler.HandleTenantHealth(tenantProvider, provider, detailLevel);
        }).ExcludeFromDescription();

        return endpoints;
    }
}

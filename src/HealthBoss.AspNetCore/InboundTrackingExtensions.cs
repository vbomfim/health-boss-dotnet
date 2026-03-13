using Microsoft.AspNetCore.Builder;

namespace HealthBoss.AspNetCore;

/// <summary>
/// Extension methods for adding HealthBoss inbound HTTP request tracking to the ASP.NET Core pipeline.
/// </summary>
public static class InboundTrackingExtensions
{
    /// <summary>
    /// Adds the HealthBoss inbound tracking middleware to the application pipeline.
    /// Records HTTP request success/failure as health signals for each request.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <param name="configure">
    /// Optional action to configure tracking options such as route mappings,
    /// excluded paths, and failure classification.
    /// </param>
    /// <returns>The application builder for chaining.</returns>
    /// <example>
    /// <code>
    /// app.UseHealthBossInboundTracking(options =>
    /// {
    ///     options.Map("/api/payments", "payments-handler");
    ///     options.Map("/api/users", "users-handler");
    ///     options.DefaultComponent = "api";
    ///     options.ExcludePaths = ["/healthz", "/healthz/ready"];
    ///     options.IsFailure = statusCode => statusCode >= 500;
    /// });
    /// </code>
    /// </example>
    public static IApplicationBuilder UseHealthBossInboundTracking(
        this IApplicationBuilder app,
        Action<InboundTrackingOptions>? configure = null)
    {
        var options = new InboundTrackingOptions();
        configure?.Invoke(options);

        // Validate DefaultComponent is a valid DependencyId at startup,
        // not at request time — fail fast.
        _ = new HealthBoss.Core.Contracts.DependencyId(options.DefaultComponent);

        return app.UseMiddleware<InboundHealthMiddleware>(options);
    }
}

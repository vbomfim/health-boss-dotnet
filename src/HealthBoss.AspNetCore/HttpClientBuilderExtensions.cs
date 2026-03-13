// <copyright file="HttpClientBuilderExtensions.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace HealthBoss.AspNetCore;

/// <summary>
/// Extension methods for adding HealthBoss outbound HTTP tracking to <see cref="IHttpClientBuilder"/>.
/// </summary>
public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// Adds a <see cref="HealthBossDelegatingHandler"/> to the HTTP client pipeline
    /// that records outbound call success/failure as health signals.
    /// </summary>
    /// <param name="builder">The <see cref="IHttpClientBuilder"/> to configure.</param>
    /// <param name="configure">
    /// A delegate to configure <see cref="OutboundTrackingOptions"/>.
    /// At minimum, <see cref="OutboundTrackingOptions.ComponentName"/> must be set.
    /// </param>
    /// <returns>The <see cref="IHttpClientBuilder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="builder"/> or <paramref name="configure"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <see cref="OutboundTrackingOptions.ComponentName"/> is empty or invalid.
    /// </exception>
    public static IHttpClientBuilder AddHealthBossOutboundTracking(
        this IHttpClientBuilder builder,
        Action<OutboundTrackingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new OutboundTrackingOptions();
        configure(options);

        // Validate eagerly at registration time for fast-fail developer experience.
        // DependencyId.Create throws ArgumentException on invalid names.
        _ = DependencyId.Create(options.ComponentName);

        return builder.AddHttpMessageHandler(serviceProvider =>
        {
            var signalBuffer = serviceProvider.GetRequiredService<ISignalBuffer>();
            var clock = serviceProvider.GetRequiredService<ISystemClock>();
            return new HealthBossDelegatingHandler(signalBuffer, clock, options);
        });
    }
}

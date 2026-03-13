// <copyright file="OutboundTrackingOptions.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.AspNetCore;

/// <summary>
/// Configuration options for outbound HTTP call tracking via <see cref="HealthBossDelegatingHandler"/>.
/// </summary>
public sealed class OutboundTrackingOptions
{
    /// <summary>
    /// Gets or sets the component name used as the <see cref="Core.Contracts.DependencyId"/>
    /// for recorded health signals. Must be a valid dependency identifier
    /// (alphanumeric with hyphens and underscores, max 200 chars).
    /// </summary>
    public string ComponentName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional predicate that determines whether an HTTP response
    /// should be considered a failure. When <c>null</c>, the default behavior treats
    /// any response with status code ≥ 500 as a failure.
    /// </summary>
    public Func<HttpResponseMessage, bool>? IsFailure { get; set; }
}

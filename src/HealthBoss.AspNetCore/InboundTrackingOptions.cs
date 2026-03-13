using HealthBoss.Core.Contracts;

namespace HealthBoss.AspNetCore;

/// <summary>
/// Configuration options for inbound HTTP request health tracking.
/// Maps URL path prefixes to component name aliases and controls
/// failure classification and path exclusions.
/// </summary>
/// <remarks>
/// Component names are validated as <see cref="DependencyId"/> values
/// (alphanumeric, hyphens, underscores, max 200 chars) to prevent
/// raw route templates from leaking into health signals.
/// </remarks>
public sealed class InboundTrackingOptions
{
    private readonly List<RouteMapping> _mappings = [];

    /// <summary>
    /// Gets or sets the default component name used when no route mapping matches.
    /// Must be a valid <see cref="DependencyId"/> value. Defaults to "api".
    /// </summary>
    public string DefaultComponent { get; set; } = "api";

    /// <summary>
    /// Gets or sets the paths to exclude from signal recording.
    /// Matching is exact and case-insensitive.
    /// Defaults to ["/healthz", "/healthz/ready"].
    /// </summary>
    public IReadOnlyList<string> ExcludePaths { get; set; } = ["/healthz", "/healthz/ready"];

    /// <summary>
    /// Gets or sets the predicate that determines whether an HTTP status code
    /// represents a failure. Defaults to <c>statusCode &gt;= 500</c>.
    /// </summary>
    public Func<int, bool> IsFailure { get; set; } = statusCode => statusCode >= 500;

    /// <summary>
    /// Maps a URL path prefix to a component name alias.
    /// Requests whose path starts with <paramref name="pathPrefix"/> (case-insensitive)
    /// will record signals under the given <paramref name="componentName"/>.
    /// </summary>
    /// <param name="pathPrefix">
    /// The URL path prefix to match (e.g., "/api/payments").
    /// Trailing "/**" is stripped automatically for prefix matching.
    /// </param>
    /// <param name="componentName">
    /// The component name alias. Must be a valid <see cref="DependencyId"/> value.
    /// </param>
    /// <returns>This instance for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="pathPrefix"/> or <paramref name="componentName"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="componentName"/> is empty or contains invalid characters.
    /// </exception>
    public InboundTrackingOptions Map(string pathPrefix, string componentName)
    {
        ArgumentNullException.ThrowIfNull(pathPrefix);
        ArgumentNullException.ThrowIfNull(componentName);

        // Validate componentName is a valid DependencyId — this ensures only
        // safe aliases (no raw route templates) appear in signals.
        _ = new DependencyId(componentName);

        // Strip trailing glob patterns for clean prefix matching.
        string normalizedPrefix = pathPrefix.TrimEnd('*').TrimEnd('/');
        _mappings.Add(new RouteMapping(normalizedPrefix, componentName));

        return this;
    }

    /// <summary>
    /// Resolves the component name for the given request path using registered mappings.
    /// Returns <see cref="DefaultComponent"/> if no mapping matches.
    /// </summary>
    /// <param name="path">The HTTP request path.</param>
    /// <returns>The resolved component name alias.</returns>
    internal string ResolveComponent(string path)
    {
        foreach (var mapping in _mappings)
        {
            if (path.StartsWith(mapping.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return mapping.ComponentName;
            }
        }

        return DefaultComponent;
    }

    /// <summary>
    /// Determines whether the given path should be excluded from signal recording.
    /// </summary>
    /// <param name="path">The HTTP request path.</param>
    /// <returns>True if the path is excluded; otherwise, false.</returns>
    internal bool IsExcluded(string path)
    {
        foreach (var excluded in ExcludePaths)
        {
            if (string.Equals(path, excluded, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Internal route mapping entry.
    /// </summary>
    private sealed record RouteMapping(string Prefix, string ComponentName);
}

// <copyright file="HealthBossOptions.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace HealthBoss.Core;

/// <summary>
/// Configuration options for the HealthBoss health intelligence layer.
/// Used with <see cref="HealthBossServiceCollectionExtensions.AddHealthBoss"/> to register
/// tracked components and configure aggregate health resolution.
/// </summary>
public sealed class HealthBossOptions
{
    /// <summary>
    /// Gets the registered component configurations, keyed by component name.
    /// </summary>
    internal Dictionary<string, ComponentRegistration> Components { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a tracked component with an optional fluent configuration action.
    /// Validates the resulting <see cref="HealthPolicy"/> immediately — invalid configuration
    /// causes an exception at registration time (fail-fast).
    /// </summary>
    /// <param name="name">The component name. Must be a valid <see cref="DependencyId"/> value.</param>
    /// <param name="configure">Optional fluent configuration for the component's health policy.</param>
    /// <returns>This <see cref="HealthBossOptions"/> instance for chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when the name is invalid or policy validation fails.</exception>
    public HealthBossOptions AddComponent(string name, Action<ComponentBuilder>? configure = null)
    {
        var dependencyId = new DependencyId(name);

        var builder = new ComponentBuilder();
        configure?.Invoke(builder);

        var healthPolicy = builder.Build();

        HealthBossValidator.ValidateHealthPolicy(healthPolicy);

        Components[name] = new ComponentRegistration(dependencyId, healthPolicy);

        return this;
    }

    /// <summary>
    /// Gets or sets an optional delegate that resolves the aggregate <see cref="HealthStatus"/>
    /// from all dependency snapshots. When <c>null</c>, the default worst-of-all strategy is used.
    /// </summary>
    public Func<IReadOnlyList<DependencySnapshot>, HealthStatus>? AggregateHealthResolver { get; set; }

    /// <summary>
    /// Gets or sets an optional delegate that resolves the aggregate <see cref="ReadinessStatus"/>
    /// from all dependency snapshots. When <c>null</c>, the default all-must-be-ready strategy is used.
    /// </summary>
    public Func<IReadOnlyList<DependencySnapshot>, ReadinessStatus>? AggregateReadinessResolver { get; set; }

    /// <summary>
    /// Gets the custom event sink factories registered via <see cref="AddEventSink{T}"/>
    /// or <see cref="AddEventSink(Func{IServiceProvider, IHealthEventSink})"/>.
    /// </summary>
    internal List<Func<IServiceProvider, IHealthEventSink>> EventSinkFactories { get; } = [];

    /// <summary>
    /// Registers a custom <see cref="IHealthEventSink"/> implementation to receive
    /// health state change events. The sink type must be registered in DI separately
    /// (e.g., via <c>services.AddSingleton&lt;T&gt;()</c>) before calling <c>AddHealthBoss</c>.
    /// </summary>
    /// <typeparam name="T">The concrete event sink type.</typeparam>
    /// <returns>This <see cref="HealthBossOptions"/> instance for chaining.</returns>
    public HealthBossOptions AddEventSink<T>() where T : class, IHealthEventSink
    {
        EventSinkFactories.Add(sp => sp.GetRequiredService<T>());
        return this;
    }

    /// <summary>
    /// Registers a custom <see cref="IHealthEventSink"/> using a factory delegate.
    /// The factory receives the <see cref="IServiceProvider"/> and returns the sink instance.
    /// </summary>
    /// <param name="factory">Factory that creates the event sink from the service provider.</param>
    /// <returns>This <see cref="HealthBossOptions"/> instance for chaining.</returns>
    public HealthBossOptions AddEventSink(Func<IServiceProvider, IHealthEventSink> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        EventSinkFactories.Add(factory);
        return this;
    }

    /// <summary>
    /// Gets or sets an optional <see cref="System.TimeProvider"/> override.
    /// Primarily useful for deterministic testing. When <c>null</c>, <see cref="TimeProvider.System"/> is used.
    /// </summary>
    public TimeProvider? TimeProvider { get; set; }
}

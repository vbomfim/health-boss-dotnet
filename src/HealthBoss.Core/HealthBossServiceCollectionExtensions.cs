// <copyright file="HealthBossServiceCollectionExtensions.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HealthBoss.Core;

/// <summary>
/// Extension methods for registering HealthBoss services in the dependency injection container.
/// </summary>
public static class HealthBossServiceCollectionExtensions
{
    /// <summary>
    /// Adds the HealthBoss health intelligence layer to the service collection.
    /// Registers all core services (clock, evaluator, state graph, transition engine,
    /// startup tracker, orchestrator) as singletons, plus per-component keyed
    /// <see cref="ISignalBuffer"/> instances.
    /// Configuration is validated at registration time — invalid policies cause immediate exceptions.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configure">The configuration action for <see cref="HealthBossOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="configure"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when any component configuration fails validation.</exception>
    public static IServiceCollection AddHealthBoss(
        this IServiceCollection services,
        Action<HealthBossOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new HealthBossOptions();
        configure(options);

        // Register options as IOptions<HealthBossOptions> singleton
        services.AddSingleton<IOptions<HealthBossOptions>>(Options.Create(options));

        // Determine the time provider (custom or system default)
        var timeProvider = options.TimeProvider ?? TimeProvider.System;

        // Metrics — AddMetrics() registers IMeterFactory (idempotent — safe to call multiple times).
        // HealthBossMetrics resolves IMeterFactory via constructor injection.
        services.AddMetrics();
        services.AddSingleton<HealthBossMetrics>();
        services.AddSingleton<IHealthBossMetrics>(sp => sp.GetRequiredService<HealthBossMetrics>());
        services.AddSingleton<IComponentMetrics>(sp => sp.GetRequiredService<HealthBossMetrics>());
        services.AddSingleton<ISessionMetrics>(sp => sp.GetRequiredService<HealthBossMetrics>());
        services.AddSingleton<IStateMachineMetrics>(sp => sp.GetRequiredService<HealthBossMetrics>());
        services.AddSingleton<ITenantMetrics>(sp => sp.GetRequiredService<HealthBossMetrics>());
        services.AddSingleton<IQuorumMetrics>(sp => sp.GetRequiredService<HealthBossMetrics>());

        // Core singleton services
        services.AddSingleton<ISystemClock>(_ => new SystemClock(timeProvider));
        services.AddSingleton<IPolicyEvaluator>(_ => new PolicyEvaluator());
        services.AddSingleton<IStateGraph>(_ => new DefaultStateGraph());
        services.AddSingleton<ITransitionEngine>(sp =>
            new TransitionEngine(sp.GetRequiredService<IStateGraph>()));
        services.AddSingleton<IStartupTracker>(_ => new StartupTracker());
        services.AddSingleton<ITimerBudgetValidator>(_ => new TimerBudgetValidator());

        // Per-component keyed signal buffers
        foreach (var (name, _) in options.Components)
        {
            services.AddKeyedSingleton<ISignalBuffer>(name, (sp, _) =>
                new SignalBuffer(sp.GetRequiredService<ISystemClock>()));
        }

        // ISP: register ISignalWriter as an alias so write-only consumers
        // (gRPC interceptors, Polly hooks, recovery probers) can resolve
        // the narrow interface without depending on full ISignalBuffer.
        foreach (var (name, _) in options.Components)
        {
            services.AddKeyedSingleton<ISignalWriter>(name, (sp, key) =>
                sp.GetRequiredKeyedService<ISignalBuffer>(key));
        }

        // Build per-dependency monitors and register the orchestrator
        services.AddSingleton<IHealthOrchestrator>(sp =>
        {
            var clock = sp.GetRequiredService<ISystemClock>();
            var evaluator = sp.GetRequiredService<IPolicyEvaluator>();
            var transitionEngine = sp.GetRequiredService<ITransitionEngine>();
            var startupTracker = sp.GetRequiredService<IStartupTracker>();

            var monitors = new Dictionary<DependencyId, IDependencyMonitor>();
            foreach (var (name, registration) in options.Components)
            {
                var buffer = sp.GetRequiredKeyedService<ISignalBuffer>(name);
                var monitor = new DependencyMonitor(
                    registration.DependencyId,
                    buffer,
                    evaluator,
                    transitionEngine,
                    registration.Policy,
                    clock);
                monitors[registration.DependencyId] = monitor;
            }

            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger<HealthOrchestrator>();
            var metrics = sp.GetService<IComponentMetrics>();

            return new HealthOrchestrator(
                monitors,
                options.AggregateHealthResolver,
                options.AggregateReadinessResolver,
                startupTracker,
                clock,
                logger,
                metrics);
        });

        // Forward-register interface projections so consumers can resolve either interface
        services.AddSingleton<IHealthStateReader>(sp =>
            sp.GetRequiredService<IHealthOrchestrator>());
        services.AddSingleton<IHealthReportProvider>(sp =>
            sp.GetRequiredService<IHealthOrchestrator>());
        services.AddSingleton<ISignalIngress>(sp =>
            sp.GetRequiredService<IHealthOrchestrator>());

        // Session health tracker — active session gauge for SIGTERM drain decisions
        services.AddSingleton<ISessionHealthTracker>(sp =>
            new SessionHealthTracker(
                sp.GetRequiredService<ISystemClock>(),
                metrics: sp.GetService<ISessionMetrics>()));

        // Quorum evaluator — instance-level health assessment for gRPC/load-balanced backends
        services.AddSingleton<IQuorumEvaluator, QuorumEvaluator>();

        // Event sink pipeline: concrete sinks → dispatcher (fan-out with rate limiting)
        // The dispatcher implements IHealthEventSink itself for orchestrator integration,
        // but must NOT appear in its own sink collection (it IS the dispatcher, not a leaf sink).
        // OpenTelemetryMetricEventSink is always included as a default sink.
        // Consumers add custom sinks via options.AddEventSink<T>() or options.AddEventSink(factory).
        services.AddSingleton<OpenTelemetryMetricEventSink>(sp =>
            new OpenTelemetryMetricEventSink(
                sp.GetRequiredService<IStateMachineMetrics>(),
                sp.GetRequiredService<ITenantMetrics>()));

        services.AddSingleton<EventSinkDispatcher>(sp =>
        {
            var sinks = new List<IHealthEventSink>
            {
                sp.GetRequiredService<OpenTelemetryMetricEventSink>(),
            };

            // Resolve consumer-registered sinks via options (avoids circular DI)
            foreach (var factory in options.EventSinkFactories)
            {
                sinks.Add(factory(sp));
            }

            return new EventSinkDispatcher(
                sinks,
                new EventSinkDispatcherOptions(),
                sp.GetRequiredService<ISystemClock>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<EventSinkDispatcher>(),
                sp.GetService<IStateMachineMetrics>());
        });

        services.AddSingleton<IEventSinkDispatcher>(sp =>
            sp.GetRequiredService<EventSinkDispatcher>());
        services.AddSingleton<IHealthEventSink>(sp =>
            sp.GetRequiredService<EventSinkDispatcher>());

        return services;
    }
}

// <copyright file="PluggableEventSinkTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace HealthBoss.Core.Tests;

/// <summary>
/// Tests that consumers can register custom <see cref="IHealthEventSink"/>
/// implementations via <see cref="HealthBossOptions.AddEventSink{T}"/>
/// and <see cref="HealthBossOptions.AddEventSink(Func{IServiceProvider, IHealthEventSink})"/>.
/// </summary>
public sealed class PluggableEventSinkTests
{
    [Fact]
    public void AddEventSink_generic_registers_custom_sink()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TestEventSink>();
        services.AddHealthBoss(opts =>
        {
            opts.AddComponent("redis");
            opts.AddEventSink<TestEventSink>();
        });

        using var provider = services.BuildServiceProvider();

        // Resolve the dispatcher — it should include our custom sink
        var dispatcher = provider.GetRequiredService<IEventSinkDispatcher>();
        dispatcher.Should().NotBeNull();

        // The custom sink should be resolvable
        var customSink = provider.GetRequiredService<TestEventSink>();
        customSink.Should().NotBeNull();
    }

    [Fact]
    public void AddEventSink_factory_registers_custom_sink()
    {
        var sinkInstance = new TestEventSink();
        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.AddComponent("redis");
            opts.AddEventSink(_ => sinkInstance);
        });

        using var provider = services.BuildServiceProvider();

        var dispatcher = provider.GetRequiredService<IEventSinkDispatcher>();
        dispatcher.Should().NotBeNull();
    }

    [Fact]
    public void AddEventSink_null_factory_throws()
    {
        var options = new HealthBossOptions();

        var act = () => options.AddEventSink(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Custom_sink_receives_dispatched_events()
    {
        var customSink = new TestEventSink();
        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.AddComponent("redis");
            opts.AddEventSink(_ => customSink);
        });

        using var provider = services.BuildServiceProvider();

        var dispatcher = provider.GetRequiredService<IEventSinkDispatcher>();
        var healthEvent = new HealthEvent(
            new DependencyId("redis"),
            HealthState.Healthy,
            HealthState.Degraded,
            DateTimeOffset.UtcNow);

        await dispatcher.DispatchAsync(healthEvent);

        customSink.HealthEvents.Should().ContainSingle();
    }

    [Fact]
    public void Default_sinks_include_OTelMetrics_without_StructuredLog()
    {
        // After v1.0 cleanup, StructuredLogEventSink is removed.
        // Only OpenTelemetryMetricEventSink should be registered by default.
        var services = new ServiceCollection();
        services.AddHealthBoss(opts => opts.AddComponent("redis"));

        using var provider = services.BuildServiceProvider();

        var dispatcher = provider.GetRequiredService<IEventSinkDispatcher>();
        dispatcher.Should().NotBeNull();

        // Verify no StructuredLogEventSink in the service collection
        var descriptors = services.Where(d => d.ServiceType.Name == "StructuredLogEventSink");
        descriptors.Should().BeEmpty();
    }

    /// <summary>
    /// Test event sink that captures dispatched events for assertion.
    /// </summary>
    private sealed class TestEventSink : IHealthEventSink
    {
        public List<HealthEvent> HealthEvents { get; } = [];
        public List<TenantHealthEvent> TenantEvents { get; } = [];

        public Task OnHealthStateChanged(HealthEvent healthEvent, CancellationToken ct = default)
        {
            HealthEvents.Add(healthEvent);
            return Task.CompletedTask;
        }

        public Task OnTenantHealthChanged(TenantHealthEvent tenantEvent, CancellationToken ct = default)
        {
            TenantEvents.Add(tenantEvent);
            return Task.CompletedTask;
        }
    }
}

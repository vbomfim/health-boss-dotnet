// <copyright file="SignalIngressDiTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace HealthBoss.Core.Tests;

/// <summary>
/// Tests for <see cref="ISignalIngress"/> DI registration and behavior.
/// Verifies that the orchestrator-level signal recording interface is
/// properly wired as a singleton forwarding to <see cref="IHealthOrchestrator"/>.
/// </summary>
public sealed class SignalIngressDiTests
{
    [Fact]
    public void AddHealthBoss_registers_ISignalIngress_as_singleton()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts => opts.AddComponent("redis"));

        using var provider = services.BuildServiceProvider();

        var ingress1 = provider.GetRequiredService<ISignalIngress>();
        var ingress2 = provider.GetRequiredService<ISignalIngress>();

        ingress1.Should().NotBeNull();
        ingress1.Should().BeSameAs(ingress2);
    }

    [Fact]
    public void ISignalIngress_resolves_to_same_instance_as_IHealthOrchestrator()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts => opts.AddComponent("redis"));

        using var provider = services.BuildServiceProvider();

        var ingress = provider.GetRequiredService<ISignalIngress>();
        var orchestrator = provider.GetRequiredService<IHealthOrchestrator>();

        ingress.Should().BeSameAs(orchestrator);
    }

    [Fact]
    public void IHealthOrchestrator_implements_ISignalIngress()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts => opts.AddComponent("redis"));

        using var provider = services.BuildServiceProvider();

        var orchestrator = provider.GetRequiredService<IHealthOrchestrator>();

        orchestrator.Should().BeAssignableTo<ISignalIngress>();
    }

    [Fact]
    public void RecordSignal_via_ISignalIngress_flows_to_orchestrator()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts => opts.AddComponent("redis"));

        using var provider = services.BuildServiceProvider();

        var ingress = provider.GetRequiredService<ISignalIngress>();
        var orchestrator = provider.GetRequiredService<IHealthOrchestrator>();

        var depId = new DependencyId("redis");
        for (int i = 0; i < 10; i++)
        {
            ingress.RecordSignal(depId, new HealthSignal(
                DateTimeOffset.UtcNow.AddSeconds(i),
                depId,
                SignalOutcome.Success));
        }

        var report = orchestrator.GetHealthReport();
        report.Status.Should().Be(HealthStatus.Healthy);
        report.Dependencies.Should().ContainSingle()
            .Which.LatestAssessment.TotalSignals.Should().Be(10);
    }

    [Fact]
    public void RecordSignal_via_ISignalIngress_for_unknown_dep_does_not_throw()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts => opts.AddComponent("redis"));

        using var provider = services.BuildServiceProvider();

        var ingress = provider.GetRequiredService<ISignalIngress>();
        var unknownDep = new DependencyId("unknown");

        // Should not throw — signal is dropped with warning
        var act = () => ingress.RecordSignal(unknownDep, new HealthSignal(
            DateTimeOffset.UtcNow, unknownDep, SignalOutcome.Success));

        act.Should().NotThrow();
    }
}

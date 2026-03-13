using FluentAssertions;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace HealthBoss.Core.Tests;

/// <summary>
/// DI integration tests for HealthOrchestrator registration via AddHealthBoss.
/// Verifies that the orchestrator is resolved correctly and wired with monitors.
/// </summary>
public sealed class HealthOrchestratorDiTests
{
    [Fact]
    public void AddHealthBoss_registers_IHealthOrchestrator_as_singleton()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.AddComponent("redis");
            opts.AddComponent("sql-db");
        });

        using var provider = services.BuildServiceProvider();

        var orchestrator1 = provider.GetRequiredService<IHealthOrchestrator>();
        var orchestrator2 = provider.GetRequiredService<IHealthOrchestrator>();

        orchestrator1.Should().NotBeNull();
        orchestrator1.Should().BeSameAs(orchestrator2);
    }

    [Fact]
    public void AddHealthBoss_registers_IHealthStateReader_forwarding_to_orchestrator()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts => opts.AddComponent("redis"));

        using var provider = services.BuildServiceProvider();

        var reader = provider.GetRequiredService<IHealthStateReader>();
        var orchestrator = provider.GetRequiredService<IHealthOrchestrator>();

        reader.Should().BeSameAs(orchestrator);
    }

    [Fact]
    public void AddHealthBoss_registers_IHealthReportProvider_forwarding_to_orchestrator()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts => opts.AddComponent("redis"));

        using var provider = services.BuildServiceProvider();

        var reportProvider = provider.GetRequiredService<IHealthReportProvider>();
        var orchestrator = provider.GetRequiredService<IHealthOrchestrator>();

        reportProvider.Should().BeSameAs(orchestrator);
    }

    [Fact]
    public void Orchestrator_has_all_registered_dependencies()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.AddComponent("redis");
            opts.AddComponent("sql-db");
            opts.AddComponent("blob-storage");
        });

        using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IHealthOrchestrator>();

        orchestrator.RegisteredDependencies.Should().HaveCount(3);
        orchestrator.RegisteredDependencies
            .Select(d => d.Value)
            .Should().Contain(["redis", "sql-db", "blob-storage"]);
    }

    [Fact]
    public void Orchestrator_GetMonitor_returns_monitors_for_all_components()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.AddComponent("redis");
            opts.AddComponent("sql-db");
        });

        using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IHealthOrchestrator>();

        orchestrator.GetMonitor(new DependencyId("redis")).Should().NotBeNull();
        orchestrator.GetMonitor(new DependencyId("sql-db")).Should().NotBeNull();
        orchestrator.GetMonitor(new DependencyId("unknown")).Should().BeNull();
    }

    [Fact]
    public void Orchestrator_RecordSignal_and_GetHealthReport_works_end_to_end()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts => opts.AddComponent("redis"));

        using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IHealthOrchestrator>();

        // Record signals through the orchestrator
        var depId = new DependencyId("redis");
        for (int i = 0; i < 10; i++)
        {
            orchestrator.RecordSignal(depId, new HealthSignal(
                DateTimeOffset.UtcNow.AddSeconds(i),
                depId,
                SignalOutcome.Success));
        }

        var report = orchestrator.GetHealthReport();

        report.Status.Should().Be(HealthStatus.Healthy);
        report.Dependencies.Should().ContainSingle();
    }

    [Fact]
    public void Orchestrator_with_custom_health_resolver_uses_delegate()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.AddComponent("redis");
            opts.AggregateHealthResolver = _ => HealthStatus.Degraded;
        });

        using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IHealthOrchestrator>();

        var report = orchestrator.GetHealthReport();
        report.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public void Orchestrator_with_custom_readiness_resolver_uses_delegate()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.AddComponent("redis");
            opts.AggregateReadinessResolver = _ => ReadinessStatus.NotReady;
        });

        using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IHealthOrchestrator>();

        var report = orchestrator.GetReadinessReport();
        report.Status.Should().Be(ReadinessStatus.NotReady);
    }

    [Fact]
    public void Orchestrator_initial_state_is_healthy()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts => opts.AddComponent("redis"));

        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IHealthStateReader>();

        reader.CurrentState.Should().Be(HealthState.Healthy);
        reader.ReadinessStatus.Should().Be(ReadinessStatus.Ready);
    }
}

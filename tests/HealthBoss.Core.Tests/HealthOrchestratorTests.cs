using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace HealthBoss.Core.Tests;

public sealed class HealthOrchestratorTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ISystemClock _clock;
    private readonly IPolicyEvaluator _evaluator;
    private readonly ITransitionEngine _transitionEngine;
    private readonly IStartupTracker _startupTracker;

    public HealthOrchestratorTests()
    {
        _timeProvider.SetUtcNow(TestFixtures.BaseTime);
        _clock = new SystemClock(_timeProvider);
        _evaluator = new PolicyEvaluator();
        _transitionEngine = new TransitionEngine(new DefaultStateGraph());
        _startupTracker = new Core.Components.StartupTracker();
    }

    private DependencyMonitor CreateDependencyMonitor(string name, HealthPolicy? policy = null) =>
        new(
            new DependencyId(name),
            new SignalBuffer(_clock),
            _evaluator,
            _transitionEngine,
            policy ?? TestFixtures.ZeroJitterPolicy,
            _clock);

    private HealthOrchestrator CreateOrchestrator(
        IDependencyMonitor[]? monitors = null,
        Func<IReadOnlyList<DependencySnapshot>, HealthStatus>? healthResolver = null,
        Func<IReadOnlyList<DependencySnapshot>, ReadinessStatus>? readinessResolver = null,
        IStartupTracker? startupTracker = null)
    {
        var monitorList = monitors ?? [CreateDependencyMonitor("dep-1"), CreateDependencyMonitor("dep-2")];
        var dict = monitorList.ToDictionary(m => m.DependencyId, m => (IDependencyMonitor)m);
        return new HealthOrchestrator(
            dict,
            healthResolver,
            readinessResolver,
            startupTracker ?? _startupTracker,
            _clock);
    }

    // ── Registration ──

    [Fact]
    public void RegisteredDependencies_returns_all_registered_ids()
    {
        var orchestrator = CreateOrchestrator();

        orchestrator.RegisteredDependencies.Should().HaveCount(2);
        orchestrator.RegisteredDependencies
            .Select(d => d.Value)
            .Should().Contain(["dep-1", "dep-2"]);
    }

    [Fact]
    public void GetMonitor_returns_monitor_for_registered_dependency()
    {
        var orchestrator = CreateOrchestrator();

        var monitor = orchestrator.GetMonitor(new DependencyId("dep-1"));

        monitor.Should().NotBeNull();
        monitor!.DependencyId.Value.Should().Be("dep-1");
    }

    [Fact]
    public void GetMonitor_returns_null_for_unknown_dependency()
    {
        var orchestrator = CreateOrchestrator();

        var monitor = orchestrator.GetMonitor(new DependencyId("unknown-dep"));

        monitor.Should().BeNull();
    }

    // ── RecordSignal routing ──

    [Fact]
    public void RecordSignal_routes_to_correct_monitor()
    {
        var orchestrator = CreateOrchestrator();
        var depId = new DependencyId("dep-1");
        var signal = TestFixtures.CreateSignal(
            timestamp: _clock.UtcNow, dependencyId: depId);

        orchestrator.RecordSignal(depId, signal);

        // Verify by getting a snapshot — should have 1 signal
        var monitor = orchestrator.GetMonitor(depId);
        var snapshot = monitor!.GetSnapshot();
        snapshot.LatestAssessment.TotalSignals.Should().Be(1);
    }

    [Fact]
    public void RecordSignal_unknown_dependency_does_not_throw()
    {
        var orchestrator = CreateOrchestrator();
        var unknownId = new DependencyId("not-registered");
        var signal = TestFixtures.CreateSignal(
            timestamp: _clock.UtcNow, dependencyId: unknownId);

        // Should not throw — signal is dropped
        var act = () => orchestrator.RecordSignal(unknownId, signal);

        act.Should().NotThrow();
    }

    [Fact]
    public void RecordSignal_null_signal_throws()
    {
        var orchestrator = CreateOrchestrator();
        var act = () => orchestrator.RecordSignal(new DependencyId("dep-1"), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── GetHealthReport ──

    [Fact]
    public void GetHealthReport_all_healthy_returns_healthy()
    {
        var orchestrator = CreateOrchestrator();

        // Record some successful signals for both dependencies
        RecordSuccessSignals(orchestrator, "dep-1", 10);
        RecordSuccessSignals(orchestrator, "dep-2", 10);

        var report = orchestrator.GetHealthReport();

        report.Status.Should().Be(HealthStatus.Healthy);
        report.Dependencies.Should().HaveCount(2);
        report.GeneratedAt.Should().Be(_clock.UtcNow);
    }

    [Fact]
    public void GetHealthReport_default_aggregation_worst_status_wins()
    {
        var policy = TestFixtures.ZeroJitterPolicy with
        {
            CooldownBeforeTransition = TimeSpan.Zero,
        };
        var dep1 = CreateDependencyMonitor("dep-healthy", policy);
        var dep2 = CreateDependencyMonitor("dep-degraded", policy);
        var orchestrator = CreateOrchestrator(monitors: [dep1, dep2]);

        // dep-healthy: all success
        for (int i = 0; i < 10; i++)
        {
            dep1.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Success, timestamp: _clock.UtcNow.AddSeconds(i),
                dependencyId: new DependencyId("dep-healthy")));
        }

        // dep-degraded: 50% success → Degraded
        for (int i = 0; i < 5; i++)
        {
            dep2.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Success, timestamp: _clock.UtcNow.AddSeconds(i),
                dependencyId: new DependencyId("dep-degraded")));
        }

        for (int i = 5; i < 10; i++)
        {
            dep2.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Failure, timestamp: _clock.UtcNow.AddSeconds(i),
                dependencyId: new DependencyId("dep-degraded")));
        }

        var report = orchestrator.GetHealthReport();
        report.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public void GetHealthReport_circuit_open_maps_to_unhealthy()
    {
        var policy = TestFixtures.ZeroJitterPolicy with
        {
            CooldownBeforeTransition = TimeSpan.Zero,
        };
        var dep1 = CreateDependencyMonitor("dep-healthy", policy);
        var dep2 = CreateDependencyMonitor("dep-broken", policy);
        var orchestrator = CreateOrchestrator(monitors: [dep1, dep2]);

        // dep-healthy: all success
        for (int i = 0; i < 10; i++)
        {
            dep1.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Success, timestamp: _clock.UtcNow.AddSeconds(i),
                dependencyId: new DependencyId("dep-healthy")));
        }

        // dep-broken: 10% success → CircuitOpen (needs two-step via Degraded)
        dep2.RecordSignal(TestFixtures.CreateSignal(
            SignalOutcome.Success, timestamp: _clock.UtcNow,
            dependencyId: new DependencyId("dep-broken")));
        for (int i = 1; i < 10; i++)
        {
            dep2.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Failure, timestamp: _clock.UtcNow.AddSeconds(i),
                dependencyId: new DependencyId("dep-broken")));
        }

        // First report: dep-broken transitions Healthy → Degraded
        var report1 = orchestrator.GetHealthReport();
        report1.Status.Should().Be(HealthStatus.Degraded);

        // Second report: dep-broken transitions Degraded → CircuitOpen
        var report2 = orchestrator.GetHealthReport();
        report2.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public void GetHealthReport_empty_dependencies_returns_healthy()
    {
        var orchestrator = CreateOrchestrator(monitors: []);

        var report = orchestrator.GetHealthReport();

        report.Status.Should().Be(HealthStatus.Healthy);
        report.Dependencies.Should().BeEmpty();
    }

    // ── Custom delegate ──

    [Fact]
    public void GetHealthReport_custom_delegate_returns_custom_result()
    {
        var orchestrator = CreateOrchestrator(
            healthResolver: _ => HealthStatus.Degraded);

        var report = orchestrator.GetHealthReport();

        report.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public void GetHealthReport_custom_delegate_throws_falls_back_to_default()
    {
        var orchestrator = CreateOrchestrator(
            healthResolver: _ => throw new InvalidOperationException("boom"));

        var report = orchestrator.GetHealthReport();

        // Default fallback: all healthy → Healthy
        report.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public void GetHealthReport_custom_delegate_timeout_falls_back_to_default()
    {
        var orchestrator = CreateOrchestrator(
            healthResolver: _ =>
            {
                // Simulate a hanging delegate (>5s)
                Thread.Sleep(TimeSpan.FromSeconds(10));
                return HealthStatus.Degraded;
            });

        var report = orchestrator.GetHealthReport();

        // Should fall back to default (Healthy, since no signals recorded)
        report.Status.Should().Be(HealthStatus.Healthy);
    }

    // ── GetReadinessReport ──

    [Fact]
    public void GetReadinessReport_all_healthy_returns_ready()
    {
        _startupTracker.MarkReady();
        var orchestrator = CreateOrchestrator();

        RecordSuccessSignals(orchestrator, "dep-1", 10);
        RecordSuccessSignals(orchestrator, "dep-2", 10);

        var report = orchestrator.GetReadinessReport();

        report.Status.Should().Be(ReadinessStatus.Ready);
        report.StartupStatus.Should().Be(StartupStatus.Ready);
        report.DrainStatus.Should().Be(DrainStatus.Idle);
        report.Dependencies.Should().HaveCount(2);
    }

    [Fact]
    public void GetReadinessReport_circuit_open_returns_not_ready()
    {
        var policy = TestFixtures.ZeroJitterPolicy with
        {
            CooldownBeforeTransition = TimeSpan.Zero,
        };
        var dep = CreateDependencyMonitor("dep-broken", policy);
        var orchestrator = CreateOrchestrator(monitors: [dep]);

        // All failures → needs two-step: Healthy → Degraded → CircuitOpen
        for (int i = 0; i < 10; i++)
        {
            dep.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Failure, timestamp: _clock.UtcNow.AddSeconds(i),
                dependencyId: new DependencyId("dep-broken")));
        }

        // First report: Healthy → Degraded
        var report1 = orchestrator.GetReadinessReport();
        report1.Status.Should().Be(ReadinessStatus.Ready); // Degraded is still ready

        // Second report: Degraded → CircuitOpen
        var report2 = orchestrator.GetReadinessReport();
        report2.Status.Should().Be(ReadinessStatus.NotReady);
    }

    [Fact]
    public void GetReadinessReport_includes_startup_status()
    {
        var orchestrator = CreateOrchestrator();

        var report = orchestrator.GetReadinessReport();
        report.StartupStatus.Should().Be(StartupStatus.Starting);

        _startupTracker.MarkReady();
        var report2 = orchestrator.GetReadinessReport();
        report2.StartupStatus.Should().Be(StartupStatus.Ready);
    }

    [Fact]
    public void GetReadinessReport_custom_delegate_returns_custom_result()
    {
        var orchestrator = CreateOrchestrator(
            readinessResolver: _ => ReadinessStatus.NotReady);

        var report = orchestrator.GetReadinessReport();
        report.Status.Should().Be(ReadinessStatus.NotReady);
    }

    [Fact]
    public void GetReadinessReport_custom_delegate_throws_falls_back_to_default()
    {
        var orchestrator = CreateOrchestrator(
            readinessResolver: _ => throw new InvalidOperationException("boom"));

        var report = orchestrator.GetReadinessReport();
        report.Status.Should().Be(ReadinessStatus.Ready);
    }

    [Fact]
    public void GetReadinessReport_custom_delegate_timeout_falls_back_to_default()
    {
        var orchestrator = CreateOrchestrator(
            readinessResolver: _ =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(10));
                return ReadinessStatus.NotReady;
            });

        var report = orchestrator.GetReadinessReport();
        report.Status.Should().Be(ReadinessStatus.Ready);
    }

    // ── IHealthStateReader ──

    [Fact]
    public void Implements_IHealthStateReader()
    {
        var orchestrator = CreateOrchestrator();
        orchestrator.Should().BeAssignableTo<IHealthStateReader>();
    }

    [Fact]
    public void Implements_IHealthReportProvider()
    {
        var orchestrator = CreateOrchestrator();
        orchestrator.Should().BeAssignableTo<IHealthReportProvider>();
    }

    [Fact]
    public void Implements_IHealthOrchestrator()
    {
        var orchestrator = CreateOrchestrator();
        orchestrator.Should().BeAssignableTo<IHealthOrchestrator>();
    }

    [Fact]
    public void CurrentState_reflects_aggregate_health()
    {
        var policy = TestFixtures.ZeroJitterPolicy with
        {
            CooldownBeforeTransition = TimeSpan.Zero,
        };
        var dep = CreateDependencyMonitor("dep-degraded", policy);
        var orchestrator = CreateOrchestrator(monitors: [dep]);

        // 50% success → Degraded
        for (int i = 0; i < 5; i++)
        {
            dep.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Success, timestamp: _clock.UtcNow.AddSeconds(i),
                dependencyId: new DependencyId("dep-degraded")));
        }

        for (int i = 5; i < 10; i++)
        {
            dep.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Failure, timestamp: _clock.UtcNow.AddSeconds(i),
                dependencyId: new DependencyId("dep-degraded")));
        }

        orchestrator.CurrentState.Should().Be(HealthState.Degraded);
    }

    [Fact]
    public void ReadinessStatus_reflects_aggregate_readiness()
    {
        var orchestrator = CreateOrchestrator();
        orchestrator.ReadinessStatus.Should().Be(ReadinessStatus.Ready);
    }

    // ── Default aggregation static methods ──

    [Fact]
    public void DefaultHealthAggregation_empty_returns_healthy()
    {
        var result = HealthOrchestrator.DefaultHealthAggregation([]);
        result.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public void DefaultHealthAggregation_all_healthy_returns_healthy()
    {
        var snapshots = new[]
        {
            CreateSnapshot(HealthState.Healthy),
            CreateSnapshot(HealthState.Healthy),
        };

        var result = HealthOrchestrator.DefaultHealthAggregation(snapshots);
        result.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public void DefaultHealthAggregation_one_degraded_returns_degraded()
    {
        var snapshots = new[]
        {
            CreateSnapshot(HealthState.Healthy),
            CreateSnapshot(HealthState.Degraded),
        };

        var result = HealthOrchestrator.DefaultHealthAggregation(snapshots);
        result.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public void DefaultHealthAggregation_one_circuit_open_returns_unhealthy()
    {
        var snapshots = new[]
        {
            CreateSnapshot(HealthState.Healthy),
            CreateSnapshot(HealthState.Degraded),
            CreateSnapshot(HealthState.CircuitOpen),
        };

        var result = HealthOrchestrator.DefaultHealthAggregation(snapshots);
        result.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public void DefaultReadinessAggregation_all_healthy_returns_ready()
    {
        var snapshots = new[]
        {
            CreateSnapshot(HealthState.Healthy),
            CreateSnapshot(HealthState.Degraded),
        };

        var result = HealthOrchestrator.DefaultReadinessAggregation(snapshots);
        result.Should().Be(ReadinessStatus.Ready);
    }

    [Fact]
    public void DefaultReadinessAggregation_circuit_open_returns_not_ready()
    {
        var snapshots = new[]
        {
            CreateSnapshot(HealthState.Healthy),
            CreateSnapshot(HealthState.CircuitOpen),
        };

        var result = HealthOrchestrator.DefaultReadinessAggregation(snapshots);
        result.Should().Be(ReadinessStatus.NotReady);
    }

    // ── Thread safety ──

    [Fact]
    public async Task Thread_safety_concurrent_record_and_report()
    {
        var orchestrator = CreateOrchestrator();

        var recordTask = Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                var depId = i % 2 == 0
                    ? new DependencyId("dep-1")
                    : new DependencyId("dep-2");
                orchestrator.RecordSignal(depId, TestFixtures.CreateSignal(
                    timestamp: _clock.UtcNow.AddMilliseconds(i),
                    dependencyId: depId));
            }
        });

        var reportTask = Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                _ = orchestrator.GetHealthReport();
                _ = orchestrator.GetReadinessReport();
                _ = orchestrator.CurrentState;
                _ = orchestrator.ReadinessStatus;
            }
        });

        await Task.WhenAll(recordTask, reportTask);

        // No exception is the assertion; plus sanity check
        var report = orchestrator.GetHealthReport();
        report.Dependencies.Should().HaveCount(2);
    }

    // ── Constructor validation ──

    [Fact]
    public void Constructor_throws_on_null_monitors()
    {
        var act = () => new HealthOrchestrator(
            null!, null, null, _startupTracker, _clock);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_throws_on_null_startup_tracker()
    {
        var act = () => new HealthOrchestrator(
            new Dictionary<DependencyId, IDependencyMonitor>(),
            null, null, null!, _clock);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_throws_on_null_clock()
    {
        var act = () => new HealthOrchestrator(
            new Dictionary<DependencyId, IDependencyMonitor>(),
            null, null, _startupTracker, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Helpers ──

    private void RecordSuccessSignals(
        HealthOrchestrator orchestrator, string depName, int count)
    {
        var depId = new DependencyId(depName);
        for (int i = 0; i < count; i++)
        {
            orchestrator.RecordSignal(depId, TestFixtures.CreateSignal(
                SignalOutcome.Success,
                timestamp: _clock.UtcNow.AddSeconds(i),
                dependencyId: depId));
        }
    }

    private static DependencySnapshot CreateSnapshot(
        HealthState state,
        string name = "test") =>
        new(
            DependencyId: new DependencyId(name),
            CurrentState: state,
            LatestAssessment: TestFixtures.CreateAssessment(recommendedState: state),
            StateChangedAt: TestFixtures.BaseTime,
            ConsecutiveFailures: 0);
}

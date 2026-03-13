using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace HealthBoss.Core.Tests;

public sealed class DependencyMonitorTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ISystemClock _clock;
    private readonly IPolicyEvaluator _evaluator;
    private readonly ITransitionEngine _transitionEngine;

    public DependencyMonitorTests()
    {
        _timeProvider.SetUtcNow(TestFixtures.BaseTime);
        _clock = new SystemClock(_timeProvider);
        _evaluator = new PolicyEvaluator();
        _transitionEngine = new TransitionEngine(new DefaultStateGraph());
    }

    private DependencyMonitor CreateMonitor(
        string name = "test-dep",
        HealthPolicy? policy = null) =>
        new(
            new DependencyId(name),
            new SignalBuffer(_clock),
            _evaluator,
            _transitionEngine,
            policy ?? TestFixtures.ZeroJitterPolicy,
            _clock);

    [Fact]
    public void DependencyId_is_set_on_construction()
    {
        var monitor = CreateMonitor("my-service");
        monitor.DependencyId.Value.Should().Be("my-service");
    }

    [Fact]
    public void Initial_state_is_healthy()
    {
        var monitor = CreateMonitor();
        monitor.CurrentState.Should().Be(HealthState.Healthy);
    }

    [Fact]
    public void RecordSignal_and_GetSnapshot_returns_correct_assessment()
    {
        var monitor = CreateMonitor();

        // Record enough signals for evaluation (min 5)
        for (int i = 0; i < 10; i++)
        {
            monitor.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Success,
                timestamp: _clock.UtcNow.AddSeconds(i)));
        }

        var snapshot = monitor.GetSnapshot();

        snapshot.DependencyId.Value.Should().Be("test-dep");
        snapshot.CurrentState.Should().Be(HealthState.Healthy);
        snapshot.LatestAssessment.SuccessRate.Should().Be(1.0);
        snapshot.LatestAssessment.TotalSignals.Should().Be(10);
    }

    [Fact]
    public void GetSnapshot_transitions_to_degraded_on_partial_failures()
    {
        // Use a policy with zero cooldown and zero jitter
        var policy = TestFixtures.ZeroJitterPolicy with
        {
            CooldownBeforeTransition = TimeSpan.Zero,
        };
        var monitor = CreateMonitor(policy: policy);

        // 5 success, 5 failure = 50% success rate
        // DegradedThreshold=0.9, CircuitOpenThreshold=0.5 → should be Degraded
        for (int i = 0; i < 5; i++)
        {
            monitor.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Success, timestamp: _clock.UtcNow.AddSeconds(i)));
        }

        for (int i = 5; i < 10; i++)
        {
            monitor.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Failure, timestamp: _clock.UtcNow.AddSeconds(i)));
        }

        var snapshot = monitor.GetSnapshot();
        snapshot.CurrentState.Should().Be(HealthState.Degraded);
    }

    [Fact]
    public void GetSnapshot_transitions_to_circuit_open_via_degraded()
    {
        // State graph: Healthy → Degraded → CircuitOpen (two-step)
        var policy = TestFixtures.ZeroJitterPolicy with
        {
            CooldownBeforeTransition = TimeSpan.Zero,
        };
        var monitor = CreateMonitor(policy: policy);

        // 2 success, 8 failure = 20% success rate < CircuitOpenThreshold 0.5
        for (int i = 0; i < 2; i++)
        {
            monitor.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Success, timestamp: _clock.UtcNow.AddSeconds(i)));
        }

        for (int i = 2; i < 10; i++)
        {
            monitor.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Failure, timestamp: _clock.UtcNow.AddSeconds(i)));
        }

        // First evaluation: Healthy → Degraded (guard matches Degraded|CircuitOpen)
        var snapshot1 = monitor.GetSnapshot();
        snapshot1.CurrentState.Should().Be(HealthState.Degraded);

        // Second evaluation: Degraded → CircuitOpen (guard matches CircuitOpen)
        var snapshot2 = monitor.GetSnapshot();
        snapshot2.CurrentState.Should().Be(HealthState.CircuitOpen);
    }

    [Fact]
    public void ConsecutiveFailures_tracks_streak()
    {
        var monitor = CreateMonitor();

        monitor.RecordSignal(TestFixtures.CreateSignal(
            SignalOutcome.Success, timestamp: _clock.UtcNow));
        monitor.RecordSignal(TestFixtures.CreateSignal(
            SignalOutcome.Failure, timestamp: _clock.UtcNow.AddSeconds(1)));
        monitor.RecordSignal(TestFixtures.CreateSignal(
            SignalOutcome.Failure, timestamp: _clock.UtcNow.AddSeconds(2)));
        monitor.RecordSignal(TestFixtures.CreateSignal(
            SignalOutcome.Failure, timestamp: _clock.UtcNow.AddSeconds(3)));

        var snapshot = monitor.GetSnapshot();
        snapshot.ConsecutiveFailures.Should().Be(3);
    }

    [Fact]
    public void ConsecutiveFailures_resets_on_success()
    {
        var monitor = CreateMonitor();

        monitor.RecordSignal(TestFixtures.CreateSignal(
            SignalOutcome.Failure, timestamp: _clock.UtcNow));
        monitor.RecordSignal(TestFixtures.CreateSignal(
            SignalOutcome.Failure, timestamp: _clock.UtcNow.AddSeconds(1)));
        monitor.RecordSignal(TestFixtures.CreateSignal(
            SignalOutcome.Success, timestamp: _clock.UtcNow.AddSeconds(2)));

        var snapshot = monitor.GetSnapshot();
        snapshot.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public void Implements_IDependencyMonitor()
    {
        var monitor = CreateMonitor();
        monitor.Should().BeAssignableTo<IDependencyMonitor>();
    }

    [Fact]
    public void RecordSignal_throws_on_null()
    {
        var monitor = CreateMonitor();
        var act = () => monitor.RecordSignal(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Thread_safety_concurrent_record_and_snapshot()
    {
        var monitor = CreateMonitor();

        var writeTask = Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                monitor.RecordSignal(TestFixtures.CreateSignal(
                    timestamp: _clock.UtcNow.AddMilliseconds(i)));
            }
        });

        var readTask = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                // Should never throw
                _ = monitor.GetSnapshot();
                _ = monitor.CurrentState;
            }
        });

        await Task.WhenAll(writeTask, readTask);

        // No exception is the assertion; plus sanity check
        monitor.CurrentState.Should().BeOneOf(
            HealthState.Healthy, HealthState.Degraded, HealthState.CircuitOpen);
    }
}

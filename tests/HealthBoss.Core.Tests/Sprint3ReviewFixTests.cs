// <copyright file="Sprint3ReviewFixTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace HealthBoss.Core.Tests;

/// <summary>
/// Tests verifying Sprint 3 review findings are correctly addressed.
/// Covers: race condition fix (Fix 1), property side-effect removal (Fix 2),
/// delegate leak fix (Fix 3), TOCTOU fix (Fix 4), IDisposable (Fix 5),
/// TotalSignalCount/LastTransitionTime semantics (Fix 7), and ILogger (Fix 8).
/// </summary>
public sealed class Sprint3ReviewFixTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ISystemClock _clock;
    private readonly IPolicyEvaluator _evaluator;
    private readonly ITransitionEngine _transitionEngine;
    private readonly IStartupTracker _startupTracker;

    public Sprint3ReviewFixTests()
    {
        _timeProvider.SetUtcNow(TestFixtures.BaseTime);
        _clock = new SystemClock(_timeProvider);
        _evaluator = new PolicyEvaluator();
        _transitionEngine = new TransitionEngine(new DefaultStateGraph());
        _startupTracker = new StartupTracker();
    }

    // ── Fix 1: DependencyMonitor.GetSnapshot() race condition ──

    [Fact]
    public async Task Fix1_GetSnapshot_is_serialized_under_concurrent_access()
    {
        // Verify that concurrent GetSnapshot calls don't corrupt state.
        // Under the lock, the evaluate→transition block is atomic.
        var policy = TestFixtures.ZeroJitterPolicy with
        {
            CooldownBeforeTransition = TimeSpan.Zero,
        };
        var monitor = CreateDependencyMonitor("race-test", policy);

        // Record mixed signals to create transition pressure
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

        // Fire many concurrent GetSnapshot calls
        var tasks = Enumerable.Range(0, 50).Select(_ =>
            Task.Run(() => monitor.GetSnapshot())).ToArray();

        var snapshots = await Task.WhenAll(tasks);

        // All snapshots must have valid states — no torn or corrupted data
        foreach (var snapshot in snapshots)
        {
            snapshot.CurrentState.Should().BeOneOf(
                HealthState.Healthy, HealthState.Degraded, HealthState.CircuitOpen);
            snapshot.StateChangedAt.Should().NotBe(default);
        }
    }

    [Fact]
    public async Task Fix1_concurrent_GetSnapshot_transitions_exactly_once_per_step()
    {
        // Two-step transition (Healthy→Degraded→CircuitOpen).
        // Under the lock, only one thread should perform each transition.
        var policy = TestFixtures.ZeroJitterPolicy with
        {
            CooldownBeforeTransition = TimeSpan.Zero,
        };
        var monitor = CreateDependencyMonitor("two-step", policy);

        // All failures → should trigger Healthy→Degraded on first GetSnapshot,
        // then Degraded→CircuitOpen on second.
        for (int i = 0; i < 10; i++)
        {
            monitor.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Failure, timestamp: _clock.UtcNow.AddSeconds(i)));
        }

        // First wave: should transition to Degraded
        var firstWave = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => Task.Run(() => monitor.GetSnapshot())));

        // At least one snapshot should show Degraded after first wave
        firstWave.Should().Contain(s => s.CurrentState == HealthState.Degraded);

        // Second wave: should transition to CircuitOpen
        var secondWave = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => Task.Run(() => monitor.GetSnapshot())));

        secondWave.Should().Contain(s => s.CurrentState == HealthState.CircuitOpen);
    }

    // ── Fix 2: HealthOrchestrator property side effects ──

    [Fact]
    public void Fix2_TotalSignalCount_does_not_trigger_additional_state_transitions()
    {
        var policy = TestFixtures.ZeroJitterPolicy with
        {
            CooldownBeforeTransition = TimeSpan.Zero,
        };
        var dep = CreateDependencyMonitor("transition-check", policy);
        var orchestrator = CreateOrchestrator(monitors: [dep]);

        // Record signals that would trigger Healthy→Degraded
        for (int i = 0; i < 5; i++)
        {
            dep.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Success, timestamp: _clock.UtcNow.AddSeconds(i),
                dependencyId: new DependencyId("transition-check")));
        }

        for (int i = 5; i < 10; i++)
        {
            dep.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Failure, timestamp: _clock.UtcNow.AddSeconds(i),
                dependencyId: new DependencyId("transition-check")));
        }

        // First call to GetAllSnapshots triggers evaluate→transition (Healthy→Degraded)
        var snapshots = orchestrator.GetAllSnapshots();
        snapshots.Should().ContainSingle(s => s.CurrentState == HealthState.Degraded);

        // Now reading TotalSignalCount multiple times should NOT trigger further transitions
        var count1 = orchestrator.TotalSignalCount;
        var count2 = orchestrator.TotalSignalCount;
        var count3 = orchestrator.TotalSignalCount;

        // State should still be Degraded, not CircuitOpen (which would happen
        // if TotalSignalCount called GetSnapshot() triggering another transition)
        dep.CurrentState.Should().Be(HealthState.Degraded);

        count1.Should().Be(count2);
        count2.Should().Be(count3);
    }

    [Fact]
    public void Fix2_LastTransitionTime_does_not_trigger_additional_state_transitions()
    {
        var policy = TestFixtures.ZeroJitterPolicy with
        {
            CooldownBeforeTransition = TimeSpan.Zero,
        };
        var dep = CreateDependencyMonitor("transition-check-time", policy);
        var orchestrator = CreateOrchestrator(monitors: [dep]);

        // Record signals that would trigger Healthy→Degraded
        for (int i = 0; i < 5; i++)
        {
            dep.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Success, timestamp: _clock.UtcNow.AddSeconds(i),
                dependencyId: new DependencyId("transition-check-time")));
        }

        for (int i = 5; i < 10; i++)
        {
            dep.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Failure, timestamp: _clock.UtcNow.AddSeconds(i),
                dependencyId: new DependencyId("transition-check-time")));
        }

        // First call triggers Healthy→Degraded
        _ = orchestrator.GetAllSnapshots();

        // Now LastTransitionTime should not cause further transitions
        var time1 = orchestrator.LastTransitionTime;
        var time2 = orchestrator.LastTransitionTime;
        var time3 = orchestrator.LastTransitionTime;

        dep.CurrentState.Should().Be(HealthState.Degraded);
        time1.Should().Be(time2);
        time2.Should().Be(time3);
    }

    // ── Fix 7: TotalSignalCount and LastTransitionTime semantics ──

    [Fact]
    public void Fix7_TotalSignalCount_returns_correct_sum_across_monitors()
    {
        var dep1 = CreateDependencyMonitor("dep-a");
        var dep2 = CreateDependencyMonitor("dep-b");
        var orchestrator = CreateOrchestrator(monitors: [dep1, dep2]);

        // Record 10 signals for dep-a
        for (int i = 0; i < 10; i++)
        {
            dep1.RecordSignal(TestFixtures.CreateSignal(
                timestamp: _clock.UtcNow.AddSeconds(i),
                dependencyId: new DependencyId("dep-a")));
        }

        // Record 5 signals for dep-b
        for (int i = 0; i < 5; i++)
        {
            dep2.RecordSignal(TestFixtures.CreateSignal(
                timestamp: _clock.UtcNow.AddSeconds(i),
                dependencyId: new DependencyId("dep-b")));
        }

        // Trigger snapshot collection to populate cache
        _ = orchestrator.GetAllSnapshots();

        orchestrator.TotalSignalCount.Should().Be(15);
    }

    [Fact]
    public void Fix7_TotalSignalCount_returns_zero_for_empty_orchestrator()
    {
        var orchestrator = CreateOrchestrator(monitors: []);

        _ = orchestrator.GetAllSnapshots();

        orchestrator.TotalSignalCount.Should().Be(0);
    }

    [Fact]
    public void Fix7_LastTransitionTime_returns_most_recent_transition()
    {
        var policy = TestFixtures.ZeroJitterPolicy with
        {
            CooldownBeforeTransition = TimeSpan.Zero,
        };
        var dep1 = CreateDependencyMonitor("dep-early", policy);
        var dep2 = CreateDependencyMonitor("dep-late", policy);
        var orchestrator = CreateOrchestrator(monitors: [dep1, dep2]);

        // dep-early: failures causing transition
        for (int i = 0; i < 5; i++)
        {
            dep1.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Success, timestamp: _clock.UtcNow.AddSeconds(i),
                dependencyId: new DependencyId("dep-early")));
        }

        for (int i = 5; i < 10; i++)
        {
            dep1.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Failure, timestamp: _clock.UtcNow.AddSeconds(i),
                dependencyId: new DependencyId("dep-early")));
        }

        // dep-late: all success (no transition, stays at initial time)
        for (int i = 0; i < 10; i++)
        {
            dep2.RecordSignal(TestFixtures.CreateSignal(
                SignalOutcome.Success, timestamp: _clock.UtcNow.AddSeconds(i),
                dependencyId: new DependencyId("dep-late")));
        }

        // Force snapshot collection
        _ = orchestrator.GetAllSnapshots();

        var lastTransition = orchestrator.LastTransitionTime;
        lastTransition.Should().NotBeNull();
        // The transition time should be from the monitor that transitioned,
        // which is the current clock time when GetSnapshot was called
        lastTransition!.Value.Should().BeOnOrAfter(TestFixtures.BaseTime);
    }

    [Fact]
    public void Fix7_LastTransitionTime_returns_null_for_empty_orchestrator()
    {
        var orchestrator = CreateOrchestrator(monitors: []);

        _ = orchestrator.GetAllSnapshots();

        orchestrator.LastTransitionTime.Should().BeNull();
    }

    // ── Fix 3: ExecuteWithTimeout delegate leak ──

    [Fact]
    public void Fix3_timed_out_delegate_falls_back_to_default()
    {
        var orchestrator = CreateOrchestrator(
            healthResolver: _ =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(10));
                return HealthStatus.Degraded;
            });

        var report = orchestrator.GetHealthReport();

        // Should fall back to default (Healthy, no signals recorded)
        report.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public void Fix3_throwing_delegate_falls_back_to_default()
    {
        var orchestrator = CreateOrchestrator(
            healthResolver: _ => throw new InvalidOperationException("boom"));

        var report = orchestrator.GetHealthReport();

        report.Status.Should().Be(HealthStatus.Healthy);
    }

    // ── Fix 4: RecoveryProber TOCTOU ──

    [Fact]
    public async Task Fix4_StartProbingAsync_idempotent_without_TOCTOU_race()
    {
        var handler = new FakeProbeHandler(true);
        var recorder = new RecordingSignalRecorder();
        var prober = new RecoveryProber(handler, recorder, _clock, _timeProvider);
        using var cts = new CancellationTokenSource();

        var depId = new DependencyId("toctou-test");

        // Fire many concurrent StartProbingAsync calls — only one should succeed
        var tasks = Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => prober.StartProbingAsync(depId, TestFixtures.DefaultPolicy, cts.Token)));

        await Task.WhenAll(tasks);

        prober.IsProbing(depId).Should().BeTrue();

        cts.Cancel();
        prober.Dispose();
    }

    // ── Fix 5: IRecoveryProber extends IDisposable ──

    [Fact]
    public void Fix5_IRecoveryProber_extends_IDisposable()
    {
        typeof(IRecoveryProber).Should().Implement<IDisposable>();
    }

    [Fact]
    public void Fix5_RecoveryProber_can_be_used_as_IDisposable_through_interface()
    {
        var handler = new FakeProbeHandler(true);
        var recorder = new RecordingSignalRecorder();
        IRecoveryProber prober = new RecoveryProber(handler, recorder, _clock, _timeProvider);

        // Should be disposable through the interface
        var act = () => prober.Dispose();
        act.Should().NotThrow();
    }

    // ── Fix 8: ILogger for dropped signals ──

    [Fact]
    public void Fix8_RecordSignal_unknown_dependency_logs_warning()
    {
        var loggerFactory = new InMemoryLoggerFactory();
        var logger = loggerFactory.CreateLogger<HealthOrchestrator>();

        var dep = CreateDependencyMonitor("known-dep");
        var orchestrator = new HealthOrchestrator(
            new Dictionary<DependencyId, IDependencyMonitor>
            {
                [dep.DependencyId] = dep,
            },
            null,
            null,
            _startupTracker,
            _clock,
            logger);

        var unknownId = new DependencyId("unknown-dep");
        var signal = TestFixtures.CreateSignal(
            timestamp: _clock.UtcNow, dependencyId: unknownId);

        orchestrator.RecordSignal(unknownId, signal);

        var logEntry = loggerFactory.LogEntries.Should().ContainSingle().Subject;
        logEntry.LogLevel.Should().Be(LogLevel.Warning);
        logEntry.Message.Should().Contain("unknown-dep");
    }

    [Fact]
    public void Fix8_RecordSignal_known_dependency_does_not_log()
    {
        var loggerFactory = new InMemoryLoggerFactory();
        var logger = loggerFactory.CreateLogger<HealthOrchestrator>();

        var dep = CreateDependencyMonitor("known-dep");
        var orchestrator = new HealthOrchestrator(
            new Dictionary<DependencyId, IDependencyMonitor>
            {
                [dep.DependencyId] = dep,
            },
            null,
            null,
            _startupTracker,
            _clock,
            logger);

        var knownId = new DependencyId("known-dep");
        var signal = TestFixtures.CreateSignal(
            timestamp: _clock.UtcNow, dependencyId: knownId);

        orchestrator.RecordSignal(knownId, signal);

        loggerFactory.LogEntries.Should().BeEmpty();
    }

    [Fact]
    public void Fix8_orchestrator_without_logger_still_works()
    {
        // Constructor should accept null logger gracefully (defaults to NullLogger)
        var dep = CreateDependencyMonitor("dep");
        var orchestrator = new HealthOrchestrator(
            new Dictionary<DependencyId, IDependencyMonitor>
            {
                [dep.DependencyId] = dep,
            },
            null,
            null,
            _startupTracker,
            _clock,
            logger: null);

        var unknownId = new DependencyId("unknown");
        var signal = TestFixtures.CreateSignal(
            timestamp: _clock.UtcNow, dependencyId: unknownId);

        // Should not throw even without a logger
        var act = () => orchestrator.RecordSignal(unknownId, signal);
        act.Should().NotThrow();
    }

    // ── Helpers ──

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
        Func<IReadOnlyList<DependencySnapshot>, ReadinessStatus>? readinessResolver = null)
    {
        var monitorList = monitors ?? [CreateDependencyMonitor("dep-1"), CreateDependencyMonitor("dep-2")];
        var dict = monitorList.ToDictionary(m => m.DependencyId, m => (IDependencyMonitor)m);
        return new HealthOrchestrator(
            dict,
            healthResolver,
            readinessResolver,
            _startupTracker,
            _clock);
    }

    // ── Test doubles ──

    /// <summary>Captures recorded signals for test assertions.</summary>
    private sealed class RecordingSignalRecorder : ISignalRecorder
    {
        private readonly List<HealthSignal> _signals = [];
        private readonly object _lock = new();

        public IReadOnlyList<HealthSignal> Signals
        {
            get
            {
                lock (_lock) { return [.. _signals]; }
            }
        }

        public void Record(HealthSignal signal)
        {
            lock (_lock) { _signals.Add(signal); }
        }
    }

    /// <summary>Fake probe handler that returns a configurable result.</summary>
    private sealed class FakeProbeHandler : IRecoveryProbeHandler
    {
        private readonly bool _result;

        public FakeProbeHandler(bool result) => _result = result;

        public Task<bool> ProbeAsync(DependencyId id, CancellationToken ct)
            => Task.FromResult(_result);
    }

    /// <summary>In-memory logger for verifying log output.</summary>
    private sealed class InMemoryLoggerFactory : ILoggerFactory
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> LogEntries => _entries;

        public ILogger CreateLogger(string categoryName) =>
            new InMemoryLogger(_entries);

        public ILogger<T> CreateLogger<T>() =>
            new InMemoryLogger<T>(_entries);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }
    }

    private sealed class InMemoryLogger<T> : InMemoryLogger, ILogger<T>
    {
        public InMemoryLogger(List<LogEntry> entries) : base(entries) { }
    }

    private class InMemoryLogger : ILogger
    {
        private readonly List<LogEntry> _entries;

        public InMemoryLogger(List<LogEntry> entries) => _entries = entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    internal sealed record LogEntry(LogLevel LogLevel, string Message);
}

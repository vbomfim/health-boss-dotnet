// <copyright file="RecoveryProberTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using HealthBoss.Core.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace HealthBoss.Core.Tests;

/// <summary>
/// TDD tests for <see cref="RecoveryProber"/>.
/// Verifies periodic probing, signal recording, lifecycle control, and thread safety.
/// </summary>
public sealed class RecoveryProberTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ISystemClock _clock;
    private readonly RecordingSignalRecorder _recorder;

    public RecoveryProberTests()
    {
        _timeProvider.SetUtcNow(TestFixtures.BaseTime);
        _clock = new SystemClock(_timeProvider);
        _recorder = new RecordingSignalRecorder();
    }

    // ── Constructor guard clauses ──────────────────────────────

    [Fact]
    public void Constructor_null_handler_throws_ArgumentNullException()
    {
        var act = () => new RecoveryProber(null!, _recorder, _clock, _timeProvider);

        act.Should().Throw<ArgumentNullException>().WithParameterName("handler");
    }

    [Fact]
    public void Constructor_null_recorder_throws_ArgumentNullException()
    {
        var handler = new FakeProbeHandler(true);

        var act = () => new RecoveryProber(handler, null!, _clock, _timeProvider);

        act.Should().Throw<ArgumentNullException>().WithParameterName("recorder");
    }

    [Fact]
    public void Constructor_null_clock_throws_ArgumentNullException()
    {
        var handler = new FakeProbeHandler(true);

        var act = () => new RecoveryProber(handler, _recorder, null!, _timeProvider);

        act.Should().Throw<ArgumentNullException>().WithParameterName("clock");
    }

    [Fact]
    public void Constructor_null_timeProvider_throws_ArgumentNullException()
    {
        var handler = new FakeProbeHandler(true);

        var act = () => new RecoveryProber(handler, _recorder, _clock, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("timeProvider");
    }

    // ── IsProbing ──────────────────────────────────────────────

    [Fact]
    public void IsProbing_returns_false_for_unknown_dependency()
    {
        var prober = CreateProber(new FakeProbeHandler(true));

        prober.IsProbing(TestFixtures.DefaultDependencyId).Should().BeFalse();
    }

    [Fact]
    public async Task IsProbing_returns_true_after_StartProbingAsync()
    {
        var handler = new FakeProbeHandler(true);
        var prober = CreateProber(handler);
        using var cts = new CancellationTokenSource();

        await prober.StartProbingAsync(TestFixtures.DefaultDependencyId, TestFixtures.DefaultPolicy, cts.Token);

        prober.IsProbing(TestFixtures.DefaultDependencyId).Should().BeTrue();

        cts.Cancel();
    }

    [Fact]
    public async Task IsProbing_returns_false_after_StopProbing()
    {
        var handler = new FakeProbeHandler(true);
        var prober = CreateProber(handler);
        using var cts = new CancellationTokenSource();

        await prober.StartProbingAsync(TestFixtures.DefaultDependencyId, TestFixtures.DefaultPolicy, cts.Token);
        prober.StopProbing(TestFixtures.DefaultDependencyId);

        // Allow background loop to process cancellation
        await Task.Delay(50);

        prober.IsProbing(TestFixtures.DefaultDependencyId).Should().BeFalse();
    }

    // ── StartProbingAsync idempotency ──────────────────────────

    [Fact]
    public async Task StartProbingAsync_twice_is_idempotent()
    {
        var handler = new FakeProbeHandler(true);
        var prober = CreateProber(handler);
        using var cts = new CancellationTokenSource();

        await prober.StartProbingAsync(TestFixtures.DefaultDependencyId, TestFixtures.DefaultPolicy, cts.Token);
        await prober.StartProbingAsync(TestFixtures.DefaultDependencyId, TestFixtures.DefaultPolicy, cts.Token);

        prober.IsProbing(TestFixtures.DefaultDependencyId).Should().BeTrue();

        cts.Cancel();
    }

    // ── StopProbing idempotency ────────────────────────────────

    [Fact]
    public void StopProbing_for_unknown_dependency_is_noop()
    {
        var prober = CreateProber(new FakeProbeHandler(true));

        var act = () => prober.StopProbing(TestFixtures.DefaultDependencyId);

        act.Should().NotThrow();
    }

    // ── Probe calls at interval ────────────────────────────────

    [Fact]
    public async Task StartProbing_calls_ProbeAsync_at_configured_interval()
    {
        var handler = new FakeProbeHandler(true);
        var prober = CreateProber(handler);
        var policy = TestFixtures.DefaultPolicy; // RecoveryProbeInterval = 10s
        using var cts = new CancellationTokenSource();

        await prober.StartProbingAsync(TestFixtures.DefaultDependencyId, policy, cts.Token);

        // Allow background loop to reach Task.Delay before advancing fake time.
        await Task.Delay(50);

        // Advance time to trigger the first probe
        _timeProvider.Advance(policy.RecoveryProbeInterval);
        await handler.WaitForCallsAsync(1, timeout: TimeSpan.FromSeconds(2));

        handler.CallCount.Should().BeGreaterThanOrEqualTo(1);

        // Advance again for second probe
        await Task.Delay(50);
        _timeProvider.Advance(policy.RecoveryProbeInterval);
        await handler.WaitForCallsAsync(2, timeout: TimeSpan.FromSeconds(2));

        handler.CallCount.Should().BeGreaterThanOrEqualTo(2);

        cts.Cancel();
    }

    // ── Successful probe records success signal ────────────────

    [Fact]
    public async Task Successful_probe_records_success_signal()
    {
        var handler = new FakeProbeHandler(true);
        var prober = CreateProber(handler);
        using var cts = new CancellationTokenSource();

        await prober.StartProbingAsync(TestFixtures.DefaultDependencyId, TestFixtures.DefaultPolicy, cts.Token);

        // Allow background loop to reach Task.Delay before advancing fake time.
        await Task.Delay(50);
        _timeProvider.Advance(TestFixtures.DefaultPolicy.RecoveryProbeInterval);
        await handler.WaitForCallsAsync(1, timeout: TimeSpan.FromSeconds(2));

        // Allow time for signal recording after probe returns
        await Task.Delay(50);

        _recorder.Signals.Should().ContainSingle(s =>
            s.DependencyId == TestFixtures.DefaultDependencyId &&
            s.Outcome == SignalOutcome.Success);

        cts.Cancel();
    }

    // ── Failed probe records failure signal ────────────────────

    [Fact]
    public async Task Failed_probe_records_failure_signal()
    {
        var handler = new FakeProbeHandler(false);
        var prober = CreateProber(handler);
        using var cts = new CancellationTokenSource();

        await prober.StartProbingAsync(TestFixtures.DefaultDependencyId, TestFixtures.DefaultPolicy, cts.Token);

        // Allow background loop to reach Task.Delay before advancing fake time.
        await Task.Delay(50);
        _timeProvider.Advance(TestFixtures.DefaultPolicy.RecoveryProbeInterval);
        await handler.WaitForCallsAsync(1, timeout: TimeSpan.FromSeconds(2));

        await Task.Delay(50);

        _recorder.Signals.Should().ContainSingle(s =>
            s.DependencyId == TestFixtures.DefaultDependencyId &&
            s.Outcome == SignalOutcome.Failure);

        cts.Cancel();
    }

    // ── Probe exception records failure signal ─────────────────

    [Fact]
    public async Task Probe_exception_records_failure_signal()
    {
        var handler = new ThrowingProbeHandler();
        var prober = CreateProber(handler);
        using var cts = new CancellationTokenSource();

        await prober.StartProbingAsync(TestFixtures.DefaultDependencyId, TestFixtures.DefaultPolicy, cts.Token);

        // Allow background loop to reach Task.Delay before advancing fake time.
        await Task.Delay(50);
        _timeProvider.Advance(TestFixtures.DefaultPolicy.RecoveryProbeInterval);
        await handler.WaitForCallsAsync(1, timeout: TimeSpan.FromSeconds(2));

        await Task.Delay(50);

        _recorder.Signals.Should().ContainSingle(s =>
            s.DependencyId == TestFixtures.DefaultDependencyId &&
            s.Outcome == SignalOutcome.Failure);

        cts.Cancel();
    }

    // ── StopProbing stops further probe calls ──────────────────

    [Fact]
    public async Task StopProbing_prevents_further_probe_calls()
    {
        var handler = new FakeProbeHandler(true);
        var prober = CreateProber(handler);
        using var cts = new CancellationTokenSource();

        await prober.StartProbingAsync(TestFixtures.DefaultDependencyId, TestFixtures.DefaultPolicy, cts.Token);

        // Allow background loop to reach Task.Delay before advancing fake time.
        await Task.Delay(50);

        // Trigger one probe
        _timeProvider.Advance(TestFixtures.DefaultPolicy.RecoveryProbeInterval);
        await handler.WaitForCallsAsync(1, timeout: TimeSpan.FromSeconds(2));

        var countAfterFirst = handler.CallCount;

        // Stop probing
        prober.StopProbing(TestFixtures.DefaultDependencyId);
        await Task.Delay(50);

        // Advance time — no more probes should happen
        _timeProvider.Advance(TestFixtures.DefaultPolicy.RecoveryProbeInterval);
        _timeProvider.Advance(TestFixtures.DefaultPolicy.RecoveryProbeInterval);
        await Task.Delay(100);

        handler.CallCount.Should().Be(countAfterFirst);
    }

    // ── CancellationToken stops probing ────────────────────────

    [Fact]
    public async Task CancellationToken_stops_probing()
    {
        var handler = new FakeProbeHandler(true);
        var prober = CreateProber(handler);
        using var cts = new CancellationTokenSource();

        await prober.StartProbingAsync(TestFixtures.DefaultDependencyId, TestFixtures.DefaultPolicy, cts.Token);

        // Allow background loop to reach Task.Delay before advancing fake time.
        await Task.Delay(50);

        // Trigger one probe
        _timeProvider.Advance(TestFixtures.DefaultPolicy.RecoveryProbeInterval);
        await handler.WaitForCallsAsync(1, timeout: TimeSpan.FromSeconds(2));

        var countAfterFirst = handler.CallCount;

        // Cancel
        cts.Cancel();
        await Task.Delay(50);

        // Advance time — no more probes should happen
        _timeProvider.Advance(TestFixtures.DefaultPolicy.RecoveryProbeInterval);
        _timeProvider.Advance(TestFixtures.DefaultPolicy.RecoveryProbeInterval);
        await Task.Delay(100);

        handler.CallCount.Should().Be(countAfterFirst);
        prober.IsProbing(TestFixtures.DefaultDependencyId).Should().BeFalse();
    }

    // ── Multiple dependencies probed independently ─────────────

    [Fact]
    public async Task Multiple_dependencies_probed_independently()
    {
        var handler = new FakeProbeHandler(true);
        var prober = CreateProber(handler);
        using var cts = new CancellationTokenSource();

        var dep1 = new DependencyId("dep-one");
        var dep2 = new DependencyId("dep-two");

        await prober.StartProbingAsync(dep1, TestFixtures.DefaultPolicy, cts.Token);
        await prober.StartProbingAsync(dep2, TestFixtures.DefaultPolicy, cts.Token);

        prober.IsProbing(dep1).Should().BeTrue();
        prober.IsProbing(dep2).Should().BeTrue();

        // Allow background loops to reach Task.Delay before advancing fake time.
        await Task.Delay(50);

        // Advance to trigger probes for both
        _timeProvider.Advance(TestFixtures.DefaultPolicy.RecoveryProbeInterval);
        await handler.WaitForCallsAsync(2, timeout: TimeSpan.FromSeconds(2));

        // Stop only dep1
        prober.StopProbing(dep1);
        await Task.Delay(50);

        prober.IsProbing(dep1).Should().BeFalse();
        prober.IsProbing(dep2).Should().BeTrue();

        cts.Cancel();
    }

    // ── Signal timestamp uses clock ────────────────────────────

    [Fact]
    public async Task Recorded_signal_timestamp_uses_system_clock()
    {
        var handler = new FakeProbeHandler(true);
        var prober = CreateProber(handler);
        using var cts = new CancellationTokenSource();

        await prober.StartProbingAsync(TestFixtures.DefaultDependencyId, TestFixtures.DefaultPolicy, cts.Token);

        // Allow background loop to reach Task.Delay before advancing fake time.
        await Task.Delay(50);
        _timeProvider.Advance(TestFixtures.DefaultPolicy.RecoveryProbeInterval);
        await handler.WaitForCallsAsync(1, timeout: TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        var expectedTime = TestFixtures.BaseTime + TestFixtures.DefaultPolicy.RecoveryProbeInterval;
        _recorder.Signals.Should().ContainSingle()
            .Which.Timestamp.Should().BeCloseTo(expectedTime, precision: TimeSpan.FromSeconds(1));

        cts.Cancel();
    }

    // ── Restart after stop ─────────────────────────────────────

    [Fact]
    public async Task Can_restart_probing_after_stop()
    {
        var handler = new FakeProbeHandler(true);
        var prober = CreateProber(handler);
        using var cts = new CancellationTokenSource();

        await prober.StartProbingAsync(TestFixtures.DefaultDependencyId, TestFixtures.DefaultPolicy, cts.Token);
        prober.StopProbing(TestFixtures.DefaultDependencyId);
        await Task.Delay(50);

        prober.IsProbing(TestFixtures.DefaultDependencyId).Should().BeFalse();

        // Restart
        await prober.StartProbingAsync(TestFixtures.DefaultDependencyId, TestFixtures.DefaultPolicy, cts.Token);

        prober.IsProbing(TestFixtures.DefaultDependencyId).Should().BeTrue();

        cts.Cancel();
    }

    // ── Thread safety: concurrent start/stop ───────────────────

    [Fact]
    public async Task Concurrent_start_and_stop_does_not_throw()
    {
        var handler = new FakeProbeHandler(true);
        var prober = CreateProber(handler);
        using var cts = new CancellationTokenSource();
        var deps = Enumerable.Range(0, 10).Select(i => new DependencyId($"dep-{i}")).ToList();

        var startTasks = deps.Select(d =>
            Task.Run(() => prober.StartProbingAsync(d, TestFixtures.DefaultPolicy, cts.Token)));

        var stopTasks = deps.Select(d =>
            Task.Run(() => prober.StopProbing(d)));

        var act = () => Task.WhenAll(startTasks.Concat(stopTasks));

        await act.Should().NotThrowAsync();

        cts.Cancel();
    }

    // ── Dispose stops all probing ──────────────────────────────

    [Fact]
    public async Task Dispose_stops_all_active_probing()
    {
        var handler = new FakeProbeHandler(true);
        var prober = CreateProber(handler);
        using var cts = new CancellationTokenSource();

        var dep1 = new DependencyId("dep-one");
        var dep2 = new DependencyId("dep-two");

        await prober.StartProbingAsync(dep1, TestFixtures.DefaultPolicy, cts.Token);
        await prober.StartProbingAsync(dep2, TestFixtures.DefaultPolicy, cts.Token);

        prober.Dispose();
        await Task.Delay(50);

        prober.IsProbing(dep1).Should().BeFalse();
        prober.IsProbing(dep2).Should().BeFalse();
    }

    // ── Logging: exception logs Warning ────────────────────────

    [Fact]
    public async Task Probe_exception_logs_Warning_with_exception_and_component()
    {
        var logger = new FakeLogger<RecoveryProber>();
        var handler = new ThrowingProbeHandler();
        var prober = CreateProber(handler, logger: logger);
        using var cts = new CancellationTokenSource();

        await prober.StartProbingAsync(TestFixtures.DefaultDependencyId, TestFixtures.DefaultPolicy, cts.Token);

        await Task.Delay(50);
        _timeProvider.Advance(TestFixtures.DefaultPolicy.RecoveryProbeInterval);
        await handler.WaitForCallsAsync(1, timeout: TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        cts.Cancel();
        await Task.Delay(50);

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains(TestFixtures.DefaultDependencyId.Value) &&
            e.Exception is InvalidOperationException);
    }

    // ── Logging: successful probe logs Information ──────────────

    [Fact]
    public async Task Successful_probe_logs_Information_with_outcome()
    {
        var logger = new FakeLogger<RecoveryProber>();
        var handler = new FakeProbeHandler(true);
        var prober = CreateProber(handler, logger: logger);
        using var cts = new CancellationTokenSource();

        await prober.StartProbingAsync(TestFixtures.DefaultDependencyId, TestFixtures.DefaultPolicy, cts.Token);

        await Task.Delay(50);
        _timeProvider.Advance(TestFixtures.DefaultPolicy.RecoveryProbeInterval);
        await handler.WaitForCallsAsync(1, timeout: TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        cts.Cancel();
        await Task.Delay(50);

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains(TestFixtures.DefaultDependencyId.Value) &&
            e.Message.Contains("Success"));
    }

    // ── Logging: failed probe logs Information ──────────────────

    [Fact]
    public async Task Failed_probe_logs_Information_with_failure_outcome()
    {
        var logger = new FakeLogger<RecoveryProber>();
        var handler = new FakeProbeHandler(false);
        var prober = CreateProber(handler, logger: logger);
        using var cts = new CancellationTokenSource();

        await prober.StartProbingAsync(TestFixtures.DefaultDependencyId, TestFixtures.DefaultPolicy, cts.Token);

        await Task.Delay(50);
        _timeProvider.Advance(TestFixtures.DefaultPolicy.RecoveryProbeInterval);
        await handler.WaitForCallsAsync(1, timeout: TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        cts.Cancel();
        await Task.Delay(50);

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains(TestFixtures.DefaultDependencyId.Value) &&
            e.Message.Contains("Failure"));
    }

    // ── Logging: exception probe also logs Information ──────────

    [Fact]
    public async Task Probe_exception_also_logs_Information_with_failure_outcome()
    {
        var logger = new FakeLogger<RecoveryProber>();
        var handler = new ThrowingProbeHandler();
        var prober = CreateProber(handler, logger: logger);
        using var cts = new CancellationTokenSource();

        await prober.StartProbingAsync(TestFixtures.DefaultDependencyId, TestFixtures.DefaultPolicy, cts.Token);

        await Task.Delay(50);
        _timeProvider.Advance(TestFixtures.DefaultPolicy.RecoveryProbeInterval);
        await handler.WaitForCallsAsync(1, timeout: TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        cts.Cancel();
        await Task.Delay(50);

        // Should have both: Warning for the exception AND Information for the outcome
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning &&
            e.Exception is InvalidOperationException);
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("Failure"));
    }

    // ── Logging: null logger uses NullLogger (no crash) ────────

    [Fact]
    public async Task Null_logger_falls_back_to_NullLogger()
    {
        // Ensures the constructor default (NullLogger) doesn't crash at runtime
        var handler = new ThrowingProbeHandler();
        var prober = CreateProber(handler); // no logger passed
        using var cts = new CancellationTokenSource();

        await prober.StartProbingAsync(TestFixtures.DefaultDependencyId, TestFixtures.DefaultPolicy, cts.Token);

        await Task.Delay(50);
        _timeProvider.Advance(TestFixtures.DefaultPolicy.RecoveryProbeInterval);
        await handler.WaitForCallsAsync(1, timeout: TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        cts.Cancel();
        await Task.Delay(50);

        // No exception means NullLogger handled silently — pass
        _recorder.Signals.Should().ContainSingle(s =>
            s.Outcome == SignalOutcome.Failure);
    }

    // ── Helper methods ─────────────────────────────────────────

    private RecoveryProber CreateProber(
        IRecoveryProbeHandler handler,
        ILogger<RecoveryProber>? logger = null) =>
        new(handler, _recorder, _clock, _timeProvider, logger);

    // ── Test doubles ───────────────────────────────────────────

    /// <summary>
    /// Captures recorded signals for test assertions.
    /// </summary>
    private sealed class RecordingSignalRecorder : ISignalWriter
    {
        private readonly List<HealthSignal> _signals = [];
        private readonly object _lock = new();

        public IReadOnlyList<HealthSignal> Signals
        {
            get
            {
                lock (_lock)
                {
                    return [.. _signals];
                }
            }
        }

        public void Record(HealthSignal signal)
        {
            lock (_lock)
            {
                _signals.Add(signal);
            }
        }
    }

    /// <summary>
    /// Fake probe handler that returns a configurable result.
    /// </summary>
    private sealed class FakeProbeHandler : IRecoveryProbeHandler
    {
        private readonly bool _result;
        private int _callCount;
        private readonly SemaphoreSlim _callSemaphore = new(0);

        public FakeProbeHandler(bool result) => _result = result;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<bool> ProbeAsync(DependencyId id, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            _callSemaphore.Release();
            return Task.FromResult(_result);
        }

        public async Task WaitForCallsAsync(int expectedCount, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (CallCount < expectedCount)
            {
                try
                {
                    await _callSemaphore.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Probe handler that always throws an exception.
    /// </summary>
    private sealed class ThrowingProbeHandler : IRecoveryProbeHandler
    {
        private int _callCount;
        private readonly SemaphoreSlim _callSemaphore = new(0);

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<bool> ProbeAsync(DependencyId id, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            _callSemaphore.Release();
            throw new InvalidOperationException("Probe failed");
        }

        public async Task WaitForCallsAsync(int expectedCount, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (CallCount < expectedCount)
            {
                try
                {
                    await _callSemaphore.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}

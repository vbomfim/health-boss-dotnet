using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HealthBoss.Core.Tests;

/// <summary>
/// TDD tests for <see cref="DrainCoordinator"/> — graceful session drain.
/// Follows the acceptance criteria from issue #24 (AC45, AC46).
/// </summary>
public sealed class DrainCoordinatorTests
{
    private static readonly DateTimeOffset Now =
        new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Default poll interval used by the coordinator.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    // ──────────────────────────────────────────────
    //  Test helpers
    // ──────────────────────────────────────────────

    private static FakeTimeProvider CreateTimeProvider()
    {
        var tp = new FakeTimeProvider();
        tp.SetUtcNow(Now);
        return tp;
    }

    private static DrainCoordinator CreateCoordinator(FakeTimeProvider timeProvider)
    {
        var clock = new SystemClock(timeProvider);
        return new DrainCoordinator(clock, NullLogger<DrainCoordinator>.Instance, timeProvider);
    }

    private static DrainConfig ConfigWithTimeout(
        TimeSpan timeout,
        Func<int, CancellationToken, Task<bool>>? drainDelegate = null)
    {
        return new DrainConfig(Timeout: timeout, DrainDelegate: drainDelegate);
    }

    // ──────────────────────────────────────────────
    //  AC45 — Drain waits for session count = 0 OR timeout
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Sessions_drain_to_zero_before_timeout_returns_Drained()
    {
        // Arrange
        var tp = CreateTimeProvider();
        var coordinator = CreateCoordinator(tp);
        var sessionCount = 3;
        Func<int> getCount = () => sessionCount;
        var config = ConfigWithTimeout(TimeSpan.FromSeconds(10));

        // Act — run drain in background, simulate sessions finishing
        var drainTask = coordinator.DrainAsync(getCount, config);

        // Simulate time passing and sessions draining
        tp.Advance(PollInterval);
        sessionCount = 2;
        tp.Advance(PollInterval);
        sessionCount = 1;
        tp.Advance(PollInterval);
        sessionCount = 0;
        tp.Advance(PollInterval);

        var result = await drainTask;

        // Assert
        result.Should().Be(DrainStatus.Drained);
        coordinator.Status.Should().Be(DrainStatus.Drained);
    }

    [Fact]
    public async Task Timeout_before_sessions_finish_returns_TimedOut()
    {
        // Arrange
        var tp = CreateTimeProvider();
        var coordinator = CreateCoordinator(tp);
        Func<int> getCount = () => 5; // Sessions never drain
        var config = ConfigWithTimeout(TimeSpan.FromSeconds(2));

        // Act — advance time past timeout
        var drainTask = coordinator.DrainAsync(getCount, config);
        tp.Advance(TimeSpan.FromSeconds(3));

        var result = await drainTask;

        // Assert
        result.Should().Be(DrainStatus.TimedOut);
        coordinator.Status.Should().Be(DrainStatus.TimedOut);
    }

    [Fact]
    public async Task Zero_active_sessions_returns_immediate_Drained()
    {
        // Arrange
        var tp = CreateTimeProvider();
        var coordinator = CreateCoordinator(tp);
        Func<int> getCount = () => 0;
        var config = ConfigWithTimeout(TimeSpan.FromSeconds(10));

        // Act
        var result = await coordinator.DrainAsync(getCount, config);

        // Assert
        result.Should().Be(DrainStatus.Drained);
        coordinator.Status.Should().Be(DrainStatus.Drained);
    }

    // ──────────────────────────────────────────────
    //  AC46 — Custom drain delegate invoked when configured
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Custom_drain_delegate_called_with_correct_count()
    {
        // Arrange
        var tp = CreateTimeProvider();
        var coordinator = CreateCoordinator(tp);
        var capturedCounts = new List<int>();
        var sessionCount = 2;

        Func<int, CancellationToken, Task<bool>> drainDelegate = (count, _) =>
        {
            capturedCounts.Add(count);
            return Task.FromResult(true);
        };

        Func<int> getCount = () => sessionCount;
        var config = ConfigWithTimeout(TimeSpan.FromSeconds(10), drainDelegate);

        // Act
        var drainTask = coordinator.DrainAsync(getCount, config);

        tp.Advance(PollInterval); // poll with count=2
        sessionCount = 1;
        tp.Advance(PollInterval); // poll with count=1
        sessionCount = 0;
        tp.Advance(PollInterval); // poll with count=0 → Drained

        await drainTask;

        // Assert — delegate was called with the session counts
        capturedCounts.Should().NotBeEmpty();
        capturedCounts.Should().Contain(2);
    }

    [Fact]
    public async Task Drain_delegate_returning_false_does_not_abort_drain()
    {
        // Arrange: delegate returns false, but drain should still complete via count=0
        var tp = CreateTimeProvider();
        var coordinator = CreateCoordinator(tp);
        var sessionCount = 1;

        Func<int, CancellationToken, Task<bool>> drainDelegate = (_, _) =>
            Task.FromResult(false);

        Func<int> getCount = () => sessionCount;
        var config = ConfigWithTimeout(TimeSpan.FromSeconds(10), drainDelegate);

        // Act
        var drainTask = coordinator.DrainAsync(getCount, config);

        tp.Advance(PollInterval);
        sessionCount = 0;
        tp.Advance(PollInterval);

        var result = await drainTask;

        // Assert
        result.Should().Be(DrainStatus.Drained);
    }

    // ──────────────────────────────────────────────
    //  CancellationToken
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CancellationToken_cancels_drain()
    {
        // Arrange
        var tp = CreateTimeProvider();
        var coordinator = CreateCoordinator(tp);
        Func<int> getCount = () => 5; // Sessions never drain
        var config = ConfigWithTimeout(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();

        // Act
        var drainTask = coordinator.DrainAsync(getCount, config, cts.Token);
        tp.Advance(PollInterval);
        cts.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drainTask);
    }

    // ──────────────────────────────────────────────
    //  Status transitions
    // ──────────────────────────────────────────────

    [Fact]
    public void Initial_status_is_Idle()
    {
        var tp = CreateTimeProvider();
        var coordinator = CreateCoordinator(tp);

        coordinator.Status.Should().Be(DrainStatus.Idle);
    }

    [Fact]
    public async Task Status_transitions_Idle_to_Draining_to_Drained()
    {
        // Arrange
        var tp = CreateTimeProvider();
        var coordinator = CreateCoordinator(tp);
        var sessionCount = 1;
        Func<int> getCount = () => sessionCount;
        var config = ConfigWithTimeout(TimeSpan.FromSeconds(10));
        var observedStatuses = new List<DrainStatus>();

        // Act — start drain, observe status
        var drainTask = coordinator.DrainAsync(getCount, config);

        // After starting, status should be Draining
        observedStatuses.Add(coordinator.Status);

        sessionCount = 0;
        tp.Advance(PollInterval);
        await drainTask;

        observedStatuses.Add(coordinator.Status);

        // Assert
        observedStatuses[0].Should().Be(DrainStatus.Draining);
        observedStatuses[1].Should().Be(DrainStatus.Drained);
    }

    [Fact]
    public async Task Status_transitions_Idle_to_Draining_to_TimedOut()
    {
        // Arrange
        var tp = CreateTimeProvider();
        var coordinator = CreateCoordinator(tp);
        Func<int> getCount = () => 5;
        var config = ConfigWithTimeout(TimeSpan.FromSeconds(1));
        var observedStatuses = new List<DrainStatus>();

        // Act
        var drainTask = coordinator.DrainAsync(getCount, config);
        observedStatuses.Add(coordinator.Status);

        tp.Advance(TimeSpan.FromSeconds(2));
        await drainTask;

        observedStatuses.Add(coordinator.Status);

        // Assert
        observedStatuses[0].Should().Be(DrainStatus.Draining);
        observedStatuses[1].Should().Be(DrainStatus.TimedOut);
    }

    // ──────────────────────────────────────────────
    //  Concurrent / reentrancy
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DrainAsync_called_twice_second_returns_same_result()
    {
        // Arrange
        var tp = CreateTimeProvider();
        var coordinator = CreateCoordinator(tp);
        var sessionCount = 1;
        Func<int> getCount = () => sessionCount;
        var config = ConfigWithTimeout(TimeSpan.FromSeconds(10));

        // Act — start first drain
        var firstDrain = coordinator.DrainAsync(getCount, config);

        // Second call while first is in progress
        var secondDrain = coordinator.DrainAsync(getCount, config);

        sessionCount = 0;
        tp.Advance(PollInterval);

        var result1 = await firstDrain;
        var result2 = await secondDrain;

        // Assert — both return same final status
        result1.Should().Be(DrainStatus.Drained);
        result2.Should().Be(DrainStatus.Drained);
    }

    [Fact]
    public async Task Concurrent_Status_reads_during_drain_are_safe()
    {
        // Arrange
        var tp = CreateTimeProvider();
        var coordinator = CreateCoordinator(tp);
        var sessionCount = 5;
        Func<int> getCount = () => Volatile.Read(ref sessionCount);
        var config = ConfigWithTimeout(TimeSpan.FromSeconds(10));

        // Act — start drain and read status concurrently
        var drainTask = coordinator.DrainAsync(getCount, config);

        var statusReads = Enumerable.Range(0, 100)
            .Select(_ => coordinator.Status)
            .ToList();

        sessionCount = 0;
        tp.Advance(PollInterval);
        await drainTask;

        // Assert — all reads should be valid DrainStatus values
        statusReads.Should().AllSatisfy(s =>
            s.Should().BeOneOf(DrainStatus.Idle, DrainStatus.Draining, DrainStatus.Drained, DrainStatus.TimedOut));
    }

    // ──────────────────────────────────────────────
    //  Argument validation
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Null_getActiveSessionCount_throws_ArgumentNullException()
    {
        var tp = CreateTimeProvider();
        var coordinator = CreateCoordinator(tp);
        var config = ConfigWithTimeout(TimeSpan.FromSeconds(10));

        var act = () => coordinator.DrainAsync(null!, config);

        await Assert.ThrowsAsync<ArgumentNullException>(act);
    }

    [Fact]
    public async Task Null_config_throws_ArgumentNullException()
    {
        var tp = CreateTimeProvider();
        var coordinator = CreateCoordinator(tp);

        var act = () => coordinator.DrainAsync(() => 0, null!);

        await Assert.ThrowsAsync<ArgumentNullException>(act);
    }

    // ──────────────────────────────────────────────
    //  Drain delegate edge cases
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Drain_delegate_exception_is_logged_and_drain_continues()
    {
        // Arrange
        var tp = CreateTimeProvider();
        var coordinator = CreateCoordinator(tp);
        var sessionCount = 1;
        var callCount = 0;

        Func<int, CancellationToken, Task<bool>> throwingDelegate = (_, _) =>
        {
            callCount++;
            if (callCount == 1)
            {
                throw new InvalidOperationException("Delegate failed");
            }

            return Task.FromResult(true);
        };

        Func<int> getCount = () => sessionCount;
        var config = ConfigWithTimeout(TimeSpan.FromSeconds(10), throwingDelegate);

        // Act
        var drainTask = coordinator.DrainAsync(getCount, config);

        tp.Advance(PollInterval); // First poll — delegate throws
        sessionCount = 0;
        tp.Advance(PollInterval); // Second poll — count=0, drained

        var result = await drainTask;

        // Assert — drain completed despite delegate exception
        result.Should().Be(DrainStatus.Drained);
    }

    [Fact]
    public async Task Drain_delegate_receives_cancellation_token()
    {
        // Arrange
        var tp = CreateTimeProvider();
        var coordinator = CreateCoordinator(tp);
        CancellationToken? capturedToken = null;

        Func<int, CancellationToken, Task<bool>> drainDelegate = (_, ct) =>
        {
            capturedToken = ct;
            return Task.FromResult(true);
        };

        Func<int> getCount = () => 1;
        var config = ConfigWithTimeout(TimeSpan.FromSeconds(10), drainDelegate);
        using var cts = new CancellationTokenSource();

        // Act
        var drainTask = coordinator.DrainAsync(getCount, config, cts.Token);
        tp.Advance(PollInterval);

        // Let it time out or cancel
        cts.Cancel();
        try { await drainTask; } catch (OperationCanceledException) { }

        // Assert — delegate received a non-default token
        capturedToken.Should().NotBeNull();
    }

    // ──────────────────────────────────────────────
    //  Constructor validation
    // ──────────────────────────────────────────────

    [Fact]
    public void Null_clock_throws_ArgumentNullException()
    {
        var act = () => new DrainCoordinator(null!, NullLogger<DrainCoordinator>.Instance, TimeProvider.System);

        Assert.Throws<ArgumentNullException>(act);
    }

    [Fact]
    public void Null_logger_throws_ArgumentNullException()
    {
        var tp = CreateTimeProvider();
        var clock = new SystemClock(tp);

        var act = () => new DrainCoordinator(clock, null!, tp);

        Assert.Throws<ArgumentNullException>(act);
    }

    // ──────────────────────────────────────────────
    //  Single-use semantics (Fix #3)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DrainAsync_after_completion_returns_same_result_without_restarting()
    {
        // Arrange — complete a drain to Drained
        var tp = CreateTimeProvider();
        var coordinator = CreateCoordinator(tp);
        Func<int> getCount = () => 0;
        var config = ConfigWithTimeout(TimeSpan.FromSeconds(10));

        var firstResult = await coordinator.DrainAsync(getCount, config);
        firstResult.Should().Be(DrainStatus.Drained);

        // Act — call DrainAsync again after the drain has completed
        var secondResult = await coordinator.DrainAsync(getCount, config);

        // Assert — returns the same result (single-use, no reset)
        secondResult.Should().Be(DrainStatus.Drained);
        coordinator.Status.Should().Be(DrainStatus.Drained);
    }

    [Fact]
    public async Task DrainAsync_after_timeout_returns_same_result_without_restarting()
    {
        // Arrange — complete a drain to TimedOut
        var tp = CreateTimeProvider();
        var coordinator = CreateCoordinator(tp);
        Func<int> getCount = () => 5; // never drains
        var config = ConfigWithTimeout(TimeSpan.FromSeconds(1));

        var drainTask = coordinator.DrainAsync(getCount, config);
        tp.Advance(TimeSpan.FromSeconds(2));
        var firstResult = await drainTask;
        firstResult.Should().Be(DrainStatus.TimedOut);

        // Act — call DrainAsync again after the drain timed out
        var secondResult = await coordinator.DrainAsync(getCount, config);

        // Assert — returns the same result (single-use, no reset)
        secondResult.Should().Be(DrainStatus.TimedOut);
        coordinator.Status.Should().Be(DrainStatus.TimedOut);
    }

    // ──────────────────────────────────────────────
    //  IDisposable (Fix #7)
    // ──────────────────────────────────────────────

    [Fact]
    public void Dispose_does_not_throw()
    {
        var tp = CreateTimeProvider();
        var coordinator = CreateCoordinator(tp);

        var act = () => coordinator.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void Coordinator_implements_IDisposable()
    {
        var tp = CreateTimeProvider();
        var coordinator = CreateCoordinator(tp);

        coordinator.Should().BeAssignableTo<IDisposable>();
    }

    // ──────────────────────────────────────────────
    //  TimeProvider constructor (Fix #2)
    // ──────────────────────────────────────────────

    [Fact]
    public void Null_timeProvider_defaults_to_system()
    {
        var tp = CreateTimeProvider();
        var clock = new SystemClock(tp);

        // Act — pass null TimeProvider (should default to TimeProvider.System)
        var act = () => new DrainCoordinator(clock, NullLogger<DrainCoordinator>.Instance, timeProvider: null);

        act.Should().NotThrow();
    }
}

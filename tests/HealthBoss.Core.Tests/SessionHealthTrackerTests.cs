// <copyright file="SessionHealthTrackerTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace HealthBoss.Core.Tests;

/// <summary>
/// Unit tests for <see cref="SessionHealthTracker"/> covering lifecycle tracking,
/// active session gauge, sliding window success rates, and thread safety.
/// </summary>
public sealed class SessionHealthTrackerTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ISystemClock _clock;

    public SessionHealthTrackerTests()
    {
        _timeProvider.SetUtcNow(TestFixtures.BaseTime);
        _clock = new SystemClock(_timeProvider);
    }

    private SessionHealthTracker CreateTracker(TimeSpan? slidingWindow = null) =>
        new(_clock, slidingWindow);

    // ──────────────────────────────────────────────
    //  AC43: Session start/complete lifecycle tracking
    // ──────────────────────────────────────────────

    [Fact]
    public void TrackSessionStart_increments_ActiveSessionCount()
    {
        var tracker = CreateTracker();

        using var handle = tracker.TrackSessionStart("websocket", "session-1");

        tracker.ActiveSessionCount.Should().Be(1);
    }

    [Fact]
    public void Complete_Success_and_Dispose_decrements_ActiveSessionCount()
    {
        var tracker = CreateTracker();
        var handle = tracker.TrackSessionStart("websocket", "session-1");

        handle.Complete(SessionOutcome.Success);
        handle.Dispose();

        tracker.ActiveSessionCount.Should().Be(0);
    }

    [Fact]
    public void Dispose_without_Complete_records_Cancelled_outcome()
    {
        var tracker = CreateTracker();

        using (tracker.TrackSessionStart("websocket", "session-1"))
        {
            // Intentionally not calling Complete
        }

        tracker.ActiveSessionCount.Should().Be(0);

        var snapshot = tracker.GetSnapshot();
        snapshot.RecentFailures.Should().Be(1, "Cancelled counts as a failure");
        snapshot.RecentSuccesses.Should().Be(0);
    }

    [Fact]
    public void Complete_called_twice_only_records_first_outcome()
    {
        var tracker = CreateTracker();
        var handle = tracker.TrackSessionStart("websocket", "session-1");

        handle.Complete(SessionOutcome.Success);
        handle.Complete(SessionOutcome.Failure); // should be ignored
        handle.Dispose(); // should also be ignored (already completed)

        tracker.ActiveSessionCount.Should().Be(0);

        var snapshot = tracker.GetSnapshot();
        snapshot.RecentSuccesses.Should().Be(1);
        snapshot.RecentFailures.Should().Be(0);
    }

    // ──────────────────────────────────────────────
    //  AC44: Active session count (Interlocked gauge)
    // ──────────────────────────────────────────────

    [Fact]
    public void Multiple_concurrent_sessions_tracked_independently()
    {
        var tracker = CreateTracker();

        var handle1 = tracker.TrackSessionStart("websocket", "session-1");
        var handle2 = tracker.TrackSessionStart("grpc", "session-2");
        var handle3 = tracker.TrackSessionStart("websocket", "session-3");

        tracker.ActiveSessionCount.Should().Be(3);

        handle2.Complete(SessionOutcome.Success);
        handle2.Dispose();

        tracker.ActiveSessionCount.Should().Be(2);

        handle1.Complete(SessionOutcome.Failure);
        handle1.Dispose();

        tracker.ActiveSessionCount.Should().Be(1);

        handle3.Dispose(); // Cancelled

        tracker.ActiveSessionCount.Should().Be(0);
    }

    // ──────────────────────────────────────────────
    //  AC47: Session success rate in sliding window
    // ──────────────────────────────────────────────

    [Fact]
    public void GetSnapshot_returns_correct_counts_and_rates()
    {
        var tracker = CreateTracker();

        // 3 successes
        for (int i = 0; i < 3; i++)
        {
            var h = tracker.TrackSessionStart("ws", $"s-{i}");
            h.Complete(SessionOutcome.Success);
            h.Dispose();
        }

        // 1 failure
        var hFail = tracker.TrackSessionStart("ws", "s-fail");
        hFail.Complete(SessionOutcome.Failure);
        hFail.Dispose();

        // 1 timeout
        var hTimeout = tracker.TrackSessionStart("ws", "s-timeout");
        hTimeout.Complete(SessionOutcome.Timeout);
        hTimeout.Dispose();

        var snapshot = tracker.GetSnapshot();

        snapshot.ActiveSessions.Should().Be(0);
        snapshot.RecentSuccesses.Should().Be(3);
        snapshot.RecentFailures.Should().Be(2); // failure + timeout
        snapshot.SuccessRate.Should().BeApproximately(0.6, 0.001); // 3/5
        snapshot.SnapshotAt.Should().Be(TestFixtures.BaseTime);
    }

    [Fact]
    public void GetSnapshot_empty_state_returns_zero_snapshot()
    {
        var tracker = CreateTracker();

        var snapshot = tracker.GetSnapshot();

        snapshot.ActiveSessions.Should().Be(0);
        snapshot.RecentSuccesses.Should().Be(0);
        snapshot.RecentFailures.Should().Be(0);
        snapshot.SuccessRate.Should().Be(1.0, "no completed sessions means 100% success by default");
        snapshot.SnapshotAt.Should().Be(TestFixtures.BaseTime);
    }

    [Fact]
    public void GetSnapshot_sliding_window_evicts_old_completions()
    {
        var window = TimeSpan.FromMinutes(5);
        var tracker = CreateTracker(window);

        // Record a success at T=0
        var h1 = tracker.TrackSessionStart("ws", "s-1");
        h1.Complete(SessionOutcome.Success);
        h1.Dispose();

        // Advance time past the window
        _timeProvider.Advance(TimeSpan.FromMinutes(6));

        // Record a failure at T=6min
        var h2 = tracker.TrackSessionStart("ws", "s-2");
        h2.Complete(SessionOutcome.Failure);
        h2.Dispose();

        var snapshot = tracker.GetSnapshot();

        snapshot.RecentSuccesses.Should().Be(0, "the success is outside the window");
        snapshot.RecentFailures.Should().Be(1);
        snapshot.SuccessRate.Should().Be(0.0);
    }

    [Fact]
    public void GetSnapshot_includes_active_sessions_in_count()
    {
        var tracker = CreateTracker();

        var handle1 = tracker.TrackSessionStart("ws", "s-1");
        var handle2 = tracker.TrackSessionStart("ws", "s-2");

        var snapshot = tracker.GetSnapshot();
        snapshot.ActiveSessions.Should().Be(2);

        handle1.Dispose();
        handle2.Dispose();
    }

    // ──────────────────────────────────────────────
    //  RecordEvent
    // ──────────────────────────────────────────────

    [Fact]
    public void RecordEvent_does_not_throw_and_can_be_called_multiple_times()
    {
        var tracker = CreateTracker();
        using var handle = tracker.TrackSessionStart("ws", "s-1");

        var act = () =>
        {
            handle.RecordEvent("message-received");
            handle.RecordEvent("error-retry");
            handle.RecordEvent("message-sent");
        };

        act.Should().NotThrow();
    }

    // ──────────────────────────────────────────────
    //  Thread safety
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Thread_safety_50_concurrent_session_start_complete()
    {
        var tracker = CreateTracker();
        const int concurrentSessions = 50;

        var tasks = Enumerable.Range(0, concurrentSessions).Select(i => Task.Run(() =>
        {
            var handle = tracker.TrackSessionStart("ws", $"session-{i}");
            handle.RecordEvent("ping");
            handle.Complete(i % 3 == 0 ? SessionOutcome.Failure : SessionOutcome.Success);
            handle.Dispose();
        })).ToArray();

        await Task.WhenAll(tasks);

        tracker.ActiveSessionCount.Should().Be(0, "all sessions completed");

        var snapshot = tracker.GetSnapshot();
        int expectedFailures = Enumerable.Range(0, concurrentSessions).Count(i => i % 3 == 0);
        int expectedSuccesses = concurrentSessions - expectedFailures;

        snapshot.RecentSuccesses.Should().Be(expectedSuccesses);
        snapshot.RecentFailures.Should().Be(expectedFailures);
        (snapshot.RecentSuccesses + snapshot.RecentFailures).Should().Be(concurrentSessions);
    }

    [Fact]
    public async Task Thread_safety_concurrent_start_dispose_without_complete()
    {
        var tracker = CreateTracker();
        const int concurrentSessions = 100;

        var tasks = Enumerable.Range(0, concurrentSessions).Select(i => Task.Run(() =>
        {
            using var handle = tracker.TrackSessionStart("ws", $"session-{i}");
            handle.RecordEvent("event-1");
            // No Complete() — all should be Cancelled
        })).ToArray();

        await Task.WhenAll(tasks);

        tracker.ActiveSessionCount.Should().Be(0, "all sessions disposed");

        var snapshot = tracker.GetSnapshot();
        snapshot.RecentFailures.Should().Be(concurrentSessions, "all sessions were cancelled");
        snapshot.RecentSuccesses.Should().Be(0);
    }

    [Fact]
    public async Task Thread_safety_mixed_active_and_completed_sessions()
    {
        var tracker = CreateTracker();
        const int totalSessions = 80;
        var handles = new ISessionHandle[totalSessions / 2];

        // Start some sessions that will remain active
        for (int i = 0; i < handles.Length; i++)
        {
            handles[i] = tracker.TrackSessionStart("ws", $"active-{i}");
        }

        // Complete other sessions concurrently
        var completeTasks = Enumerable.Range(0, totalSessions / 2).Select(i => Task.Run(() =>
        {
            var h = tracker.TrackSessionStart("ws", $"completed-{i}");
            h.Complete(SessionOutcome.Success);
            h.Dispose();
        })).ToArray();

        await Task.WhenAll(completeTasks);

        tracker.ActiveSessionCount.Should().Be(totalSessions / 2, "half remain active");

        // Clean up active handles
        foreach (var h in handles)
        {
            h.Dispose();
        }

        tracker.ActiveSessionCount.Should().Be(0);
    }

    // ──────────────────────────────────────────────
    //  Constructor validation
    // ──────────────────────────────────────────────

    [Fact]
    public void Constructor_null_clock_throws_ArgumentNullException()
    {
        var act = () => new SessionHealthTracker(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("clock");
    }

    [Fact]
    public void Constructor_zero_sliding_window_throws_ArgumentOutOfRangeException()
    {
        var act = () => new SessionHealthTracker(_clock, TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("slidingWindow");
    }

    [Fact]
    public void Constructor_negative_sliding_window_throws_ArgumentOutOfRangeException()
    {
        var act = () => new SessionHealthTracker(_clock, TimeSpan.FromMinutes(-1));

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("slidingWindow");
    }

    [Fact]
    public void TrackSessionStart_null_sessionType_throws_ArgumentNullException()
    {
        var tracker = CreateTracker();

        var act = () => tracker.TrackSessionStart(null!, "session-1");

        act.Should().Throw<ArgumentNullException>().WithParameterName("sessionType");
    }

    [Fact]
    public void TrackSessionStart_null_sessionId_throws_ArgumentNullException()
    {
        var tracker = CreateTracker();

        var act = () => tracker.TrackSessionStart("ws", null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("sessionId");
    }

    [Fact]
    public void RecordEvent_null_eventName_throws_ArgumentNullException()
    {
        var tracker = CreateTracker();
        using var handle = tracker.TrackSessionStart("ws", "s-1");

        var act = () => handle.RecordEvent(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("eventName");
    }

    // ──────────────────────────────────────────────
    //  All SessionOutcome variants
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData(SessionOutcome.Success, 1, 0)]
    [InlineData(SessionOutcome.Failure, 0, 1)]
    [InlineData(SessionOutcome.Timeout, 0, 1)]
    [InlineData(SessionOutcome.Cancelled, 0, 1)]
    public void Complete_with_each_outcome_records_correctly(
        SessionOutcome outcome, int expectedSuccesses, int expectedFailures)
    {
        var tracker = CreateTracker();
        var handle = tracker.TrackSessionStart("ws", "s-1");
        handle.Complete(outcome);
        handle.Dispose();

        var snapshot = tracker.GetSnapshot();

        snapshot.RecentSuccesses.Should().Be(expectedSuccesses);
        snapshot.RecentFailures.Should().Be(expectedFailures);
    }
}

// <copyright file="SessionHealthTracker.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Components;

/// <summary>
/// Thread-safe tracker for active WebSocket/long-lived session counts and outcomes.
/// Uses <see cref="Interlocked"/> for the active session gauge and a
/// <see cref="ConcurrentQueue{T}"/> sliding window for recent outcome tracking.
/// </summary>
internal sealed class SessionHealthTracker : ISessionHealthTracker
{
    private readonly ISystemClock _clock;
    private readonly TimeSpan _slidingWindow;
    private readonly ISessionMetrics _metrics;
    private readonly ConcurrentQueue<SessionCompletionRecord> _completions = new();
    private int _activeCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionHealthTracker"/> class.
    /// </summary>
    /// <param name="clock">The system clock for time-based window calculations.</param>
    /// <param name="slidingWindow">
    /// The duration of the sliding window for success/failure tracking.
    /// Defaults to 5 minutes when <c>null</c>.
    /// </param>
    /// <param name="metrics">Optional metrics recorder for session tracking.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="clock"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="slidingWindow"/> is not positive.</exception>
    public SessionHealthTracker(ISystemClock clock, TimeSpan? slidingWindow = null, ISessionMetrics? metrics = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        var window = slidingWindow ?? TimeSpan.FromMinutes(5);
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slidingWindow),
                slidingWindow,
                "Sliding window must be a positive duration.");
        }

        _slidingWindow = window;
        _metrics = metrics ?? NullHealthBossMetrics.Instance;
    }

    /// <inheritdoc />
    public int ActiveSessionCount => Volatile.Read(ref _activeCount);

    /// <inheritdoc />
    public ISessionHandle TrackSessionStart(string sessionType, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionType);
        ArgumentNullException.ThrowIfNull(sessionId);

        Interlocked.Increment(ref _activeCount);
        _metrics.SetActiveSessionCount(Volatile.Read(ref _activeCount));
        return new SessionHandle(this, sessionType, sessionId);
    }

    /// <inheritdoc />
    public SessionHealthSnapshot GetSnapshot()
    {
        var now = _clock.UtcNow;
        var cutoff = now - _slidingWindow;

        EvictExpired(cutoff);

        int successes = 0;
        int failures = 0;

        foreach (var record in _completions)
        {
            if (record.CompletedAt >= cutoff)
            {
                if (record.Outcome == SessionOutcome.Success)
                {
                    successes++;
                }
                else
                {
                    failures++;
                }
            }
        }

        int total = successes + failures;
        double successRate = total == 0 ? 1.0 : (double)successes / total;

        return new SessionHealthSnapshot(
            ActiveSessions: Volatile.Read(ref _activeCount),
            RecentSuccesses: successes,
            RecentFailures: failures,
            SuccessRate: successRate,
            SnapshotAt: now);
    }

    /// <summary>
    /// Records a session completion with the specified outcome. Called by <see cref="SessionHandle"/>.
    /// </summary>
    internal void RecordCompletion(SessionOutcome outcome)
    {
        Interlocked.Decrement(ref _activeCount);
        _metrics.SetActiveSessionCount(Volatile.Read(ref _activeCount));
        _completions.Enqueue(new SessionCompletionRecord(outcome, _clock.UtcNow));
    }

    /// <summary>
    /// Evicts completion records older than the specified cutoff from the sliding window.
    /// </summary>
    private void EvictExpired(DateTimeOffset cutoff)
    {
        while (_completions.TryPeek(out var oldest) && oldest.CompletedAt < cutoff)
        {
            _completions.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Internal record for tracking session completion outcomes in the sliding window.
    /// </summary>
    private sealed record SessionCompletionRecord(SessionOutcome Outcome, DateTimeOffset CompletedAt);

    /// <summary>
    /// Represents an active session. Disposing the handle ends the session.
    /// If <see cref="Complete"/> was not called, the outcome is <see cref="SessionOutcome.Cancelled"/>.
    /// </summary>
    private sealed class SessionHandle : ISessionHandle
    {
        private readonly SessionHealthTracker _tracker;
        private readonly ConcurrentBag<string> _events = new();
        private int _completed; // 0 = not completed, 1 = completed

        /// <summary>
        /// Initializes a new instance of the <see cref="SessionHandle"/> class.
        /// </summary>
        public SessionHandle(SessionHealthTracker tracker, string sessionType, string sessionId)
        {
            _tracker = tracker;
            SessionType = sessionType;
            SessionId = sessionId;
        }

        /// <summary>Gets the session type.</summary>
        public string SessionType { get; }

        /// <summary>Gets the session identifier.</summary>
        public string SessionId { get; }

        /// <inheritdoc />
        public void RecordEvent(string eventName)
        {
            ArgumentNullException.ThrowIfNull(eventName);
            _events.Add(eventName);
        }

        /// <inheritdoc />
        public void Complete(SessionOutcome outcome)
        {
            if (Interlocked.CompareExchange(ref _completed, 1, 0) == 0)
            {
                _tracker.RecordCompletion(outcome);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // If not explicitly completed, record as Cancelled
            Complete(SessionOutcome.Cancelled);
        }
    }
}

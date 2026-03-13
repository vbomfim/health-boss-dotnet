// <copyright file="EventSinkDispatcher.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HealthBoss.Core.Components;

/// <summary>
/// Fan-out dispatcher that sends health events to all registered
/// <see cref="IHealthEventSink"/> implementations with error isolation,
/// rate limiting, and per-sink timeouts.
/// <para>
/// <strong>Trust model:</strong> Sinks are privileged, in-process code that
/// receives validated, non-PII event data. This dispatcher adds defensive
/// layers to prevent a misbehaving sink from impacting health evaluation:
/// </para>
/// <list type="bullet">
///   <item>Each sink call is wrapped in try-catch — one failure never blocks others.</item>
///   <item>Per-sink rate limiting prevents event storm amplification (Security Finding #12).</item>
///   <item>Configurable timeout prevents slow sinks from blocking dispatch.</item>
/// </list>
/// </summary>
internal sealed class EventSinkDispatcher : IEventSinkDispatcher, IHealthEventSink
{
    private readonly IReadOnlyList<IHealthEventSink> _sinks;
    private readonly SinkRateLimiter[] _rateLimiters;
    private readonly ISystemClock _clock;
    private readonly ILogger<EventSinkDispatcher> _logger;
    private readonly EventSinkDispatcherOptions _options;
    private readonly IStateMachineMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventSinkDispatcher"/> class.
    /// </summary>
    /// <param name="sinks">The event sinks to dispatch to.</param>
    /// <param name="options">Dispatcher configuration (rate limit, timeout).</param>
    /// <param name="clock">System clock for rate limiter window tracking.</param>
    /// <param name="logger">Optional logger for diagnostic messages.</param>
    /// <param name="metrics">Optional metrics recorder for dispatch tracking.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="sinks"/>, <paramref name="options"/>,
    /// or <paramref name="clock"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <see cref="EventSinkDispatcherOptions.MaxEventsPerSecondPerSink"/> is less than 1.
    /// </exception>
    internal EventSinkDispatcher(
        IReadOnlyList<IHealthEventSink> sinks,
        EventSinkDispatcherOptions options,
        ISystemClock clock,
        ILogger<EventSinkDispatcher>? logger = null,
        IStateMachineMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        if (options.MaxEventsPerSecondPerSink < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxEventsPerSecondPerSink,
                "MaxEventsPerSecondPerSink must be at least 1.");
        }

        _sinks = sinks;
        _options = options;
        _clock = clock;
        _logger = logger ?? NullLogger<EventSinkDispatcher>.Instance;
        _metrics = metrics ?? NullHealthBossMetrics.Instance;
        _rateLimiters = new SinkRateLimiter[sinks.Count];

        for (int i = 0; i < sinks.Count; i++)
        {
            _rateLimiters[i] = new SinkRateLimiter(options.MaxEventsPerSecondPerSink);
        }
    }

    /// <inheritdoc />
    public Task OnHealthStateChanged(HealthEvent healthEvent, CancellationToken ct = default)
        => DispatchAsync(healthEvent, ct);

    /// <inheritdoc />
    public Task OnTenantHealthChanged(TenantHealthEvent tenantEvent, CancellationToken ct = default)
        => DispatchTenantEventAsync(tenantEvent, ct);

    /// <inheritdoc />
    public Task DispatchAsync(HealthEvent healthEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(healthEvent);
        return DispatchCoreAsync(sink => sink.OnHealthStateChanged(healthEvent, ct), ct);
    }

    /// <inheritdoc />
    public Task DispatchTenantEventAsync(TenantHealthEvent tenantEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenantEvent);
        return DispatchCoreAsync(sink => sink.OnTenantHealthChanged(tenantEvent, ct), ct);
    }

    /// <summary>
    /// Shared dispatch loop — acquires rate-limit tokens, fans out to all sinks
    /// with error isolation, and awaits completion.
    /// </summary>
    private async Task DispatchCoreAsync(Func<IHealthEventSink, Task> action, CancellationToken ct)
    {
        if (_sinks.Count == 0)
        {
            return;
        }

        var nowTicks = _clock.UtcNow.UtcTicks;
        var tasks = new List<Task>(_sinks.Count);

        for (int i = 0; i < _sinks.Count; i++)
        {
            if (!_rateLimiters[i].TryAcquire(nowTicks))
            {
                LogRateLimitExceeded(i);
                continue;
            }

            int sinkIndex = i;
            tasks.Add(InvokeSinkSafelyAsync(sinkIndex, action, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        _metrics.RecordEventSinkDispatch();
    }

    /// <summary>
    /// Invokes a sink operation with timeout and error isolation.
    /// Catches all exceptions from the sink and logs at Warning level.
    /// Caller cancellation (via <paramref name="ct"/>) is propagated.
    /// </summary>
    private async Task InvokeSinkSafelyAsync(
        int index,
        Func<IHealthEventSink, Task> action,
        CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.EffectiveSinkTimeout);
            await action(_sinks[index]).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout (not caller cancellation) — log and continue
            _logger.LogWarning(
                "Sink {SinkIndex} ({SinkType}) timed out after {Timeout}",
                index,
                _sinks[index].GetType().Name,
                _options.EffectiveSinkTimeout);
            _metrics.RecordEventSinkFailure(_sinks[index].GetType().Name);
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation — propagate
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Sink {SinkIndex} ({SinkType}) failed",
                index,
                _sinks[index].GetType().Name);
            _metrics.RecordEventSinkFailure(_sinks[index].GetType().Name);
        }
    }

    private void LogRateLimitExceeded(int index)
    {
        _logger.LogWarning(
            "Rate limit exceeded for sink {SinkIndex} ({SinkType}), dropping event",
            index,
            _sinks[index].GetType().Name);
    }

    /// <summary>
    /// Per-sink fixed-window rate limiter.
    /// Uses a 1-second fixed window with a counter that resets on window expiry.
    /// Thread-safe via <c>lock</c> for short critical sections.
    /// </summary>
    private sealed class SinkRateLimiter
    {
        private readonly int _maxPerSecond;
        private readonly object _lock = new();
        private long _windowStartTicks;
        private int _count;

        /// <summary>
        /// Initializes a new instance of the <see cref="SinkRateLimiter"/> class.
        /// </summary>
        /// <param name="maxPerSecond">Maximum events allowed per 1-second window.</param>
        internal SinkRateLimiter(int maxPerSecond)
        {
            _maxPerSecond = maxPerSecond;
        }

        /// <summary>
        /// Attempts to acquire a rate limit token.
        /// </summary>
        /// <param name="nowTicks">Current UTC ticks from the system clock.</param>
        /// <returns><c>true</c> if the event is within the rate limit; <c>false</c> if it should be dropped.</returns>
        internal bool TryAcquire(long nowTicks)
        {
            lock (_lock)
            {
                if (nowTicks - _windowStartTicks >= TimeSpan.TicksPerSecond)
                {
                    _windowStartTicks = nowTicks;
                    _count = 1;
                    return true;
                }

                if (_count < _maxPerSecond)
                {
                    _count++;
                    return true;
                }

                return false;
            }
        }
    }
}

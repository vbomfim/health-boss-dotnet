// <copyright file="DrainCoordinator.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;
using Microsoft.Extensions.Logging;

namespace HealthBoss.Core.Components;

/// <summary>
/// Coordinates graceful session drain during shutdown.
/// <para>
/// Polls <c>getActiveSessionCount</c> every 500 ms. The drain completes when
/// the active session count reaches zero (<see cref="DrainStatus.Drained"/>)
/// or the configured timeout is exceeded (<see cref="DrainStatus.TimedOut"/>).
/// </para>
/// <para>
/// Thread-safe: concurrent calls to <see cref="DrainAsync"/> share the same
/// drain operation — the second caller awaits the result of the first.
/// </para>
/// </summary>
internal sealed class DrainCoordinator : IDrainCoordinator, IDisposable
{
    /// <summary>
    /// Interval between active-session-count polls during drain.
    /// </summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly ISystemClock _clock;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DrainCoordinator> _logger;
    private readonly ISessionMetrics _metrics;

    private volatile DrainStatus _status = DrainStatus.Idle;

    /// <summary>
    /// Guards concurrent <see cref="DrainAsync"/> calls so only one drain
    /// loop executes; subsequent callers piggyback on the active task.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Holds the active drain task when a drain is in progress.
    /// Protected by <see cref="_gate"/>.
    /// </summary>
    private Task<DrainStatus>? _activeDrain;

    /// <summary>
    /// Initializes a new instance of the <see cref="DrainCoordinator"/> class.
    /// </summary>
    /// <param name="clock">Clock abstraction for timeout evaluation.</param>
    /// <param name="logger">Logger for drain lifecycle events.</param>
    /// <param name="timeProvider">Time provider for testable delays.</param>
    /// <param name="metrics">Optional metrics recorder for drain status tracking.</param>
    public DrainCoordinator(
        ISystemClock clock,
        ILogger<DrainCoordinator> logger,
        TimeProvider? timeProvider = null,
        ISessionMetrics? metrics = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _metrics = metrics ?? NullHealthBossMetrics.Instance;
    }

    /// <inheritdoc />
    public DrainStatus Status => _status;

    /// <inheritdoc />
    public async Task<DrainStatus> DrainAsync(
        Func<int> getActiveSessionCount,
        DrainConfig config,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(getActiveSessionCount);
        ArgumentNullException.ThrowIfNull(config);

        // Fast path: if a drain is already in progress, piggyback on it.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        bool released = false;
        try
        {
            if (_activeDrain is not null)
            {
                var existingTask = _activeDrain;
                _gate.Release();
                released = true;
                return await existingTask.ConfigureAwait(false);
            }

            _status = DrainStatus.Draining;
            _metrics.SetDrainStatus(DrainStatus.Draining);
            _activeDrain = ExecuteDrainLoopAsync(getActiveSessionCount, config, ct);
        }
        finally
        {
            if (!released)
            {
                _gate.Release();
            }
        }

        return await _activeDrain.ConfigureAwait(false);
    }

    /// <summary>
    /// Core drain loop: poll session count every <see cref="PollInterval"/> until
    /// count reaches zero or timeout is exceeded.
    /// </summary>
    private async Task<DrainStatus> ExecuteDrainLoopAsync(
        Func<int> getActiveSessionCount,
        DrainConfig config,
        CancellationToken ct)
    {
        var deadline = _clock.UtcNow + config.Timeout;

        _logger.LogInformation(
            "Drain started — timeout: {Timeout}, deadline: {Deadline}",
            config.Timeout,
            deadline);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var activeCount = getActiveSessionCount();

            if (activeCount <= 0)
            {
                _status = DrainStatus.Drained;
                _metrics.SetDrainStatus(DrainStatus.Drained);
                _logger.LogInformation("Drain completed — all sessions drained");
                return DrainStatus.Drained;
            }

            if (_clock.UtcNow >= deadline)
            {
                _status = DrainStatus.TimedOut;
                _metrics.SetDrainStatus(DrainStatus.TimedOut);
                _logger.LogWarning(
                    "Drain timed out — {ActiveCount} sessions still active after {Timeout}",
                    activeCount,
                    config.Timeout);
                return DrainStatus.TimedOut;
            }

            // Invoke custom drain delegate if configured.
            if (config.DrainDelegate is not null)
            {
                await InvokeDrainDelegateAsync(config.DrainDelegate, activeCount, ct)
                    .ConfigureAwait(false);
            }

            // Wait for next poll cycle using the injected TimeProvider (testable).
            await Task.Delay(PollInterval, _timeProvider, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Invokes the custom drain delegate, swallowing and logging any exceptions
    /// to prevent a faulty delegate from breaking the drain loop.
    /// </summary>
    private async Task InvokeDrainDelegateAsync(
        Func<int, CancellationToken, Task<bool>> drainDelegate,
        int activeCount,
        CancellationToken ct)
    {
        try
        {
            await drainDelegate(activeCount, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-initiated cancellation — let it propagate.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Drain delegate threw an exception (active sessions: {ActiveCount})",
                activeCount);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
    }
}

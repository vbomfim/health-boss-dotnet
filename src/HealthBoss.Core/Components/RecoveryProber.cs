// <copyright file="RecoveryProber.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HealthBoss.Core.Components;

/// <summary>
/// Periodically probes dependencies in <see cref="HealthState.CircuitOpen"/> state
/// to detect recovery. Runs an independent background loop per dependency, calling
/// <see cref="IRecoveryProbeHandler.ProbeAsync"/> at the configured interval and
/// recording the result as a <see cref="HealthSignal"/>.
/// </summary>
internal sealed class RecoveryProber : IRecoveryProber, IDisposable
{
    private readonly IRecoveryProbeHandler _handler;
    private readonly ISignalRecorder _recorder;
    private readonly ISystemClock _clock;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RecoveryProber> _logger;
    private readonly IStateMachineMetrics _metrics;
    private readonly ConcurrentDictionary<DependencyId, ProbingEntry> _entries = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RecoveryProber"/> class.
    /// </summary>
    /// <param name="handler">User-provided probe logic.</param>
    /// <param name="recorder">Signal recorder to capture probe results.</param>
    /// <param name="clock">System clock for signal timestamps.</param>
    /// <param name="timeProvider">Time provider for testable delays.</param>
    /// <param name="logger">Optional logger for probe diagnostics.</param>
    /// <param name="metrics">Optional metrics recorder for probe tracking.</param>
    public RecoveryProber(
        IRecoveryProbeHandler handler,
        ISignalRecorder recorder,
        ISystemClock clock,
        TimeProvider timeProvider,
        ILogger<RecoveryProber>? logger = null,
        IStateMachineMetrics? metrics = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger<RecoveryProber>.Instance;
        _metrics = metrics ?? NullHealthBossMetrics.Instance;
    }

    /// <inheritdoc />
    public Task StartProbingAsync(DependencyId id, HealthPolicy policy, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var entry = new ProbingEntry(cts);

        if (!_entries.TryAdd(id, entry))
        {
            // Already probing — dispose our CTS and return.
            cts.Dispose();
            return Task.CompletedTask;
        }

        // Fire-and-forget the background loop. The loop will self-clean on cancellation.
        entry.Task = Task.Run(() => ProbeLoopAsync(id, policy.RecoveryProbeInterval, cts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void StopProbing(DependencyId id)
    {
        if (_entries.TryRemove(id, out var entry))
        {
            entry.Cts.Cancel();
            entry.Cts.Dispose();
        }
    }

    /// <inheritdoc />
    public bool IsProbing(DependencyId id) => _entries.ContainsKey(id);

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose()
    {
        foreach (var key in _entries.Keys.ToList())
        {
            StopProbing(key);
        }
    }

    /// <summary>
    /// Background loop that probes at the configured interval until cancelled.
    /// </summary>
    private async Task ProbeLoopAsync(DependencyId id, TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, _timeProvider, ct).ConfigureAwait(false);

                SignalOutcome outcome;
                try
                {
                    var recovered = await _handler.ProbeAsync(id, ct).ConfigureAwait(false);
                    outcome = recovered ? SignalOutcome.Success : SignalOutcome.Failure;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    outcome = SignalOutcome.Failure;
                    _logger.LogWarning(ex,
                        "Recovery probe for {Component} threw exception",
                        id.Value);
                }

                _metrics.RecordRecoveryProbeAttempt(id.Value);
                if (outcome == SignalOutcome.Success)
                {
                    _metrics.RecordRecoveryProbeSuccess(id.Value);
                }

                _logger.LogInformation(
                    "Recovery probe for {Component}: {Outcome}",
                    id.Value,
                    outcome);

                if (!ct.IsCancellationRequested)
                {
                    var signal = new HealthSignal(
                        timestamp: _clock.UtcNow,
                        dependencyId: id,
                        outcome: outcome);

                    _recorder.Record(signal);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected on cancellation — exit gracefully.
        }
        finally
        {
            // Clean up entry if the loop exits naturally (e.g., external CancellationToken).
            _entries.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Tracks the CancellationTokenSource and background task for a probing entry.
    /// </summary>
    private sealed class ProbingEntry
    {
        public ProbingEntry(CancellationTokenSource cts) => Cts = cts;

        /// <summary>Gets the cancellation token source for this probing loop.</summary>
        public CancellationTokenSource Cts { get; }

        /// <summary>Gets or sets the background task running the probe loop.</summary>
        public Task? Task { get; set; }
    }
}

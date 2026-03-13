// <copyright file="ShutdownOrchestrator.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;
using Microsoft.Extensions.Logging;

namespace HealthBoss.Core.Components;

/// <summary>
/// Enforces the 3-gate shutdown safety chain before approving a shutdown signal.
/// <para>
/// Gate 1 — <b>MinSignals</b>: <c>stateReader.TotalSignalCount ≥ config.MinSignals</c>.<br/>
/// Gate 2 — <b>Cooldown</b>: <c>(clock.UtcNow − lastTransition) ≥ config.Cooldown</c>.<br/>
/// Gate 3 — <b>ConfirmDelegate</b>: optional async delegate wrapped in a 5-second timeout.
/// </para>
/// <para>
/// All evaluated gates must pass. Stateless evaluation — inherently thread-safe.
/// </para>
/// </summary>
internal sealed class ShutdownOrchestrator : IShutdownOrchestrator
{
    /// <summary>
    /// Maximum time the confirm delegate is allowed to execute before timeout.
    /// Security Finding #7: prevent slow delegates from hanging the shutdown path.
    /// </summary>
    internal static readonly TimeSpan ConfirmDelegateTimeout = TimeSpan.FromSeconds(5);

    private readonly ShutdownConfig _config;
    private readonly ISystemClock _clock;
    private readonly ILogger<ShutdownOrchestrator> _logger;
    private readonly Func<IReadOnlyCollection<DependencySnapshot>, CancellationToken, Task<bool>>? _confirmDelegate;
    private readonly IStateMachineMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShutdownOrchestrator"/> class.
    /// </summary>
    /// <param name="config">The 3-gate shutdown configuration.</param>
    /// <param name="clock">Clock abstraction for cooldown evaluation.</param>
    /// <param name="logger">Logger for CRITICAL shutdown audit events.</param>
    /// <param name="confirmDelegate">
    /// Optional async delegate for Gate 3. Required when
    /// <see cref="ShutdownConfig.RequireConfirmDelegate"/> is <c>true</c>.
    /// </param>
    /// <param name="metrics">Optional metrics recorder for shutdown gate evaluations.</param>
    public ShutdownOrchestrator(
        ShutdownConfig config,
        ISystemClock clock,
        ILogger<ShutdownOrchestrator> logger,
        Func<IReadOnlyCollection<DependencySnapshot>, CancellationToken, Task<bool>>? confirmDelegate = null,
        IStateMachineMetrics? metrics = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _confirmDelegate = confirmDelegate;
        _metrics = metrics ?? NullHealthBossMetrics.Instance;
    }

    /// <inheritdoc />
    public ShutdownDecision Evaluate(IHealthStateReader stateReader)
    {
        ArgumentNullException.ThrowIfNull(stateReader);

        var decision = EvaluateSyncGates(stateReader);
        if (!decision.Approved)
        {
            LogDecision(decision);
            return decision;
        }

        // Gate 3: ConfirmDelegate — cannot be invoked synchronously.
        if (_config.RequireConfirmDelegate)
        {
            decision = new ShutdownDecision(
                Approved: false,
                Gate: "ConfirmDelegate",
                Reason: "Confirm delegate required — use RequestShutdownAsync for full evaluation");
            LogDecision(decision);
            return decision;
        }

        LogDecision(decision);
        return decision;
    }

    /// <inheritdoc />
    public async Task<ShutdownDecision> RequestShutdownAsync(
        IHealthStateReader stateReader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stateReader);

        var decision = EvaluateSyncGates(stateReader);
        if (!decision.Approved)
        {
            LogDecision(decision);
            return decision;
        }

        // Gate 3: ConfirmDelegate
        if (_config.RequireConfirmDelegate)
        {
            decision = await EvaluateConfirmDelegateAsync(stateReader, cancellationToken)
                .ConfigureAwait(false);
            LogDecision(decision);
            return decision;
        }

        LogDecision(decision);
        return decision;
    }

    /// <summary>
    /// Evaluates Gate 1 (MinSignals) and Gate 2 (Cooldown) synchronously.
    /// Returns an approved decision if both pass.
    /// </summary>
    private ShutdownDecision EvaluateSyncGates(IHealthStateReader stateReader)
    {
        // Gate 1: MinSignals
        if (stateReader.TotalSignalCount < _config.MinSignals)
        {
            return new ShutdownDecision(
                Approved: false,
                Gate: "MinSignals",
                Reason: $"Insufficient signals: {stateReader.TotalSignalCount} < {_config.MinSignals} (MinSignals gate)");
        }

        // Gate 2: Cooldown
        if (stateReader.LastTransitionTime is { } lastTransition)
        {
            var elapsed = _clock.UtcNow - lastTransition;
            if (elapsed < _config.Cooldown)
            {
                return new ShutdownDecision(
                    Approved: false,
                    Gate: "Cooldown",
                    Reason: $"Cooldown not elapsed: {elapsed.TotalSeconds:F1}s < {_config.Cooldown.TotalSeconds:F1}s (Cooldown gate)");
            }
        }

        // No transition → no cooldown to enforce → gate passes.

        return new ShutdownDecision(
            Approved: true,
            Gate: "All",
            Reason: "All gates passed — shutdown approved");
    }

    /// <summary>
    /// Evaluates Gate 3 (ConfirmDelegate) asynchronously with a 5-second timeout.
    /// </summary>
    private async Task<ShutdownDecision> EvaluateConfirmDelegateAsync(
        IHealthStateReader stateReader,
        CancellationToken cancellationToken)
    {
        if (_confirmDelegate is null)
        {
            return new ShutdownDecision(
                Approved: false,
                Gate: "ConfirmDelegate",
                Reason: "Confirm delegate required but not provided");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ConfirmDelegateTimeout);

            var snapshots = stateReader.GetAllSnapshots();
            var confirmed = await _confirmDelegate(snapshots, cts.Token).ConfigureAwait(false);

            if (!confirmed)
            {
                return new ShutdownDecision(
                    Approved: false,
                    Gate: "ConfirmDelegate",
                    Reason: "Confirm delegate denied shutdown");
            }

            return new ShutdownDecision(
                Approved: true,
                Gate: "All",
                Reason: "All gates passed — shutdown approved");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ShutdownDecision(
                Approved: false,
                Gate: "ConfirmDelegate",
                Reason: $"Confirm delegate timed out after {ConfirmDelegateTimeout.TotalSeconds}s");
        }
        catch (OperationCanceledException)
        {
            // Caller-initiated cancellation — still deny shutdown.
            return new ShutdownDecision(
                Approved: false,
                Gate: "ConfirmDelegate",
                Reason: "Shutdown evaluation cancelled");
        }
        catch (Exception ex)
        {
#pragma warning disable CA2254 // Template should be a static expression
            _logger.LogError(ex, "Confirm delegate threw an exception during shutdown evaluation");
#pragma warning restore CA2254
            return new ShutdownDecision(
                Approved: false,
                Gate: "ConfirmDelegate",
                Reason: $"Confirm delegate threw exception: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Logs every shutdown decision at CRITICAL level for audit purposes.
    /// </summary>
    private void LogDecision(ShutdownDecision decision)
    {
        _metrics.RecordShutdownGateEvaluation(decision.Gate, decision.Approved);

        if (decision.Approved)
        {
            _logger.LogCritical(
                "Shutdown APPROVED — Gate: {Gate}, Reason: {Reason}",
                decision.Gate,
                decision.Reason);
        }
        else
        {
            _logger.LogCritical(
                "Shutdown DENIED — Gate: {Gate}, Reason: {Reason}",
                decision.Gate,
                decision.Reason);
        }
    }
}

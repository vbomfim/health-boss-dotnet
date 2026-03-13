// <copyright file="HealthOrchestrator.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using System.Collections.Frozen;
using System.Diagnostics;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.Logging;

namespace HealthBoss.Core.Components;

/// <summary>
/// Top-level coordinator that owns all <see cref="IDependencyMonitor"/> instances.
/// Produces aggregate <see cref="HealthReport"/> and <see cref="ReadinessReport"/>.
/// User-supplied aggregate delegates are wrapped with try-catch and 5 s timeout
/// (Security Finding #7) to prevent malicious or buggy delegates from hanging the system.
/// </summary>
internal sealed class HealthOrchestrator : IHealthOrchestrator
{
    /// <summary>
    /// Maximum time allowed for a user-supplied aggregate delegate to execute.
    /// </summary>
    internal static readonly TimeSpan DelegateTimeout = TimeSpan.FromSeconds(5);

    private readonly FrozenDictionary<DependencyId, IDependencyMonitor> _monitors;
    private readonly IReadOnlyCollection<DependencyId> _registeredDependencies;
    private readonly Func<IReadOnlyList<DependencySnapshot>, HealthStatus>? _healthResolver;
    private readonly Func<IReadOnlyList<DependencySnapshot>, ReadinessStatus>? _readinessResolver;
    private readonly IStartupTracker _startupTracker;
    private readonly ISystemClock _clock;
    private readonly ILogger<HealthOrchestrator> _logger;
    private readonly IComponentMetrics _metrics;

    /// <summary>
    /// Cached snapshots from the last <see cref="CollectSnapshots"/> call.
    /// Lightweight properties read from this cache to avoid triggering state transitions.
    /// </summary>
    private volatile IReadOnlyList<DependencySnapshot>? _cachedSnapshots;

    /// <summary>
    /// Initializes a new instance of the <see cref="HealthOrchestrator"/> class.
    /// </summary>
    /// <param name="monitors">The per-dependency monitors, keyed by dependency ID.</param>
    /// <param name="healthResolver">Optional custom aggregate health resolver delegate.</param>
    /// <param name="readinessResolver">Optional custom aggregate readiness resolver delegate.</param>
    /// <param name="startupTracker">The startup lifecycle tracker.</param>
    /// <param name="clock">The system clock for timestamps.</param>
    /// <param name="logger">Logger for dropped-signal warnings and diagnostics.</param>
    /// <param name="metrics">Optional metrics recorder for health signals and assessments.</param>
    public HealthOrchestrator(
        IReadOnlyDictionary<DependencyId, IDependencyMonitor> monitors,
        Func<IReadOnlyList<DependencySnapshot>, HealthStatus>? healthResolver,
        Func<IReadOnlyList<DependencySnapshot>, ReadinessStatus>? readinessResolver,
        IStartupTracker startupTracker,
        ISystemClock clock,
        ILogger<HealthOrchestrator>? logger = null,
        IComponentMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(startupTracker);
        ArgumentNullException.ThrowIfNull(clock);

        _monitors = monitors.ToFrozenDictionary();
        _registeredDependencies = _monitors.Keys;
        _healthResolver = healthResolver;
        _readinessResolver = readinessResolver;
        _startupTracker = startupTracker;
        _clock = clock;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HealthOrchestrator>.Instance;
        _metrics = metrics ?? NullHealthBossMetrics.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<DependencyId> RegisteredDependencies => _registeredDependencies;

    /// <inheritdoc />
    public HealthState CurrentState
    {
        get
        {
            var snapshots = CollectSnapshots();
            return MapHealthStatusToState(ResolveHealthStatus(snapshots));
        }
    }

    /// <inheritdoc />
    public ReadinessStatus ReadinessStatus
    {
        get
        {
            var snapshots = CollectSnapshots();
            return ResolveReadinessStatus(snapshots);
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<DependencySnapshot> GetAllSnapshots()
    {
        var snapshots = CollectSnapshots();
        _cachedSnapshots = snapshots;
        return snapshots;
    }

    /// <inheritdoc />
    public int TotalSignalCount
    {
        get
        {
            var snapshots = _cachedSnapshots ?? CollectSnapshots();
            return snapshots.Sum(s => s.LatestAssessment.TotalSignals);
        }
    }

    /// <inheritdoc />
    public DateTimeOffset? LastTransitionTime
    {
        get
        {
            var snapshots = _cachedSnapshots ?? CollectSnapshots();
            return snapshots
                .Select(s => s.StateChangedAt)
                .Where(t => t != default)
                .OrderDescending()
                .Cast<DateTimeOffset?>()
                .FirstOrDefault();
        }
    }

    /// <inheritdoc />
    public void RecordSignal(DependencyId id, HealthSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        if (_monitors.TryGetValue(id, out var monitor))
        {
            monitor.RecordSignal(signal);
            _metrics.RecordSignal(id.Value, EnumStringCache.SignalOutcomeNames[signal.Outcome]);
            return;
        }

        _logger.LogWarning(
            "Signal dropped for unknown dependency {DependencyId}. Registered dependencies: [{RegisteredDependencies}]",
            id.Value,
            string.Join(", ", _registeredDependencies.Select(d => d.Value)));
    }

    /// <inheritdoc />
    public IDependencyMonitor? GetMonitor(DependencyId id) =>
        _monitors.TryGetValue(id, out var monitor) ? monitor : null;

    /// <inheritdoc />
    public HealthReport GetHealthReport()
    {
        var snapshots = CollectSnapshots();
        var status = ResolveHealthStatus(snapshots);
        return new HealthReport(status, snapshots, _clock.UtcNow);
    }

    /// <inheritdoc />
    public ReadinessReport GetReadinessReport()
    {
        var snapshots = CollectSnapshots();
        var readiness = ResolveReadinessStatus(snapshots);
        return new ReadinessReport(
            readiness,
            snapshots,
            _clock.UtcNow,
            _startupTracker.Status,
            DrainStatus.Idle);
    }

    /// <summary>
    /// Collects a snapshot from each registered monitor and updates <see cref="_cachedSnapshots"/>.
    /// Thread-safe: each monitor's GetSnapshot is independently safe.
    /// </summary>
    private IReadOnlyList<DependencySnapshot> CollectSnapshots()
    {
        var snapshots = new List<DependencySnapshot>(_monitors.Count);
        foreach (var monitor in _monitors.Values)
        {
            var sw = Stopwatch.StartNew();
            var snapshot = monitor.GetSnapshot();
            sw.Stop();
            snapshots.Add(snapshot);
            _metrics.RecordAssessmentDuration(monitor.DependencyId.Value, sw.Elapsed.TotalSeconds);
        }

        foreach (var snapshot in snapshots)
        {
            _metrics.SetHealthState(snapshot.DependencyId.Value, snapshot.CurrentState);
        }

        _cachedSnapshots = snapshots;
        return snapshots;
    }

    /// <summary>
    /// Resolves the aggregate <see cref="HealthStatus"/> using the custom delegate
    /// (if provided) with timeout protection, falling back to worst-status-wins.
    /// </summary>
    private HealthStatus ResolveHealthStatus(IReadOnlyList<DependencySnapshot> snapshots)
    {
        if (_healthResolver is null)
        {
            return DefaultHealthAggregation(snapshots);
        }

        return ExecuteWithTimeout(_healthResolver, snapshots, DefaultHealthAggregation);
    }

    /// <summary>
    /// Resolves the aggregate <see cref="ReadinessStatus"/> using the custom delegate
    /// (if provided) with timeout protection, falling back to default aggregation.
    /// </summary>
    private ReadinessStatus ResolveReadinessStatus(IReadOnlyList<DependencySnapshot> snapshots)
    {
        if (_readinessResolver is null)
        {
            return DefaultReadinessAggregation(snapshots);
        }

        return ExecuteWithTimeout(_readinessResolver, snapshots, DefaultReadinessAggregation);
    }

    /// <summary>
    /// Executes a user-supplied delegate with try-catch and cancellation-based timeout
    /// (Security Finding #7). Uses <see cref="CancellationTokenSource"/> with a timeout so
    /// timed-out delegates are cancelled rather than orphaned on the thread pool.
    /// On failure or timeout, falls back to the default aggregation function.
    /// </summary>
    private static T ExecuteWithTimeout<T>(
        Func<IReadOnlyList<DependencySnapshot>, T> resolver,
        IReadOnlyList<DependencySnapshot> snapshots,
        Func<IReadOnlyList<DependencySnapshot>, T> fallback)
    {
        try
        {
            using var cts = new CancellationTokenSource(DelegateTimeout);
            var token = cts.Token;
            var task = Task.Run(() => resolver(snapshots), token);
            task.Wait(token);
            return task.Result;
        }
        catch (OperationCanceledException)
        {
            // Delegate timed out — fall back to default
            return fallback(snapshots);
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            // Delegate threw — fall back to default
            return fallback(snapshots);
        }
    }

    /// <summary>
    /// Default health aggregation: worst-status-wins across all dependencies.
    /// CircuitOpen → Unhealthy, Degraded → Degraded, all Healthy → Healthy.
    /// </summary>
    internal static HealthStatus DefaultHealthAggregation(IReadOnlyList<DependencySnapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            return HealthStatus.Healthy;
        }

        var worst = HealthStatus.Healthy;

        foreach (var snapshot in snapshots)
        {
            var mapped = snapshot.CurrentState switch
            {
                HealthState.CircuitOpen => HealthStatus.Unhealthy,
                HealthState.Degraded => HealthStatus.Degraded,
                _ => HealthStatus.Healthy,
            };

            if (mapped == HealthStatus.Unhealthy)
            {
                return HealthStatus.Unhealthy;
            }

            if (mapped > worst)
            {
                worst = mapped;
            }
        }

        return worst;
    }

    /// <summary>
    /// Default readiness aggregation: all dependencies must be non-CircuitOpen
    /// for the service to be ready.
    /// </summary>
    internal static ReadinessStatus DefaultReadinessAggregation(IReadOnlyList<DependencySnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            if (snapshot.CurrentState == HealthState.CircuitOpen)
            {
                return ReadinessStatus.NotReady;
            }
        }

        return ReadinessStatus.Ready;
    }

    /// <summary>
    /// Maps a <see cref="HealthStatus"/> to a <see cref="HealthState"/> for the
    /// <see cref="IHealthStateReader.CurrentState"/> property.
    /// </summary>
    private static HealthState MapHealthStatusToState(HealthStatus status) => status switch
    {
        HealthStatus.Unhealthy => HealthState.CircuitOpen,
        HealthStatus.Degraded => HealthState.Degraded,
        _ => HealthState.Healthy,
    };
}

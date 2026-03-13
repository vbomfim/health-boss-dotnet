// <copyright file="TenantHealthStore.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HealthBoss.Core.Components;

/// <summary>
/// Thread-safe per-tenant health tracker with LRU + TTL eviction.
/// Uses <see cref="ConcurrentDictionary{TKey, TValue}"/> keyed by (component, tenant)
/// with a background timer for TTL cleanup.
/// <para>
/// <strong>Tenant health is an isolated dimension</strong> — it does NOT affect
/// service-level probes (<see cref="IDependencyMonitor"/>, <see cref="IHealthReportProvider"/>).
/// </para>
/// <para>
/// Thread-safety model:
/// <list type="bullet">
///   <item>Fast path (existing tenant): lock-free via <see cref="Interlocked"/> on window counters.</item>
///   <item>Slow path (new tenant): serialized by <c>_evictionLock</c> to enforce the MaxTenants hard cap.</item>
///   <item>Status change events are best-effort — minor races under high concurrency are acceptable.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class TenantHealthStore : ITenantHealthTracker, ITenantHealthProvider, IDisposable
{
    private readonly ISystemClock _clock;
    private readonly TenantEvictionConfig _config;
    private readonly IHealthEventSink? _eventSink;
    private readonly ILogger<TenantHealthStore> _logger;
    private readonly ConcurrentDictionary<(DependencyId, TenantId), TenantWindow> _windows = new();
    private readonly ConcurrentDictionary<DependencyId, long> _evictionCounts = new();
    private readonly Timer _scavengeTimer;
    private readonly object _evictionLock = new();
    private bool _disposed;

    /// <summary>
    /// Success rate at or above which a tenant is considered healthy.
    /// </summary>
    internal const double HealthyThreshold = 0.9;

    /// <summary>
    /// Success rate at or above which a tenant is considered degraded (below healthy).
    /// Below this threshold, the tenant is considered unavailable.
    /// </summary>
    internal const double DegradedThreshold = 0.5;

    /// <summary>
    /// Initializes a new instance of the <see cref="TenantHealthStore"/> class.
    /// </summary>
    /// <param name="clock">The system clock for time-based operations.</param>
    /// <param name="config">Eviction configuration (max tenants per component, TTL).</param>
    /// <param name="eventSink">Optional event sink for status change notifications.</param>
    /// <param name="logger">Optional logger for diagnostic messages (defaults to <see cref="NullLogger{T}"/> when null).</param>
    /// <param name="scavengeInterval">
    /// Optional interval for the background TTL scavenger.
    /// Pass <see cref="Timeout.InfiniteTimeSpan"/> to disable background scavenging (useful for testing).
    /// When null, defaults to half the configured TTL (minimum 1 second).
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="clock"/> or <paramref name="config"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <see cref="TenantEvictionConfig.MaxTenants"/> is less than 1 or
    /// <see cref="TenantEvictionConfig.Ttl"/> is not positive.
    /// </exception>
    internal TenantHealthStore(
        ISystemClock clock,
        TenantEvictionConfig config,
        IHealthEventSink? eventSink = null,
        ILogger<TenantHealthStore>? logger = null,
        TimeSpan? scavengeInterval = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(config);

        if (config.MaxTenants < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config),
                config.MaxTenants,
                "MaxTenants must be at least 1.");
        }

        if (config.Ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config),
                config.Ttl,
                "TTL must be greater than zero.");
        }

        _config = config;
        _eventSink = eventSink;
        _logger = logger ?? NullLogger<TenantHealthStore>.Instance;

        var interval = scavengeInterval ?? TimeSpan.FromMilliseconds(
            Math.Max(config.Ttl.TotalMilliseconds / 2, 1000));

        _scavengeTimer = new Timer(ScavengeCallback, null, interval, interval);
    }

    /// <inheritdoc />
    public void RecordSuccess(DependencyId component, TenantId tenantId)
    {
        ValidateIdentifiers(component, tenantId);
        RecordSignalCore(component, tenantId, isSuccess: true, reason: null);
    }

    /// <inheritdoc />
    public void RecordFailure(DependencyId component, TenantId tenantId, string? reason = null)
    {
        ValidateIdentifiers(component, tenantId);
        RecordSignalCore(component, tenantId, isSuccess: false, reason: reason);
    }

    /// <inheritdoc />
    public TenantHealthAssessment GetTenantHealth(DependencyId component, TenantId tenantId)
    {
        ValidateIdentifiers(component, tenantId);

        if (_windows.TryGetValue((component, tenantId), out var window))
        {
            return BuildAssessment(component, tenantId, window);
        }

        // No data — return default healthy assessment
        return new TenantHealthAssessment(
            tenantId, component, TenantHealthStatus.Healthy,
            SuccessRate: 1.0, TotalSignals: 0, FailureCount: 0, LastSignalAt: null);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<TenantId, TenantHealthAssessment> GetAllTenantHealth(DependencyId component)
    {
        if (component.IsDefault)
        {
            throw new ArgumentException(
                "Component identifier must not be default.", nameof(component));
        }

        var result = new Dictionary<TenantId, TenantHealthAssessment>();

        foreach (var kv in _windows)
        {
            if (kv.Key.Item1 == component)
            {
                result[kv.Key.Item2] = BuildAssessment(component, kv.Key.Item2, kv.Value);
            }
        }

        return result;
    }

    /// <summary>
    /// Explicit implementation for <see cref="ITenantHealthProvider"/>.
    /// Returns <c>null</c> when no tenants are tracked for the component,
    /// matching the provider contract (endpoint returns empty array on null).
    /// </summary>
    IReadOnlyDictionary<TenantId, TenantHealthAssessment>? ITenantHealthProvider.GetAllTenantHealth(
        DependencyId component)
    {
        var result = GetAllTenantHealth(component);
        return result.Count > 0 ? result : null;
    }

    /// <inheritdoc />
    public int ActiveTenantCount(DependencyId component)
    {
        if (component.IsDefault)
        {
            throw new ArgumentException(
                "Component identifier must not be default.", nameof(component));
        }

        int count = 0;

        foreach (var kv in _windows)
        {
            if (kv.Key.Item1 == component)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Gets the total number of evictions (LRU + TTL) for a component.
    /// Serves as the backing counter for the <c>healthboss.tenant.evictions</c> metric.
    /// </summary>
    /// <param name="component">The component identifier.</param>
    /// <returns>The cumulative eviction count.</returns>
    internal long GetEvictionCount(DependencyId component) =>
        _evictionCounts.GetValueOrDefault(component);

    /// <summary>
    /// Performs TTL-based scavenging of stale tenant entries.
    /// Called by the background timer and also available for deterministic testing.
    /// </summary>
    internal void ScavengeStaleTenants()
    {
        var cutoff = _clock.UtcNow - _config.Ttl;

        foreach (var kv in _windows)
        {
            if (kv.Value.LastSignalAt < cutoff)
            {
                if (_windows.TryRemove(kv.Key, out _))
                {
                    IncrementEvictionCount(kv.Key.Item1);
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _scavengeTimer.Dispose();
            _disposed = true;
        }
    }

    private void RecordSignalCore(
        DependencyId component,
        TenantId tenantId,
        bool isSuccess,
        string? reason)
    {
        var key = (component, tenantId);
        var now = _clock.UtcNow;

        // Fast path: existing tenant — no lock needed, O(1) lock-free
        if (_windows.TryGetValue(key, out var existing))
        {
            RecordInWindow(existing, isSuccess, now, reason, component, tenantId);
            return;
        }

        // Slow path: new tenant — serialize to enforce MaxTenants hard cap (Security Finding #3)
        lock (_evictionLock)
        {
            // Re-check: another thread may have added this tenant while we waited
            if (_windows.TryGetValue(key, out existing))
            {
                RecordInWindow(existing, isSuccess, now, reason, component, tenantId);
                return;
            }

            // Evict LRU tenant if at capacity (before adding)
            EvictLruIfNeeded(component);

            var window = new TenantWindow(now);

            if (isSuccess)
            {
                window.RecordSuccess(now);
            }
            else
            {
                window.RecordFailure(now, reason);
            }

            _windows.TryAdd(key, window);

            // Dispatch event for status change from implicit Healthy to actual status
            var status = ComputeStatus(window);

            if (status != TenantHealthStatus.Healthy)
            {
                PublishStatusChange(
                    component, tenantId,
                    TenantHealthStatus.Healthy, status, window);
            }
        }
    }

    /// <summary>
    /// Records a signal in an existing window and dispatches events on status change.
    /// </summary>
    /// <remarks>
    /// Status change detection has a minor race under high concurrency: between reading
    /// the previous status and recording the new signal, another thread may also modify
    /// the window. This can lead to duplicate or missed events, which is acceptable for
    /// best-effort observability metrics.
    /// </remarks>
    private void RecordInWindow(
        TenantWindow window,
        bool isSuccess,
        DateTimeOffset now,
        string? reason,
        DependencyId component,
        TenantId tenantId)
    {
        var previousStatus = ComputeStatus(window);

        if (isSuccess)
        {
            window.RecordSuccess(now);
        }
        else
        {
            window.RecordFailure(now, reason);
        }

        var newStatus = ComputeStatus(window);

        if (newStatus != previousStatus)
        {
            PublishStatusChange(component, tenantId, previousStatus, newStatus, window);
        }
    }

    /// <summary>
    /// Evicts the least-recently-used tenant for the given component when at capacity.
    /// Must be called under <c>_evictionLock</c>.
    /// </summary>
    private void EvictLruIfNeeded(DependencyId component)
    {
        // Collect all entries for this component
        var componentEntries = new List<KeyValuePair<(DependencyId, TenantId), TenantWindow>>();

        foreach (var kv in _windows)
        {
            if (kv.Key.Item1 == component)
            {
                componentEntries.Add(kv);
            }
        }

        while (componentEntries.Count >= _config.MaxTenants)
        {
            // Find the least recently used entry
            KeyValuePair<(DependencyId, TenantId), TenantWindow>? oldest = null;
            var oldestTime = DateTimeOffset.MaxValue;

            foreach (var entry in componentEntries)
            {
                if (entry.Value.LastSignalAt < oldestTime)
                {
                    oldestTime = entry.Value.LastSignalAt;
                    oldest = entry;
                }
            }

            if (oldest is null)
            {
                break;
            }

            // Remove from local tracking list regardless
            componentEntries.Remove(oldest.Value);

            // Best-effort remove from dictionary (may have been removed by TTL scavenge)
            if (_windows.TryRemove(oldest.Value.Key, out _))
            {
                IncrementEvictionCount(component);
            }
        }
    }

    private static TenantHealthStatus ComputeStatus(TenantWindow window)
    {
        int total = window.SuccessCount + window.FailureCount;

        if (total == 0)
        {
            return TenantHealthStatus.Healthy;
        }

        double rate = (double)window.SuccessCount / total;

        return rate >= HealthyThreshold
            ? TenantHealthStatus.Healthy
            : rate >= DegradedThreshold
                ? TenantHealthStatus.Degraded
                : TenantHealthStatus.Unavailable;
    }

    private TenantHealthAssessment BuildAssessment(
        DependencyId component,
        TenantId tenantId,
        TenantWindow window)
    {
        int success = window.SuccessCount;
        int failure = window.FailureCount;
        int total = success + failure;
        double rate = total > 0 ? (double)success / total : 1.0;
        var status = ComputeStatus(window);

        return new TenantHealthAssessment(
            tenantId, component, status, rate, total, failure,
            total > 0 ? window.LastSignalAt : null);
    }

    private void PublishStatusChange(
        DependencyId component,
        TenantId tenantId,
        TenantHealthStatus previousStatus,
        TenantHealthStatus newStatus,
        TenantWindow window)
    {
        if (_eventSink is null)
        {
            return;
        }

        int total = window.SuccessCount + window.FailureCount;
        double rate = total > 0 ? (double)window.SuccessCount / total : 1.0;

        // Fire-and-forget — best-effort observability.
        // The dispatcher (or sink) guarantees no unhandled exceptions.
        _ = _eventSink.OnTenantHealthChanged(new TenantHealthEvent(
            component, tenantId, previousStatus, newStatus, rate, _clock.UtcNow));
    }

    private void IncrementEvictionCount(DependencyId component)
    {
        _evictionCounts.AddOrUpdate(component, 1, static (_, count) => count + 1);
    }

    private static void ValidateIdentifiers(DependencyId component, TenantId tenantId)
    {
        if (component.IsDefault)
        {
            throw new ArgumentException(
                "Component identifier must not be default.", nameof(component));
        }

        if (tenantId.IsDefault)
        {
            throw new ArgumentException(
                "Tenant identifier must not be default.", nameof(tenantId));
        }
    }

    private void ScavengeCallback(object? state)
    {
        try
        {
            ScavengeStaleTenants();
        }
        catch (Exception ex)
        {
            // Background timer must not throw — log and swallow exceptions.
            _logger.LogWarning(ex, "Background TTL scavenge failed");
        }
    }

    /// <summary>
    /// Thread-safe per-tenant signal window with lock-free counters.
    /// Uses <see cref="Interlocked"/> and <see cref="Volatile"/> for
    /// O(1) contention-free recording on the hot path.
    /// </summary>
    internal sealed class TenantWindow
    {
        private int _successCount;
        private int _failureCount;
        private long _lastSignalTicks;
        private volatile string? _lastFailureReason;

        /// <summary>
        /// Initializes a new instance of the <see cref="TenantWindow"/> class.
        /// </summary>
        /// <param name="createdAt">The time this window was created.</param>
        internal TenantWindow(DateTimeOffset createdAt)
        {
            _lastSignalTicks = createdAt.UtcTicks;
        }

        /// <summary>Gets the number of successful signals.</summary>
        internal int SuccessCount => Volatile.Read(ref _successCount);

        /// <summary>Gets the number of failed signals.</summary>
        internal int FailureCount => Volatile.Read(ref _failureCount);

        /// <summary>Gets when the last signal was recorded.</summary>
        internal DateTimeOffset LastSignalAt =>
            new(Interlocked.Read(ref _lastSignalTicks), TimeSpan.Zero);

        /// <summary>Gets the reason for the last failure, if any.</summary>
        internal string? LastFailureReason => _lastFailureReason;

        /// <summary>Records a successful signal at the given time.</summary>
        internal void RecordSuccess(DateTimeOffset now)
        {
            Interlocked.Increment(ref _successCount);
            UpdateLastSignal(now);
        }

        /// <summary>Records a failed signal at the given time with an optional reason.</summary>
        internal void RecordFailure(DateTimeOffset now, string? reason)
        {
            Interlocked.Increment(ref _failureCount);
            _lastFailureReason = reason;
            UpdateLastSignal(now);
        }

        /// <summary>
        /// Atomically updates <c>_lastSignalTicks</c> to the maximum of
        /// the current value and the new timestamp (monotonically non-decreasing).
        /// </summary>
        private void UpdateLastSignal(DateTimeOffset now)
        {
            long newTicks = now.UtcTicks;
            long current;

            do
            {
                current = Interlocked.Read(ref _lastSignalTicks);

                if (newTicks <= current)
                {
                    return; // Another thread already set a later timestamp
                }
            }
            while (Interlocked.CompareExchange(ref _lastSignalTicks, newTicks, current) != current);
        }
    }
}

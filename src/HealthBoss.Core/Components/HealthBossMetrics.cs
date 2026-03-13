// <copyright file="HealthBossMetrics.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Components;

/// <summary>
/// OpenTelemetry metrics implementation for HealthBoss using <see cref="System.Diagnostics.Metrics"/>.
/// Creates 18 instruments under the <c>HealthBoss</c> meter: 8 counters, 3 histograms,
/// and 7 observable gauges.
/// <para>
/// Thread-safe: counters and histograms are inherently thread-safe. Observable gauge
/// state is stored in <see cref="ConcurrentDictionary{TKey,TValue}"/> or
/// <see cref="Volatile"/> fields and read by gauge callbacks.
/// </para>
/// <para>
/// The <see cref="Meter"/> lifecycle is managed by <see cref="IMeterFactory"/> via DI.
/// Disposal of the meter is handled by the factory when the DI container is disposed.
/// </para>
/// </summary>
internal sealed class HealthBossMetrics : IHealthBossMetrics
{
    private readonly Meter _meter;

    // ─── Counters (8) ────────────────────────────────────────────────
    private readonly Counter<long> _signalsRecorded;
    private readonly Counter<long> _stateTransitions;
    private readonly Counter<long> _recoveryProbeAttempts;
    private readonly Counter<long> _recoveryProbeSuccesses;
    private readonly Counter<long> _eventSinkDispatches;
    private readonly Counter<long> _eventSinkFailures;
    private readonly Counter<long> _shutdownGateEvaluations;
    private readonly Counter<long> _tenantStatusChanges;

    // ─── Histograms (3) ──────────────────────────────────────────────
    private readonly Histogram<double> _assessmentDuration;
    private readonly Histogram<double> _inboundRequestDuration;
    private readonly Histogram<double> _outboundRequestDuration;

    // ─── Observable Gauge State ──────────────────────────────────────
    private readonly ConcurrentDictionary<string, int> _healthStates = new();
    private int _activeSessionCount;
    private int _drainStatus;
    private readonly ConcurrentDictionary<string, QuorumSnapshot> _quorumSnapshots = new();
    private readonly ConcurrentDictionary<string, int> _tenantCounts = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="HealthBossMetrics"/> class.
    /// Creates all 18 instruments under the <c>HealthBoss</c> meter (version <c>1.0.0</c>).
    /// The <paramref name="meterFactory"/> owns the meter lifecycle — disposal is handled
    /// by the DI container, not by this class.
    /// </summary>
    /// <param name="meterFactory">
    /// The <see cref="IMeterFactory"/> used to create the meter. Provided by the DI container
    /// via <c>services.AddMetrics()</c>.
    /// </param>
    public HealthBossMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _meter = meterFactory.Create("HealthBoss", "1.0.0");

        // Counters
        _signalsRecorded = _meter.CreateCounter<long>(
            "healthboss.signals_recorded",
            description: "Number of health signals recorded");

        _stateTransitions = _meter.CreateCounter<long>(
            "healthboss.state_transitions",
            description: "Number of health state transitions");

        _recoveryProbeAttempts = _meter.CreateCounter<long>(
            "healthboss.recovery_probe_attempts",
            description: "Number of recovery probe attempts");

        _recoveryProbeSuccesses = _meter.CreateCounter<long>(
            "healthboss.recovery_probe_successes",
            description: "Number of successful recovery probes");

        _eventSinkDispatches = _meter.CreateCounter<long>(
            "healthboss.eventsink_dispatches",
            description: "Number of event sink dispatches");

        _eventSinkFailures = _meter.CreateCounter<long>(
            "healthboss.eventsink_failures",
            description: "Number of event sink failures");

        _shutdownGateEvaluations = _meter.CreateCounter<long>(
            "healthboss.shutdown_gate_evaluations",
            description: "Number of shutdown gate evaluations");

        _tenantStatusChanges = _meter.CreateCounter<long>(
            "healthboss.tenant.status_changes",
            description: "Number of tenant health status changes");

        // Histograms
        _assessmentDuration = _meter.CreateHistogram<double>(
            "healthboss.assessment_duration_seconds",
            unit: "s",
            description: "Duration of health assessments in seconds");

        _inboundRequestDuration = _meter.CreateHistogram<double>(
            "healthboss.middleware_inbound_duration_seconds",
            unit: "s",
            description: "Duration of inbound HTTP requests in seconds");

        _outboundRequestDuration = _meter.CreateHistogram<double>(
            "healthboss.middleware_outbound_duration_seconds",
            unit: "s",
            description: "Duration of outbound HTTP requests in seconds");

        // Observable Gauges
        _meter.CreateObservableGauge(
            "healthboss.health_state",
            observeValues: ObserveHealthStates,
            description: "Current health state per component (0=Healthy, 1=Degraded, 2=CircuitOpen)");

        _meter.CreateObservableGauge(
            "healthboss.active_sessions",
            observeValue: () => Volatile.Read(ref _activeSessionCount),
            description: "Current number of active sessions");

        _meter.CreateObservableGauge(
            "healthboss.drain_status",
            observeValue: () => Volatile.Read(ref _drainStatus),
            description: "Current drain status (0=Idle, 1=Draining, 2=Drained, 3=TimedOut)");

        _meter.CreateObservableGauge(
            "healthboss.quorum_healthy_instances",
            observeValues: ObserveQuorumHealthyInstances,
            description: "Number of healthy instances per component");

        _meter.CreateObservableGauge(
            "healthboss.quorum_total_instances",
            observeValues: ObserveQuorumTotalInstances,
            description: "Total number of instances per component");

        _meter.CreateObservableGauge(
            "healthboss.quorum_met",
            observeValues: ObserveQuorumMet,
            description: "Whether quorum is met per component (0=no, 1=yes)");

        _meter.CreateObservableGauge(
            "healthboss.tenant_count",
            observeValues: ObserveTenantCounts,
            description: "Number of tenants per component");
    }

    // ─── Counter Methods ─────────────────────────────────────────────

    /// <inheritdoc />
    public void RecordSignal(string component, string outcome)
    {
        var tags = new TagList
        {
            { "component", component },
            { "outcome", outcome },
        };
        _signalsRecorded.Add(1, tags);
    }

    /// <inheritdoc />
    public void RecordStateTransition(string component, string fromState, string toState)
    {
        var tags = new TagList
        {
            { "component", component },
            { "from_state", fromState },
            { "to_state", toState },
        };
        _stateTransitions.Add(1, tags);
    }

    /// <inheritdoc />
    public void RecordRecoveryProbeAttempt(string component)
    {
        var tags = new TagList { { "component", component } };
        _recoveryProbeAttempts.Add(1, tags);
    }

    /// <inheritdoc />
    public void RecordRecoveryProbeSuccess(string component)
    {
        var tags = new TagList { { "component", component } };
        _recoveryProbeSuccesses.Add(1, tags);
    }

    /// <inheritdoc />
    public void RecordEventSinkDispatch()
    {
        _eventSinkDispatches.Add(1);
    }

    /// <inheritdoc />
    public void RecordEventSinkFailure(string sinkType)
    {
        var tags = new TagList { { "sink_type", sinkType } };
        _eventSinkFailures.Add(1, tags);
    }

    /// <inheritdoc />
    public void RecordShutdownGateEvaluation(string gate, bool approved)
    {
        var tags = new TagList
        {
            { "gate", gate },
            { "result", approved ? "approved" : "denied" },
        };
        _shutdownGateEvaluations.Add(1, tags);
    }

    // ─── Histogram Methods ───────────────────────────────────────────

    /// <inheritdoc />
    public void RecordAssessmentDuration(string component, double durationSeconds)
    {
        var tags = new TagList { { "component", component } };
        _assessmentDuration.Record(durationSeconds, tags);
    }

    /// <inheritdoc />
    public void RecordInboundRequestDuration(string component, double durationSeconds)
    {
        var tags = new TagList { { "component", component } };
        _inboundRequestDuration.Record(durationSeconds, tags);
    }

    /// <inheritdoc />
    public void RecordOutboundRequestDuration(string component, double durationSeconds)
    {
        var tags = new TagList { { "component", component } };
        _outboundRequestDuration.Record(durationSeconds, tags);
    }

    // ─── Observable Gauge Setters ────────────────────────────────────

    /// <inheritdoc />
    public void SetHealthState(string component, HealthState state)
    {
        _healthStates[component] = (int)state;
    }

    /// <inheritdoc />
    public void SetActiveSessionCount(int count)
    {
        Volatile.Write(ref _activeSessionCount, count);
    }

    /// <inheritdoc />
    public void SetDrainStatus(DrainStatus status)
    {
        Volatile.Write(ref _drainStatus, (int)status);
    }

    /// <inheritdoc />
    public void SetQuorumHealth(string component, int healthyInstances, int totalInstances, bool quorumMet)
    {
        _quorumSnapshots[component] = new QuorumSnapshot(healthyInstances, totalInstances, quorumMet);
    }

    /// <inheritdoc />
    public void RecordTenantStatusChange(string component, string tenantId, string fromStatus, string toStatus)
    {
        var tags = new TagList
        {
            { "component", component },
            { "tenant_id", tenantId },
            { "from_status", fromStatus },
            { "to_status", toStatus },
        };
        _tenantStatusChanges.Add(1, tags);
    }

    /// <inheritdoc />
    public void SetTenantCount(string component, int count)
    {
        _tenantCounts[component] = count;
    }

    // ─── Observable Gauge Callbacks ──────────────────────────────────

    private IEnumerable<Measurement<int>> ObserveHealthStates()
    {
        foreach (var kvp in _healthStates)
        {
            yield return new Measurement<int>(
                kvp.Value,
                new TagList { { "component", kvp.Key } });
        }
    }

    private IEnumerable<Measurement<int>> ObserveQuorumHealthyInstances()
    {
        foreach (var kvp in _quorumSnapshots)
        {
            yield return new Measurement<int>(
                kvp.Value.HealthyInstances,
                new TagList { { "component", kvp.Key } });
        }
    }

    private IEnumerable<Measurement<int>> ObserveQuorumTotalInstances()
    {
        foreach (var kvp in _quorumSnapshots)
        {
            yield return new Measurement<int>(
                kvp.Value.TotalInstances,
                new TagList { { "component", kvp.Key } });
        }
    }

    private IEnumerable<Measurement<int>> ObserveQuorumMet()
    {
        foreach (var kvp in _quorumSnapshots)
        {
            yield return new Measurement<int>(
                kvp.Value.QuorumMet ? 1 : 0,
                new TagList { { "component", kvp.Key } });
        }
    }

    private IEnumerable<Measurement<int>> ObserveTenantCounts()
    {
        foreach (var kvp in _tenantCounts)
        {
            yield return new Measurement<int>(
                kvp.Value,
                new TagList { { "component", kvp.Key } });
        }
    }

    /// <summary>
    /// Immutable snapshot of quorum health for a component.
    /// </summary>
    private sealed record QuorumSnapshot(int HealthyInstances, int TotalInstances, bool QuorumMet);
}

// <copyright file="IStateMachineMetrics.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core;

/// <summary>
/// Metrics contract for state machine operations: state transitions,
/// shutdown gate evaluations, recovery probe counters, and event sink
/// dispatch/failure counters.
/// <para>
/// Consumers: <c>ShutdownOrchestrator</c>, <c>RecoveryProber</c>,
/// <c>EventSinkDispatcher</c>.
/// </para>
/// </summary>
/// <remarks>
/// Split from <see cref="IHealthBossMetrics"/> per Interface Segregation Principle (ISP).
/// See GitHub Issue #61.
/// </remarks>
public interface IStateMachineMetrics
{
    /// <summary>
    /// Records a health state transition for a component.
    /// Instrument: <c>healthboss.state_transitions</c>.
    /// </summary>
    /// <param name="component">The component name (dependency ID).</param>
    /// <param name="fromState">The previous health state.</param>
    /// <param name="toState">The new health state.</param>
    /// <remarks>
    /// <b>Cardinality warning:</b> The <paramref name="component"/> tag creates one time series
    /// per unique dependency ID × state pair. Keep the number of registered dependencies bounded
    /// (recommended ≤ 100). See <c>docs/METRICS-CARDINALITY.md</c> for best practices.
    /// </remarks>
    void RecordStateTransition(string component, string fromState, string toState);

    /// <summary>
    /// Records a recovery probe attempt for a component.
    /// Instrument: <c>healthboss.recovery_probe_attempts</c>.
    /// </summary>
    /// <param name="component">The component name (dependency ID).</param>
    /// <remarks>
    /// <b>Cardinality warning:</b> The <paramref name="component"/> tag creates one time series
    /// per unique dependency ID. See <c>docs/METRICS-CARDINALITY.md</c> for best practices.
    /// </remarks>
    void RecordRecoveryProbeAttempt(string component);

    /// <summary>
    /// Records a successful recovery probe for a component.
    /// Instrument: <c>healthboss.recovery_probe_successes</c>.
    /// </summary>
    /// <param name="component">The component name (dependency ID).</param>
    /// <remarks>
    /// <b>Cardinality warning:</b> The <paramref name="component"/> tag creates one time series
    /// per unique dependency ID. See <c>docs/METRICS-CARDINALITY.md</c> for best practices.
    /// </remarks>
    void RecordRecoveryProbeSuccess(string component);

    /// <summary>
    /// Records an event sink dispatch.
    /// Instrument: <c>healthboss.eventsink_dispatches</c>.
    /// </summary>
    void RecordEventSinkDispatch();

    /// <summary>
    /// Records an event sink failure.
    /// Instrument: <c>healthboss.eventsink_failures</c>.
    /// </summary>
    /// <param name="sinkType">The type name of the failing sink.</param>
    void RecordEventSinkFailure(string sinkType);

    /// <summary>
    /// Records a shutdown gate evaluation.
    /// Instrument: <c>healthboss.shutdown_gate_evaluations</c>.
    /// </summary>
    /// <param name="gate">The gate name (e.g., "MinSignals", "Cooldown").</param>
    /// <param name="approved">Whether the gate approved the shutdown.</param>
    void RecordShutdownGateEvaluation(string gate, bool approved);
}

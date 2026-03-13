// <copyright file="IComponentMetrics.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Metrics contract for component-level health signals: signal counters,
/// health state gauges, assessment duration histograms, and HTTP request
/// duration histograms (inbound/outbound).
/// <para>
/// Consumers: <c>HealthOrchestrator</c>, <c>InboundHealthMiddleware</c>,
/// <c>HealthBossDelegatingHandler</c>.
/// </para>
/// </summary>
/// <remarks>
/// Split from <see cref="IHealthBossMetrics"/> per Interface Segregation Principle (ISP).
/// See GitHub Issue #61.
/// </remarks>
public interface IComponentMetrics
{
    /// <summary>
    /// Records a health signal for a component.
    /// Instrument: <c>healthboss.signals_recorded</c>.
    /// </summary>
    /// <param name="component">The component name (dependency ID).</param>
    /// <param name="outcome">The signal outcome (e.g., "Success", "Failure").</param>
    /// <remarks>
    /// <b>Cardinality warning:</b> The <paramref name="component"/> tag creates one time series
    /// per unique dependency ID. Keep the number of registered dependencies bounded
    /// (recommended ≤ 100). See <c>docs/METRICS-CARDINALITY.md</c> for best practices.
    /// </remarks>
    void RecordSignal(string component, string outcome);

    /// <summary>
    /// Records the duration of a health assessment for a component.
    /// Instrument: <c>healthboss.assessment_duration_seconds</c>.
    /// </summary>
    /// <param name="component">The component name (dependency ID).</param>
    /// <param name="durationSeconds">The duration in seconds.</param>
    /// <remarks>
    /// <b>Cardinality warning:</b> The <paramref name="component"/> tag creates one time series
    /// per unique dependency ID. See <c>docs/METRICS-CARDINALITY.md</c> for best practices.
    /// </remarks>
    void RecordAssessmentDuration(string component, double durationSeconds);

    /// <summary>
    /// Records the duration of an inbound HTTP request for a component.
    /// Instrument: <c>healthboss.middleware_inbound_duration_seconds</c>.
    /// </summary>
    /// <param name="component">The component name (dependency ID).</param>
    /// <param name="durationSeconds">The duration in seconds.</param>
    void RecordInboundRequestDuration(string component, double durationSeconds);

    /// <summary>
    /// Records the duration of an outbound HTTP request for a component.
    /// Instrument: <c>healthboss.middleware_outbound_duration_seconds</c>.
    /// </summary>
    /// <param name="component">The component name (dependency ID).</param>
    /// <param name="durationSeconds">The duration in seconds.</param>
    void RecordOutboundRequestDuration(string component, double durationSeconds);

    /// <summary>
    /// Sets the current health state for a component (observable gauge).
    /// Instrument: <c>healthboss.health_state</c>.
    /// </summary>
    /// <param name="component">The component name (dependency ID).</param>
    /// <param name="state">The current health state.</param>
    /// <remarks>
    /// <b>Cardinality warning:</b> The <paramref name="component"/> tag creates one time series
    /// per unique dependency ID. See <c>docs/METRICS-CARDINALITY.md</c> for best practices.
    /// </remarks>
    void SetHealthState(string component, HealthState state);
}

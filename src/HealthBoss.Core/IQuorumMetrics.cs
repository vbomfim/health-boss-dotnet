// <copyright file="IQuorumMetrics.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core;

/// <summary>
/// Metrics contract for quorum-level tracking: quorum health gauges
/// (healthy instances, total instances, quorum met).
/// <para>
/// Consumers: <c>QuorumEvaluator</c>.
/// </para>
/// </summary>
/// <remarks>
/// Split from <see cref="IHealthBossMetrics"/> per Interface Segregation Principle (ISP).
/// See GitHub Issue #61.
/// </remarks>
public interface IQuorumMetrics
{
    /// <summary>
    /// Sets the quorum health state for a component (observable gauges).
    /// Updates three instruments: <c>healthboss.quorum_healthy_instances</c>,
    /// <c>healthboss.quorum_total_instances</c>, and <c>healthboss.quorum_met</c>.
    /// </summary>
    /// <param name="component">The component name (dependency ID).</param>
    /// <param name="healthyInstances">The number of healthy instances.</param>
    /// <param name="totalInstances">The total number of instances.</param>
    /// <param name="quorumMet">Whether the quorum requirement is met.</param>
    /// <remarks>
    /// <b>Cardinality warning:</b> The <paramref name="component"/> tag creates three time series
    /// (one per gauge) per unique dependency ID. See <c>docs/METRICS-CARDINALITY.md</c>
    /// for best practices.
    /// </remarks>
    void SetQuorumHealth(string component, int healthyInstances, int totalInstances, bool quorumMet);
}

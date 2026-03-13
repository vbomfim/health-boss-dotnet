// <copyright file="QuorumEvaluator.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Components;

/// <summary>
/// Pure-function evaluator: computes a <see cref="QuorumAssessment"/>
/// from instance health results and a quorum policy with no side effects.
/// </summary>
internal sealed class QuorumEvaluator : IQuorumEvaluator
{
    /// <inheritdoc />
    public QuorumAssessment Evaluate(
        IReadOnlyList<InstanceHealthResult> results,
        QuorumHealthPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(policy);

        int healthyCount = CountHealthy(results);
        int totalInstances = DetermineTotalInstances(results.Count, policy.TotalExpectedInstances);
        bool quorumMet = healthyCount >= policy.MinimumHealthyInstances;
        HealthState status = DetermineState(healthyCount, policy.MinimumHealthyInstances);

        return new QuorumAssessment(
            HealthyInstances: healthyCount,
            TotalInstances: totalInstances,
            MinimumRequired: policy.MinimumHealthyInstances,
            QuorumMet: quorumMet,
            Status: status,
            InstanceResults: results);
    }

    /// <summary>
    /// Determines the health state from the healthy instance count and quorum threshold.
    /// </summary>
    private static HealthState DetermineState(int healthyCount, int minimumRequired) =>
        healthyCount switch
        {
            0 => HealthState.CircuitOpen,
            _ when healthyCount >= minimumRequired => HealthState.Healthy,
            _ => HealthState.Degraded,
        };

    /// <summary>
    /// Resolves the total instance count, preferring the larger of probed vs expected
    /// (TotalExpectedInstances == 0 means dynamic/unknown fleet size).
    /// </summary>
    private static int DetermineTotalInstances(int probedCount, int totalExpected) =>
        totalExpected > 0
            ? Math.Max(probedCount, totalExpected)
            : probedCount;

    private static int CountHealthy(IReadOnlyList<InstanceHealthResult> results)
    {
        int count = 0;
        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].IsHealthy)
            {
                count++;
            }
        }

        return count;
    }
}

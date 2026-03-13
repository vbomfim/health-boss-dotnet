// <copyright file="IQuorumEvaluator.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Pure-function evaluator: given instance health results + quorum policy → quorum assessment.
/// Stateless and side-effect free.
/// </summary>
public interface IQuorumEvaluator
{
    /// <summary>
    /// Evaluates the given instance health results against a quorum policy to produce an assessment.
    /// </summary>
    /// <param name="results">The instance health results from a probe cycle.</param>
    /// <param name="policy">The quorum policy to evaluate against.</param>
    /// <returns>A quorum assessment with the recommended state and metrics.</returns>
    QuorumAssessment Evaluate(IReadOnlyList<InstanceHealthResult> results, QuorumHealthPolicy policy);
}

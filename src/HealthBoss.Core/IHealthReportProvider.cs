// <copyright file="IHealthReportProvider.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Provides health and readiness reports for probe endpoints.
/// Core-level equivalent of <c>HealthBoss.AspNetCore.IHealthReportProvider</c>
/// so the orchestrator can live in Core without depending on AspNetCore.
/// </summary>
public interface IHealthReportProvider
{
    /// <summary>
    /// Returns the current aggregated health report across all dependencies.
    /// </summary>
    HealthReport GetHealthReport();

    /// <summary>
    /// Returns the current aggregated readiness report including startup and drain status.
    /// </summary>
    ReadinessReport GetReadinessReport();
}

using HealthBoss.Core.Contracts;

namespace HealthBoss.AspNetCore;

/// <summary>
/// Provides health and readiness reports for probe endpoints.
/// Extends <see cref="Core.IHealthReportProvider"/> so that the HealthOrchestrator
/// (which lives in Core) can satisfy this interface without depending on AspNetCore.
/// </summary>
public interface IHealthReportProvider : Core.IHealthReportProvider
{
}

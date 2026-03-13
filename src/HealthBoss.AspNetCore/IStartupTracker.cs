using HealthBoss.Core.Contracts;

namespace HealthBoss.AspNetCore;

/// <summary>
/// Tracks pod initialization lifecycle for the startup probe.
/// </summary>
public interface IStartupTracker
{
    /// <summary>
    /// Gets the current startup status.
    /// </summary>
    StartupStatus Status { get; }

    /// <summary>
    /// Marks the pod as ready (initialization complete).
    /// </summary>
    void MarkReady();

    /// <summary>
    /// Marks the pod as failed (initialization failed).
    /// </summary>
    void MarkFailed();
}

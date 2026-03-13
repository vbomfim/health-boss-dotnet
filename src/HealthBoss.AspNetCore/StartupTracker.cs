using HealthBoss.Core.Contracts;

namespace HealthBoss.AspNetCore;

/// <summary>
/// Default implementation of <see cref="IStartupTracker"/> and <see cref="HealthBoss.Core.IStartupTracker"/>.
/// Thread-safe; uses volatile reads/writes for the status field.
/// </summary>
public sealed class StartupTracker : IStartupTracker, HealthBoss.Core.IStartupTracker
{
    private volatile StartupStatus _status = StartupStatus.Starting;

    /// <inheritdoc />
    public StartupStatus Status => _status;

    /// <inheritdoc />
    public void MarkReady() => _status = StartupStatus.Ready;

    /// <inheritdoc />
    public void MarkFailed() => _status = StartupStatus.Failed;

    /// <inheritdoc />
    void HealthBoss.Core.IStartupTracker.MarkFailed(string? reason) => _status = StartupStatus.Failed;
}

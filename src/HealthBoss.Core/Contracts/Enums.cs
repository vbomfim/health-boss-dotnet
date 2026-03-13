// <copyright file="Enums.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core.Contracts;

/// <summary>
/// Represents the health state of a monitored dependency.
/// </summary>
public enum HealthState
{
    /// <summary>The dependency is operating normally.</summary>
    Healthy,

    /// <summary>The dependency is experiencing partial failures.</summary>
    Degraded,

    /// <summary>The circuit breaker is open; the dependency is considered unavailable.</summary>
    CircuitOpen,
}

/// <summary>
/// Represents the startup lifecycle status.
/// </summary>
public enum StartupStatus
{
    /// <summary>The service is starting up.</summary>
    Starting,

    /// <summary>The service is ready to accept traffic.</summary>
    Ready,

    /// <summary>The service failed to start.</summary>
    Failed,
}

/// <summary>
/// Represents the graceful-drain lifecycle status.
/// </summary>
public enum DrainStatus
{
    /// <summary>No drain in progress.</summary>
    Idle,

    /// <summary>Drain is in progress.</summary>
    Draining,

    /// <summary>Drain completed successfully.</summary>
    Drained,

    /// <summary>Drain exceeded the configured timeout.</summary>
    TimedOut,
}

/// <summary>
/// Controls the level of detail returned in health reports.
/// </summary>
public enum DetailLevel
{
    /// <summary>Return status only, no dependency details.</summary>
    StatusOnly,

    /// <summary>Return a summary with aggregated metrics.</summary>
    Summary,

    /// <summary>Return full details including per-dependency snapshots.</summary>
    Full,
}

/// <summary>
/// Represents the outcome of a recorded health signal.
/// </summary>
public enum SignalOutcome
{
    /// <summary>The operation succeeded.</summary>
    Success,

    /// <summary>The operation failed.</summary>
    Failure,

    /// <summary>The operation timed out.</summary>
    Timeout,

    /// <summary>The operation was rejected (e.g., circuit breaker open).</summary>
    Rejected,
}

/// <summary>
/// Represents the overall health status for health reports.
/// </summary>
public enum HealthStatus
{
    /// <summary>All dependencies are healthy.</summary>
    Healthy,

    /// <summary>One or more dependencies are degraded.</summary>
    Degraded,

    /// <summary>One or more dependencies are unhealthy (circuit open).</summary>
    Unhealthy,
}

/// <summary>
/// Represents the readiness status for readiness reports.
/// </summary>
public enum ReadinessStatus
{
    /// <summary>The service is ready to accept traffic.</summary>
    Ready,

    /// <summary>The service is not ready to accept traffic.</summary>
    NotReady,
}

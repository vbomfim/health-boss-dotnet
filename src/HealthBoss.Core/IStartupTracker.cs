// <copyright file="IStartupTracker.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Tracks the startup lifecycle of the application.
/// Allows components to signal when the application is ready to accept traffic
/// or when startup has failed.
/// </summary>
public interface IStartupTracker
{
    /// <summary>
    /// Gets the current startup status.
    /// </summary>
    StartupStatus Status { get; }

    /// <summary>
    /// Marks the application as ready to accept traffic.
    /// </summary>
    void MarkReady();

    /// <summary>
    /// Marks the application startup as failed.
    /// </summary>
    /// <param name="reason">Optional reason describing the failure.</param>
    void MarkFailed(string? reason = null);
}

// <copyright file="ISessionHandle.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Represents an active session being tracked by <see cref="ISessionHealthTracker"/>.
/// Disposing the handle signals that the session has ended.
/// If <see cref="Complete"/> was not called before disposal,
/// the outcome is recorded as <see cref="SessionOutcome.Cancelled"/>.
/// </summary>
public interface ISessionHandle : IDisposable
{
    /// <summary>
    /// Records a named event that occurred during this session (e.g., "message-received", "error-retry").
    /// </summary>
    /// <param name="eventName">A descriptive name for the event.</param>
    void RecordEvent(string eventName);

    /// <summary>
    /// Marks the session as completed with the specified outcome.
    /// Must be called at most once. Subsequent calls are ignored.
    /// </summary>
    /// <param name="outcome">The outcome of the session.</param>
    void Complete(SessionOutcome outcome);
}

// <copyright file="ISessionHealthTracker.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Tracks active WebSocket/long-lived session counts and outcomes.
/// Exposes an <see cref="ActiveSessionCount"/> gauge suitable for SIGTERM drain decisions.
/// Thread-safe — all members may be called concurrently from any thread.
/// </summary>
public interface ISessionHealthTracker
{
    /// <summary>
    /// Starts tracking a new session. The returned handle must be disposed
    /// when the session ends. Disposing without calling
    /// <see cref="ISessionHandle.Complete"/> records a <see cref="SessionOutcome.Cancelled"/> outcome.
    /// </summary>
    /// <param name="sessionType">A logical grouping for the session (e.g., "websocket", "grpc-stream").</param>
    /// <param name="sessionId">A unique identifier for this session instance.</param>
    /// <returns>A disposable handle representing the active session.</returns>
    ISessionHandle TrackSessionStart(string sessionType, string sessionId);

    /// <summary>
    /// Returns a point-in-time snapshot of session health metrics.
    /// </summary>
    /// <returns>An immutable snapshot of current session health.</returns>
    SessionHealthSnapshot GetSnapshot();

    /// <summary>
    /// Gets the number of currently active (not yet disposed) sessions.
    /// Thread-safe atomic read via <see cref="System.Threading.Volatile"/>.
    /// </summary>
    int ActiveSessionCount { get; }
}

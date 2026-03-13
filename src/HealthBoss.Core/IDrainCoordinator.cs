// <copyright file="IDrainCoordinator.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Coordinates graceful session drain during shutdown.
/// <para>
/// Waits for active sessions to reach zero OR until a configured timeout
/// is exceeded — whichever comes first. Optionally invokes a custom
/// <see cref="DrainConfig.DrainDelegate"/> on each poll cycle.
/// </para>
/// </summary>
public interface IDrainCoordinator : IDisposable
{
    /// <summary>
    /// Gets the current drain lifecycle status.
    /// </summary>
    DrainStatus Status { get; }

    /// <summary>
    /// Initiates a graceful drain, polling <paramref name="getActiveSessionCount"/>
    /// every 500 ms until session count reaches zero or <see cref="DrainConfig.Timeout"/>
    /// is exceeded.
    /// </summary>
    /// <remarks>
    /// <b>Single-use semantics:</b> The first call starts the drain loop. Subsequent calls
    /// while a drain is in progress piggyback on the active operation and return the same
    /// result. Once a drain has completed (<see cref="DrainStatus.Drained"/> or
    /// <see cref="DrainStatus.TimedOut"/>), calling <c>DrainAsync</c> again returns the
    /// previously completed result — the coordinator does not reset.
    /// </remarks>
    /// <param name="getActiveSessionCount">
    /// Delegate that returns the current number of active sessions.
    /// Must be thread-safe.
    /// </param>
    /// <param name="config">Drain behavior configuration (timeout, optional delegate).</param>
    /// <param name="ct">Cancellation token to abort the drain.</param>
    /// <returns>
    /// The final <see cref="DrainStatus"/>:
    /// <see cref="DrainStatus.Drained"/> when sessions reach zero,
    /// <see cref="DrainStatus.TimedOut"/> when the timeout is exceeded.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="getActiveSessionCount"/> or <paramref name="config"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was cancelled during the drain.
    /// </exception>
    Task<DrainStatus> DrainAsync(
        Func<int> getActiveSessionCount,
        DrainConfig config,
        CancellationToken ct = default);
}

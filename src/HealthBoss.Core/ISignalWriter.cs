// <copyright file="ISignalWriter.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Narrow write-only interface for recording health signals.
/// Consumers that only need to write signals (e.g., gRPC interceptors,
/// Polly circuit-breaker hooks, recovery probers) should depend on this
/// interface instead of the full <see cref="ISignalBuffer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Introduced to satisfy the Interface Segregation Principle (ISP):
/// <see cref="ISignalBuffer"/> exposes read, trim, and count operations
/// that write-only consumers never use. Depending on the full interface
/// forces test fakes to stub methods they don't care about.
/// </para>
/// <para>
/// <see cref="ISignalBuffer"/> extends this interface, so any existing
/// <see cref="ISignalBuffer"/> implementation automatically satisfies
/// <see cref="ISignalWriter"/> without code changes.
/// </para>
/// </remarks>
public interface ISignalWriter
{
    /// <summary>
    /// Records a health signal. O(1), never blocks readers.
    /// </summary>
    /// <param name="signal">The signal to record.</param>
    void Record(HealthSignal signal);
}

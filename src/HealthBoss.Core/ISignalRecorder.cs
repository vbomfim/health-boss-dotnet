// <copyright file="ISignalRecorder.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Write-only interface for recording health signals.
/// Separated from <see cref="ISignalBuffer"/> per the Interface Segregation Principle:
/// middleware and handlers only need to record signals, not read or trim the buffer.
/// </summary>
public interface ISignalRecorder
{
    /// <summary>
    /// Records a health signal. O(1), never blocks readers.
    /// </summary>
    /// <param name="signal">The signal to record.</param>
    void Record(HealthSignal signal);
}

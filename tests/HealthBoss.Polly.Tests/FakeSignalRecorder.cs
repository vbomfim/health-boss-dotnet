// <copyright file="FakeSignalRecorder.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Polly.Tests;

/// <summary>
/// Test double that captures recorded signals for assertion.
/// Implements <see cref="ISignalBuffer"/> (the narrowest available write interface
/// after <c>ISignalRecorder</c> was removed in v1.0).
/// Thread-safe: all access is synchronized with a lock since
/// <see cref="Record"/> is called from async Polly callbacks.
/// </summary>
internal sealed class FakeSignalRecorder : ISignalBuffer
{
    private readonly List<HealthSignal> _signals = [];
    private readonly object _lock = new();

    public IReadOnlyList<HealthSignal> Signals
    {
        get
        {
            lock (_lock)
            {
                return [.. _signals];
            }
        }
    }

    public void Record(HealthSignal signal)
    {
        lock (_lock)
        {
            _signals.Add(signal);
        }
    }

    public IReadOnlyList<HealthSignal> GetSignals(TimeSpan window)
    {
        lock (_lock)
        {
            var cutoff = DateTimeOffset.UtcNow - window;
            return _signals.Where(s => s.Timestamp >= cutoff).ToList();
        }
    }

    public void Trim(DateTimeOffset cutoff)
    {
        lock (_lock)
        {
            _signals.RemoveAll(s => s.Timestamp < cutoff);
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _signals.Count;
            }
        }
    }
}

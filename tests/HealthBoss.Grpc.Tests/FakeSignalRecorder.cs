// <copyright file="FakeSignalRecorder.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Grpc.Tests;

/// <summary>
/// Test double that captures recorded signals for assertion.
/// Implements <see cref="ISignalWriter"/> — the narrow write-only interface
/// that <see cref="GrpcClientHealthInterceptor"/> depends on.
/// Thread-safe: all access is synchronized with a lock since
/// <see cref="Record"/> may be called from concurrent gRPC callbacks.
/// </summary>
internal sealed class FakeSignalRecorder : ISignalWriter
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
}

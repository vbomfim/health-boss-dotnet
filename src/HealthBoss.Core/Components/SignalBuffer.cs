// <copyright file="SignalBuffer.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Components;

/// <summary>
/// Thread-safe ring buffer for health signal ingestion.
/// Uses a <see cref="ConcurrentQueue{T}"/> with capacity-based eviction.
/// </summary>
internal sealed class SignalBuffer : ISignalBuffer
{
    private readonly ISystemClock _clock;
    private readonly int _maxCapacity;
    private readonly ConcurrentQueue<HealthSignal> _queue = new();
    private readonly object _trimLock = new();
    private int _count;

    /// <summary>
    /// Initializes a new instance of the <see cref="SignalBuffer"/> class.
    /// </summary>
    /// <param name="clock">The system clock for time-based queries.</param>
    /// <param name="maxCapacity">Maximum number of signals to buffer.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxCapacity"/> is less than 1.</exception>
    public SignalBuffer(ISystemClock clock, int maxCapacity = 10_000)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        if (maxCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCapacity),
                maxCapacity,
                "Max capacity must be at least 1.");
        }

        _maxCapacity = maxCapacity;
    }

    /// <inheritdoc />
    public int Count => Volatile.Read(ref _count);

    /// <inheritdoc />
    public void Record(HealthSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        _queue.Enqueue(signal);
        Interlocked.Increment(ref _count);
        EvictIfOverCapacity();
    }

    /// <inheritdoc />
    public IReadOnlyList<HealthSignal> GetSignals(TimeSpan window)
    {
        var cutoff = _clock.UtcNow - window;

        return _queue
            .Where(s => s.Timestamp >= cutoff)
            .OrderBy(s => s.Timestamp)
            .ToList();
    }

    /// <inheritdoc />
    public void Trim(DateTimeOffset cutoff)
    {
        lock (_trimLock)
        {
            while (_queue.TryPeek(out var oldest) && oldest.Timestamp < cutoff)
            {
                if (_queue.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _count);
                }
            }
        }
    }

    private void EvictIfOverCapacity()
    {
        while (Volatile.Read(ref _count) > _maxCapacity)
        {
            if (_queue.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _count);
            }
        }
    }
}

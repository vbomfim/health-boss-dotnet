// <copyright file="ThreadSafetyTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace HealthBoss.Core.Tests.EdgeCases;

/// <summary>
/// Rigorous thread-safety tests for SignalBuffer.
/// Goes beyond the existing two concurrency tests with race condition scenarios.
/// </summary>
public sealed class ThreadSafetyTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ISystemClock _clock;

    public ThreadSafetyTests()
    {
        _timeProvider.SetUtcNow(TestFixtures.BaseTime);
        _clock = new SystemClock(_timeProvider);
    }

    /// <summary>
    /// [EDGE] Concurrent Record + Trim: writers add signals while Trim removes old ones.
    /// Verifies no exceptions or data corruption under contention.
    /// </summary>
    [Fact]
    public async Task Concurrent_record_and_trim_does_not_corrupt_state()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 100_000);
        const int signalsToRecord = 2_000;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Writer: records signals with incrementing timestamps
        var writeTask = Task.Run(() =>
        {
            for (int i = 0; i < signalsToRecord; i++)
            {
                buffer.Record(TestFixtures.CreateSignal(
                    SignalOutcome.Success,
                    timestamp: _clock.UtcNow.AddMilliseconds(i)));
            }
        });

        // Trimmer: continuously trims old signals
        var trimTask = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                buffer.Trim(_clock.UtcNow.AddMilliseconds(i * 5));
            }
        });

        await Task.WhenAll(writeTask, trimTask);

        // Count should be non-negative and consistent
        buffer.Count.Should().BeGreaterThanOrEqualTo(0);
        var signals = buffer.GetSignals(TimeSpan.FromHours(1));
        signals.Count.Should().BeLessThanOrEqualTo(buffer.Count + 10); // Small margin for race
    }

    /// <summary>
    /// [EDGE] Concurrent Record + GetSignals: readers query while writers add.
    /// Verifies GetSignals never throws and always returns valid data.
    /// </summary>
    [Fact]
    public async Task Concurrent_record_and_get_signals_returns_valid_snapshots()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 100_000);
        const int writeCount = 5_000;
        const int readCount = 500;
        var allSnapshotsValid = true;

        var writeTask = Task.Run(() =>
        {
            for (int i = 0; i < writeCount; i++)
            {
                buffer.Record(TestFixtures.CreateSignal(
                    SignalOutcome.Success,
                    timestamp: _clock.UtcNow.AddMilliseconds(i)));
            }
        });

        var readTask = Task.Run(() =>
        {
            for (int i = 0; i < readCount; i++)
            {
                try
                {
                    var signals = buffer.GetSignals(TimeSpan.FromMinutes(5));
                    // Each snapshot should be internally consistent
                    if (signals.Any(s => s.Timestamp == default))
                    {
                        allSnapshotsValid = false;
                    }
                }
                catch
                {
                    allSnapshotsValid = false;
                }
            }
        });

        await Task.WhenAll(writeTask, readTask);

        allSnapshotsValid.Should().BeTrue("GetSignals should never return invalid data during concurrent writes");
        buffer.Count.Should().Be(writeCount);
    }

    /// <summary>
    /// [EDGE] Concurrent Trim + GetSignals: verify no exceptions when
    /// signals are removed while being read.
    /// </summary>
    [Fact]
    public async Task Concurrent_trim_and_get_signals_does_not_throw()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 100_000);

        // Pre-populate with signals
        for (int i = 0; i < 5_000; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Success,
                timestamp: _clock.UtcNow.AddMilliseconds(i)));
        }

        var trimTask = Task.Run(() =>
        {
            for (int i = 0; i < 5_000; i++)
            {
                buffer.Trim(_clock.UtcNow.AddMilliseconds(i));
            }
        });

        var readTask = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                // Should never throw
                _ = buffer.GetSignals(TimeSpan.FromMinutes(5));
                _ = buffer.Count;
            }
        });

        var act = () => Task.WhenAll(trimTask, readTask);

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// [EDGE] Heavy eviction contention: many writers with small capacity.
    /// Tests that Count stays in valid bounds under capacity-based eviction.
    /// </summary>
    [Fact]
    public async Task Heavy_eviction_contention_count_stays_valid()
    {
        const int capacity = 100;
        var buffer = new SignalBuffer(_clock, maxCapacity: capacity);
        const int writersCount = 8;
        const int signalsPerWriter = 1_000;

        var tasks = Enumerable.Range(0, writersCount).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < signalsPerWriter; i++)
            {
                buffer.Record(TestFixtures.CreateSignal(
                    SignalOutcome.Success,
                    timestamp: _clock.UtcNow.AddMilliseconds(w * 10_000 + i)));
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Count should never exceed capacity (plus small margin for race between
        // increment and eviction)
        buffer.Count.Should().BeLessThanOrEqualTo(capacity + writersCount,
            "Count should stabilize near capacity after all writers finish");
        buffer.Count.Should().BeGreaterThan(0, "Buffer should not be empty");
    }

    /// <summary>
    /// [EDGE] All four concurrent operations simultaneously:
    /// Record, GetSignals, Trim, and Count access.
    /// </summary>
    [Fact]
    public async Task All_operations_concurrent_does_not_deadlock_or_corrupt()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 10_000);
        const int iterations = 1_000;

        var recordTask = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                buffer.Record(TestFixtures.CreateSignal(
                    SignalOutcome.Success,
                    timestamp: _clock.UtcNow.AddMilliseconds(i)));
            }
        });

        var getTask = Task.Run(() =>
        {
            for (int i = 0; i < iterations / 10; i++)
            {
                _ = buffer.GetSignals(TimeSpan.FromMinutes(5));
            }
        });

        var trimTask = Task.Run(() =>
        {
            for (int i = 0; i < iterations / 10; i++)
            {
                buffer.Trim(_clock.UtcNow.AddMilliseconds(i * 5));
            }
        });

        var countTask = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                var c = buffer.Count;
                c.Should().BeGreaterThanOrEqualTo(0);
            }
        });

        var act = () => Task.WhenAll(recordTask, getTask, trimTask, countTask);

        await act.Should().NotThrowAsync("no operation combination should deadlock or throw");
    }

    /// <summary>
    /// [EDGE] Parallel writers with distinct dependency IDs.
    /// Verifies signals from all writers are captured.
    /// </summary>
    [Fact]
    public async Task Parallel_writers_with_distinct_dependency_ids_all_recorded()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 100_000);
        const int writersCount = 4;
        const int signalsPerWriter = 500;

        var tasks = Enumerable.Range(0, writersCount).Select(w =>
        {
            var depId = new DependencyId($"dep-{w}");
            return Task.Run(() =>
            {
                for (int i = 0; i < signalsPerWriter; i++)
                {
                    buffer.Record(TestFixtures.CreateSignal(
                        SignalOutcome.Success,
                        timestamp: _clock.UtcNow.AddMilliseconds(w * 10_000 + i),
                        dependencyId: depId));
                }
            });
        }).ToArray();

        await Task.WhenAll(tasks);

        buffer.Count.Should().Be(writersCount * signalsPerWriter);

        // Verify all dependency IDs are present
        var signals = buffer.GetSignals(TimeSpan.FromHours(1));
        var distinctDeps = signals.Select(s => s.DependencyId).Distinct().ToList();
        distinctDeps.Should().HaveCount(writersCount);
    }
}

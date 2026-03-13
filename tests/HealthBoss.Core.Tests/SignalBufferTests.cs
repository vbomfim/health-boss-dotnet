using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace HealthBoss.Core.Tests;

public sealed class SignalBufferTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ISystemClock _clock;

    public SignalBufferTests()
    {
        _timeProvider.SetUtcNow(TestFixtures.BaseTime);
        _clock = new SystemClock(_timeProvider);
    }

    private SignalBuffer CreateBuffer(int maxCapacity = 10_000) =>
        new(_clock, maxCapacity);

    [Fact]
    public void SignalBuffer_implements_ISignalRecorder()
    {
        // ISP: SignalBuffer implements the narrow ISignalRecorder interface
        var buffer = CreateBuffer();
        buffer.Should().BeAssignableTo<ISignalRecorder>();
    }

    [Fact]
    public void ISignalRecorder_Record_adds_signal_to_buffer()
    {
        // Verify recording through the narrow interface still works
        ISignalRecorder recorder = CreateBuffer();
        var signal = TestFixtures.CreateSignal(timestamp: _clock.UtcNow);

        recorder.Record(signal);

        // Cast back to read — the recorder interface is write-only
        var buffer = (SignalBuffer)recorder;
        buffer.Count.Should().Be(1);
    }

    [Fact]
    public void Record_and_GetSignals_returns_signal_within_window()
    {
        var buffer = CreateBuffer();
        var signal = TestFixtures.CreateSignal(timestamp: _clock.UtcNow);

        buffer.Record(signal);
        var result = buffer.GetSignals(TimeSpan.FromMinutes(5));

        result.Should().ContainSingle()
            .Which.Should().Be(signal);
    }

    [Fact]
    public void GetSignals_excludes_signals_outside_window()
    {
        var buffer = CreateBuffer();

        // Record a signal at T=0
        buffer.Record(TestFixtures.CreateSignal(timestamp: _clock.UtcNow));

        // Advance time past the window
        _timeProvider.Advance(TimeSpan.FromMinutes(10));

        // Record another signal at T=10min
        buffer.Record(TestFixtures.CreateSignal(timestamp: _clock.UtcNow));

        var result = buffer.GetSignals(TimeSpan.FromMinutes(5));

        result.Should().HaveCount(1);
        result[0].Timestamp.Should().Be(_clock.UtcNow);
    }

    [Fact]
    public void Count_reflects_number_of_buffered_signals()
    {
        var buffer = CreateBuffer();

        buffer.Count.Should().Be(0);

        buffer.Record(TestFixtures.CreateSignal(timestamp: _clock.UtcNow));
        buffer.Record(TestFixtures.CreateSignal(timestamp: _clock.UtcNow));

        buffer.Count.Should().Be(2);
    }

    [Fact]
    public void Capacity_eviction_removes_oldest_when_full()
    {
        var buffer = CreateBuffer(maxCapacity: 3);

        for (int i = 0; i < 5; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                timestamp: _clock.UtcNow.AddSeconds(i)));
        }

        buffer.Count.Should().Be(3);

        // Should only have the 3 newest signals (seconds 2, 3, 4)
        var signals = buffer.GetSignals(TimeSpan.FromMinutes(5));
        signals.Should().HaveCount(3);
        signals[0].Timestamp.Should().Be(_clock.UtcNow.AddSeconds(2));
    }

    [Fact]
    public void Trim_removes_signals_before_cutoff()
    {
        var buffer = CreateBuffer();

        buffer.Record(TestFixtures.CreateSignal(timestamp: _clock.UtcNow));
        buffer.Record(TestFixtures.CreateSignal(
            timestamp: _clock.UtcNow.AddMinutes(5)));

        buffer.Trim(_clock.UtcNow.AddMinutes(1));

        buffer.Count.Should().Be(1);
    }

    [Fact]
    public void GetSignals_on_empty_buffer_returns_empty_list()
    {
        var buffer = CreateBuffer();
        var result = buffer.GetSignals(TimeSpan.FromMinutes(5));

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Thread_safety_parallel_writes_and_reads()
    {
        var buffer = CreateBuffer(maxCapacity: 100_000);
        const int writeCount = 1_000;

        var writeTask = Task.Run(() =>
        {
            for (int i = 0; i < writeCount; i++)
            {
                buffer.Record(TestFixtures.CreateSignal(
                    timestamp: _clock.UtcNow.AddMilliseconds(i)));
            }
        });

        var readTask = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                // Should never throw
                _ = buffer.GetSignals(TimeSpan.FromMinutes(5));
                _ = buffer.Count;
            }
        });

        await Task.WhenAll(writeTask, readTask);

        buffer.Count.Should().Be(writeCount);
    }

    [Fact]
    public async Task Thread_safety_parallel_writes_do_not_lose_signals()
    {
        var buffer = CreateBuffer(maxCapacity: 100_000);
        const int writersCount = 4;
        const int signalsPerWriter = 500;

        var tasks = Enumerable.Range(0, writersCount).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < signalsPerWriter; i++)
            {
                buffer.Record(TestFixtures.CreateSignal(
                    timestamp: _clock.UtcNow.AddMilliseconds(w * 1000 + i)));
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        buffer.Count.Should().Be(writersCount * signalsPerWriter);
    }

    [Fact]
    public void GetSignals_returns_signals_sorted_by_timestamp()
    {
        var buffer = CreateBuffer();

        // Record out of order
        buffer.Record(TestFixtures.CreateSignal(timestamp: _clock.UtcNow.AddSeconds(3)));
        buffer.Record(TestFixtures.CreateSignal(timestamp: _clock.UtcNow.AddSeconds(1)));
        buffer.Record(TestFixtures.CreateSignal(timestamp: _clock.UtcNow.AddSeconds(2)));

        var result = buffer.GetSignals(TimeSpan.FromMinutes(5));

        result.Should().HaveCount(3);
        result.Should().BeInAscendingOrder(s => s.Timestamp);
    }

    [Fact]
    public void Constructor_throws_on_invalid_capacity()
    {
        var act = () => new SignalBuffer(_clock, maxCapacity: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

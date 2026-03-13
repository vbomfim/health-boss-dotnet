using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using HealthBoss.Core.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HealthBoss.Core.Tests;

/// <summary>
/// Tests for <see cref="EventSinkDispatcher"/> covering:
/// AC48: HealthEvent dispatched on state transition
/// AC49: Event sink failure doesn't block health evaluation
/// AC50: Multiple sinks receive same event (fan-out)
/// AC51: Event sink rate-limited
/// </summary>
public sealed class EventSinkDispatcherTests
{
    private static readonly DependencyId TestDep = new("dispatcher-dep");
    private static readonly TenantId TestTenant = new("dispatcher-tenant");

    private static readonly HealthEvent SampleHealthEvent = new(
        TestDep, HealthState.Healthy, HealthState.Degraded, TestFixtures.BaseTime);

    private static readonly TenantHealthEvent SampleTenantEvent = new(
        TestDep, TestTenant,
        TenantHealthStatus.Healthy, TenantHealthStatus.Degraded,
        SuccessRate: 0.75, OccurredAt: TestFixtures.BaseTime);

    private static readonly EventSinkDispatcherOptions DefaultOptions = new();

    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ISystemClock _clock;

    public EventSinkDispatcherTests()
    {
        _timeProvider.SetUtcNow(TestFixtures.BaseTime);
        _clock = new SystemClock(_timeProvider);
    }

    private EventSinkDispatcher CreateDispatcher(
        IReadOnlyList<IHealthEventSink> sinks,
        EventSinkDispatcherOptions? options = null,
        ILogger<EventSinkDispatcher>? logger = null) =>
        new(sinks, options ?? DefaultOptions, _clock, logger);

    // ───────────────────────────────────────────────────────────────
    // AC48: HealthEvent dispatched on state transition
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_SingleSink_ReceivesHealthEvent()
    {
        var sink = new FakeHealthEventSink();
        var dispatcher = CreateDispatcher([sink]);

        await dispatcher.DispatchAsync(SampleHealthEvent);

        sink.HealthEvents.Should().ContainSingle()
            .Which.Should().Be(SampleHealthEvent);
    }

    [Fact]
    public async Task DispatchTenantEventAsync_SingleSink_ReceivesTenantEvent()
    {
        var sink = new FakeHealthEventSink();
        var dispatcher = CreateDispatcher([sink]);

        await dispatcher.DispatchTenantEventAsync(SampleTenantEvent);

        sink.Events.Should().ContainSingle()
            .Which.Should().Be(SampleTenantEvent);
    }

    // ───────────────────────────────────────────────────────────────
    // AC50: Multiple sinks receive same event (fan-out)
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_MultipleSinks_AllReceiveHealthEvent()
    {
        var sink1 = new FakeHealthEventSink();
        var sink2 = new FakeHealthEventSink();
        var sink3 = new FakeHealthEventSink();
        var dispatcher = CreateDispatcher([sink1, sink2, sink3]);

        await dispatcher.DispatchAsync(SampleHealthEvent);

        sink1.HealthEvents.Should().ContainSingle().Which.Should().Be(SampleHealthEvent);
        sink2.HealthEvents.Should().ContainSingle().Which.Should().Be(SampleHealthEvent);
        sink3.HealthEvents.Should().ContainSingle().Which.Should().Be(SampleHealthEvent);
    }

    [Fact]
    public async Task DispatchTenantEventAsync_MultipleSinks_AllReceiveTenantEvent()
    {
        var sink1 = new FakeHealthEventSink();
        var sink2 = new FakeHealthEventSink();
        var dispatcher = CreateDispatcher([sink1, sink2]);

        await dispatcher.DispatchTenantEventAsync(SampleTenantEvent);

        sink1.Events.Should().ContainSingle().Which.Should().Be(SampleTenantEvent);
        sink2.Events.Should().ContainSingle().Which.Should().Be(SampleTenantEvent);
    }

    // ───────────────────────────────────────────────────────────────
    // AC49: Event sink failure doesn't block other sinks
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_FailingSink_DoesNotBlockOtherSinks()
    {
        var goodSink = new FakeHealthEventSink();
        var failingSink = new ThrowingEventSink();
        var dispatcher = CreateDispatcher([failingSink, goodSink]);

        await dispatcher.DispatchAsync(SampleHealthEvent);

        goodSink.HealthEvents.Should().ContainSingle();
    }

    [Fact]
    public async Task DispatchTenantEventAsync_FailingSink_DoesNotBlockOtherSinks()
    {
        var goodSink = new FakeHealthEventSink();
        var failingSink = new ThrowingEventSink();
        var dispatcher = CreateDispatcher([goodSink, failingSink]);

        await dispatcher.DispatchTenantEventAsync(SampleTenantEvent);

        goodSink.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task DispatchAsync_FailingSink_LoggedAtWarning()
    {
        var failingSink = new ThrowingEventSink();
        var logger = new RecordingLogger<EventSinkDispatcher>();
        var dispatcher = CreateDispatcher([failingSink], logger: logger);

        await dispatcher.DispatchAsync(SampleHealthEvent);

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    // ───────────────────────────────────────────────────────────────
    // AC51: Event sink rate-limited
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_RateLimitExceeded_EventsDropped()
    {
        var sink = new FakeHealthEventSink();
        var options = new EventSinkDispatcherOptions(MaxEventsPerSecondPerSink: 3);
        var dispatcher = CreateDispatcher([sink], options);

        // Dispatch 5 events in the same clock tick (same second window)
        for (int i = 0; i < 5; i++)
        {
            await dispatcher.DispatchAsync(SampleHealthEvent);
        }

        // Only 3 should pass the rate limiter
        sink.HealthEvents.Should().HaveCount(3);
    }

    [Fact]
    public async Task DispatchTenantEventAsync_RateLimitExceeded_EventsDropped()
    {
        var sink = new FakeHealthEventSink();
        var options = new EventSinkDispatcherOptions(MaxEventsPerSecondPerSink: 2);
        var dispatcher = CreateDispatcher([sink], options);

        for (int i = 0; i < 4; i++)
        {
            await dispatcher.DispatchTenantEventAsync(SampleTenantEvent);
        }

        sink.Events.Should().HaveCount(2);
    }

    [Fact]
    public async Task DispatchAsync_RateLimitResets_AfterWindowExpires()
    {
        var sink = new FakeHealthEventSink();
        var options = new EventSinkDispatcherOptions(MaxEventsPerSecondPerSink: 2);
        var dispatcher = CreateDispatcher([sink], options);

        // Use up rate limit
        await dispatcher.DispatchAsync(SampleHealthEvent);
        await dispatcher.DispatchAsync(SampleHealthEvent);
        await dispatcher.DispatchAsync(SampleHealthEvent); // Dropped

        // Advance clock past 1-second window
        _timeProvider.Advance(TimeSpan.FromSeconds(1.1));

        await dispatcher.DispatchAsync(SampleHealthEvent);
        await dispatcher.DispatchAsync(SampleHealthEvent);

        // 2 from first window + 2 from second window = 4
        sink.HealthEvents.Should().HaveCount(4);
    }

    [Fact]
    public async Task DispatchAsync_RateLimitPerSink_IndependentLimits()
    {
        var sink1 = new FakeHealthEventSink();
        var sink2 = new FakeHealthEventSink();
        var options = new EventSinkDispatcherOptions(MaxEventsPerSecondPerSink: 1);
        var dispatcher = CreateDispatcher([sink1, sink2], options);

        // Dispatch 2 events — each sink's limit is 1
        await dispatcher.DispatchAsync(SampleHealthEvent);
        await dispatcher.DispatchAsync(SampleHealthEvent);

        // Each sink independently limited: sink1 gets 1, sink2 gets 1
        sink1.HealthEvents.Should().HaveCount(1);
        sink2.HealthEvents.Should().HaveCount(1);
    }

    [Fact]
    public async Task DispatchAsync_RateLimitDropped_LogsWarning()
    {
        var sink = new FakeHealthEventSink();
        var options = new EventSinkDispatcherOptions(MaxEventsPerSecondPerSink: 1);
        var logger = new RecordingLogger<EventSinkDispatcher>();
        var dispatcher = CreateDispatcher([sink], options, logger);

        await dispatcher.DispatchAsync(SampleHealthEvent);
        await dispatcher.DispatchAsync(SampleHealthEvent); // Should be dropped

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Dispatch_CrossEventType_SharedRateBudget_DropsExcess()
    {
        // Verifies that health events and tenant events share the same per-sink
        // rate limit budget within a single window.
        var sink = new FakeHealthEventSink();
        var options = new EventSinkDispatcherOptions(MaxEventsPerSecondPerSink: 3);
        var dispatcher = CreateDispatcher([sink], options);

        // Exhaust the budget with health events
        for (int i = 0; i < 3; i++)
        {
            await dispatcher.DispatchAsync(SampleHealthEvent);
        }

        // Tenant event in the same window should be dropped (budget exhausted)
        await dispatcher.DispatchTenantEventAsync(SampleTenantEvent);

        sink.HealthEvents.Should().HaveCount(3);
        sink.Events.Should().BeEmpty("tenant event should be dropped — shared budget exhausted");
    }

    // ───────────────────────────────────────────────────────────────
    // Timeout: slow sink doesn't block dispatch
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_SlowSink_TimesOut_OtherSinksComplete()
    {
        var fastSink = new FakeHealthEventSink();
        var slowSink = new DelayingEventSink(TimeSpan.FromSeconds(30));
        var options = new EventSinkDispatcherOptions(SinkTimeout: TimeSpan.FromMilliseconds(50));
        var dispatcher = CreateDispatcher([slowSink, fastSink], options);

        await dispatcher.DispatchAsync(SampleHealthEvent);

        fastSink.HealthEvents.Should().ContainSingle();
    }

    [Fact]
    public async Task DispatchAsync_SlowSink_TimedOut_LogsWarning()
    {
        var slowSink = new DelayingEventSink(TimeSpan.FromSeconds(30));
        var options = new EventSinkDispatcherOptions(SinkTimeout: TimeSpan.FromMilliseconds(50));
        var logger = new RecordingLogger<EventSinkDispatcher>();
        var dispatcher = CreateDispatcher([slowSink], options, logger);

        await dispatcher.DispatchAsync(SampleHealthEvent);

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    // ───────────────────────────────────────────────────────────────
    // IHealthEventSink delegation
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnHealthStateChanged_DelegatesToDispatchAsync()
    {
        var sink = new FakeHealthEventSink();
        IHealthEventSink dispatcher = CreateDispatcher([sink]);

        await dispatcher.OnHealthStateChanged(SampleHealthEvent);

        sink.HealthEvents.Should().ContainSingle();
    }

    [Fact]
    public async Task OnTenantHealthChanged_DelegatesToDispatchTenantEventAsync()
    {
        var sink = new FakeHealthEventSink();
        IHealthEventSink dispatcher = CreateDispatcher([sink]);

        await dispatcher.OnTenantHealthChanged(SampleTenantEvent);

        sink.Events.Should().ContainSingle();
    }

    // ───────────────────────────────────────────────────────────────
    // Thread safety: concurrent dispatch
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_ConcurrentDispatch_AllEventsDelivered()
    {
        var sink = new FakeHealthEventSink();
        var options = new EventSinkDispatcherOptions(MaxEventsPerSecondPerSink: 10_000);
        var dispatcher = CreateDispatcher([sink], options);
        const int concurrency = 50;

        var tasks = Enumerable.Range(0, concurrency)
            .Select(i => dispatcher.DispatchAsync(
                SampleHealthEvent with { OccurredAt = TestFixtures.BaseTime.AddSeconds(i) }));

        await Task.WhenAll(tasks);

        sink.HealthEvents.Should().HaveCount(concurrency);
    }

    // ───────────────────────────────────────────────────────────────
    // Null guards and edge cases
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_NullEvent_ThrowsArgumentNullException()
    {
        var dispatcher = CreateDispatcher([new FakeHealthEventSink()]);

        Func<Task> act = () => dispatcher.DispatchAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task DispatchTenantEventAsync_NullEvent_ThrowsArgumentNullException()
    {
        var dispatcher = CreateDispatcher([new FakeHealthEventSink()]);

        Func<Task> act = () => dispatcher.DispatchTenantEventAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullSinks_ThrowsArgumentNullException()
    {
        Action act = () => new EventSinkDispatcher(null!, DefaultOptions, _clock);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        Action act = () => new EventSinkDispatcher([], null!, _clock);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullClock_ThrowsArgumentNullException()
    {
        Action act = () => new EventSinkDispatcher([], DefaultOptions, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ZeroRateLimit_ThrowsArgumentOutOfRangeException()
    {
        var options = new EventSinkDispatcherOptions(MaxEventsPerSecondPerSink: 0);

        Action act = () => new EventSinkDispatcher([], options, _clock);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task DispatchAsync_NoSinks_CompletesSuccessfully()
    {
        var dispatcher = CreateDispatcher([]);

        await dispatcher.DispatchAsync(SampleHealthEvent);
        // Should not throw — just a no-op
    }

    [Fact]
    public async Task DispatchAsync_CancellationRequested_Propagates()
    {
        var slowSink = new DelayingEventSink(TimeSpan.FromSeconds(30));
        var options = new EventSinkDispatcherOptions(SinkTimeout: TimeSpan.FromSeconds(30));
        var dispatcher = CreateDispatcher([slowSink], options);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => dispatcher.DispatchAsync(SampleHealthEvent, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ───────────────────────────────────────────────────────────────
    // Test doubles
    // ───────────────────────────────────────────────────────────────

    /// <summary>Sink that always throws to test error isolation.</summary>
    private sealed class ThrowingEventSink : IHealthEventSink
    {
        public Task OnHealthStateChanged(HealthEvent healthEvent, CancellationToken ct = default)
            => throw new InvalidOperationException("Sink failure");

        public Task OnTenantHealthChanged(TenantHealthEvent tenantEvent, CancellationToken ct = default)
            => throw new InvalidOperationException("Sink failure");
    }

    /// <summary>Sink that delays to test timeout behavior.</summary>
    private sealed class DelayingEventSink : IHealthEventSink
    {
        private readonly TimeSpan _delay;

        public DelayingEventSink(TimeSpan delay) => _delay = delay;

        public async Task OnHealthStateChanged(HealthEvent healthEvent, CancellationToken ct = default)
            => await Task.Delay(_delay, ct);

        public async Task OnTenantHealthChanged(TenantHealthEvent tenantEvent, CancellationToken ct = default)
            => await Task.Delay(_delay, ct);
    }

    /// <summary>Logger that records entries for assertion.</summary>
    internal sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];
        private readonly object _lock = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_lock)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lock)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}

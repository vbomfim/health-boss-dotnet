using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.Logging;
using static HealthBoss.Core.Tests.EventSinkDispatcherTests;

namespace HealthBoss.Core.Tests;

/// <summary>
/// Tests for <see cref="StructuredLogEventSink"/> covering:
/// - Correct log level for tenant status changes (Info/Warning/Error)
/// - Correct log level for state transitions (Info)
/// - Null guard
/// </summary>
public sealed class StructuredLogEventSinkTests
{
    private static readonly DependencyId TestDep = new("log-sink-dep");
    private static readonly TenantId TestTenant = new("log-sink-tenant");

    // ───────────────────────────────────────────────────────────────
    // OnTenantHealthChanged — log levels by NewStatus
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnTenantHealthChanged_NewStatusHealthy_LogsInformation()
    {
        var logger = new RecordingLogger<StructuredLogEventSink>();
        var sink = new StructuredLogEventSink(logger);
        var evt = CreateTenantEvent(TenantHealthStatus.Degraded, TenantHealthStatus.Healthy);

        await sink.OnTenantHealthChanged(evt);

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information);
    }

    [Fact]
    public async Task OnTenantHealthChanged_NewStatusDegraded_LogsWarning()
    {
        var logger = new RecordingLogger<StructuredLogEventSink>();
        var sink = new StructuredLogEventSink(logger);
        var evt = CreateTenantEvent(TenantHealthStatus.Healthy, TenantHealthStatus.Degraded);

        await sink.OnTenantHealthChanged(evt);

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task OnTenantHealthChanged_NewStatusUnavailable_LogsError()
    {
        var logger = new RecordingLogger<StructuredLogEventSink>();
        var sink = new StructuredLogEventSink(logger);
        var evt = CreateTenantEvent(TenantHealthStatus.Degraded, TenantHealthStatus.Unavailable);

        await sink.OnTenantHealthChanged(evt);

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task OnTenantHealthChanged_LogMessage_ContainsEventDetails()
    {
        var logger = new RecordingLogger<StructuredLogEventSink>();
        var sink = new StructuredLogEventSink(logger);
        var evt = CreateTenantEvent(TenantHealthStatus.Healthy, TenantHealthStatus.Degraded);

        await sink.OnTenantHealthChanged(evt);

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Message.Should().Contain("log-sink-dep");
        entry.Message.Should().Contain("log-sink-tenant");
        entry.Message.Should().Contain("Degraded");
    }

    // ───────────────────────────────────────────────────────────────
    // OnHealthStateChanged — state transitions
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnHealthStateChanged_AnyTransition_LogsInformation()
    {
        var logger = new RecordingLogger<StructuredLogEventSink>();
        var sink = new StructuredLogEventSink(logger);
        var evt = new HealthEvent(TestDep, HealthState.Healthy, HealthState.Degraded, TestFixtures.BaseTime);

        await sink.OnHealthStateChanged(evt);

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information);
    }

    [Fact]
    public async Task OnHealthStateChanged_CircuitOpen_LogsInformation()
    {
        var logger = new RecordingLogger<StructuredLogEventSink>();
        var sink = new StructuredLogEventSink(logger);
        var evt = new HealthEvent(TestDep, HealthState.Degraded, HealthState.CircuitOpen, TestFixtures.BaseTime);

        await sink.OnHealthStateChanged(evt);

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information);
    }

    [Fact]
    public async Task OnHealthStateChanged_LogMessage_ContainsEventDetails()
    {
        var logger = new RecordingLogger<StructuredLogEventSink>();
        var sink = new StructuredLogEventSink(logger);
        var evt = new HealthEvent(TestDep, HealthState.Healthy, HealthState.CircuitOpen, TestFixtures.BaseTime);

        await sink.OnHealthStateChanged(evt);

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Message.Should().Contain("log-sink-dep");
        entry.Message.Should().Contain("CircuitOpen");
    }

    // ───────────────────────────────────────────────────────────────
    // Null guards
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        Action act = () => new StructuredLogEventSink(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task OnHealthStateChanged_NullEvent_ThrowsArgumentNullException()
    {
        var sink = new StructuredLogEventSink(new RecordingLogger<StructuredLogEventSink>());

        Func<Task> act = () => sink.OnHealthStateChanged(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task OnTenantHealthChanged_NullEvent_ThrowsArgumentNullException()
    {
        var sink = new StructuredLogEventSink(new RecordingLogger<StructuredLogEventSink>());

        Func<Task> act = () => sink.OnTenantHealthChanged(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ───────────────────────────────────────────────────────────────
    // Helpers
    // ───────────────────────────────────────────────────────────────

    private static TenantHealthEvent CreateTenantEvent(
        TenantHealthStatus previous,
        TenantHealthStatus newStatus) =>
        new(TestDep, TestTenant, previous, newStatus, SuccessRate: 0.75, OccurredAt: TestFixtures.BaseTime);
}

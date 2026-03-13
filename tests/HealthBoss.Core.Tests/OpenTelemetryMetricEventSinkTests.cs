// <copyright file="OpenTelemetryMetricEventSinkTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Tests;

/// <summary>
/// Tests for <see cref="OpenTelemetryMetricEventSink"/> verifying:
/// - Delegation to <see cref="IStateMachineMetrics"/> on health state transitions
/// - Delegation to <see cref="ITenantMetrics"/> on tenant status changes
/// - Correct tag values forwarded to each metrics method
/// - Null guards
/// </summary>
public sealed class OpenTelemetryMetricEventSinkTests
{
    private static readonly DependencyId TestDep = new("otel-dep");
    private static readonly TenantId TestTenant = new("otel-tenant");

    private readonly FakeStateMachineMetrics _stateMachineMetrics = new();
    private readonly FakeTenantMetrics _tenantMetrics = new();
    private readonly OpenTelemetryMetricEventSink _sink;

    public OpenTelemetryMetricEventSinkTests()
    {
        _sink = new OpenTelemetryMetricEventSink(_stateMachineMetrics, _tenantMetrics);
    }

    // ───────────────────────────────────────────────────────────────
    // Health state transition → IStateMachineMetrics
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnHealthStateChanged_DelegatesToStateMachineMetrics()
    {
        var evt = CreateHealthEvent(HealthState.Healthy, HealthState.Degraded);

        await _sink.OnHealthStateChanged(evt);

        _stateMachineMetrics.StateTransitions.Should().ContainSingle();
    }

    [Fact]
    public async Task OnHealthStateChanged_PassesComponentFromDependencyId()
    {
        var evt = CreateHealthEvent(HealthState.Healthy, HealthState.CircuitOpen);

        await _sink.OnHealthStateChanged(evt);

        _stateMachineMetrics.StateTransitions.Single().Component
            .Should().Be(TestDep.ToString());
    }

    [Fact]
    public async Task OnHealthStateChanged_PassesFromState()
    {
        var evt = CreateHealthEvent(HealthState.Degraded, HealthState.Healthy);

        await _sink.OnHealthStateChanged(evt);

        _stateMachineMetrics.StateTransitions.Single().FromState
            .Should().Be("Degraded");
    }

    [Fact]
    public async Task OnHealthStateChanged_PassesToState()
    {
        var evt = CreateHealthEvent(HealthState.Healthy, HealthState.CircuitOpen);

        await _sink.OnHealthStateChanged(evt);

        _stateMachineMetrics.StateTransitions.Single().ToState
            .Should().Be("CircuitOpen");
    }

    [Fact]
    public async Task OnHealthStateChanged_MultipleEvents_DelegatesAll()
    {
        await _sink.OnHealthStateChanged(CreateHealthEvent(HealthState.Healthy, HealthState.Degraded));
        await _sink.OnHealthStateChanged(CreateHealthEvent(HealthState.Degraded, HealthState.CircuitOpen));

        _stateMachineMetrics.StateTransitions.Should().HaveCount(2);
    }

    // ───────────────────────────────────────────────────────────────
    // Tenant status change → ITenantMetrics
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnTenantHealthChanged_DelegatesToTenantMetrics()
    {
        var evt = CreateTenantEvent(TenantHealthStatus.Healthy, TenantHealthStatus.Degraded);

        await _sink.OnTenantHealthChanged(evt);

        _tenantMetrics.StatusChanges.Should().ContainSingle();
    }

    [Fact]
    public async Task OnTenantHealthChanged_PassesComponent()
    {
        var evt = CreateTenantEvent(TenantHealthStatus.Healthy, TenantHealthStatus.Unavailable);

        await _sink.OnTenantHealthChanged(evt);

        _tenantMetrics.StatusChanges.Single().Component
            .Should().Be(TestDep.ToString());
    }

    [Fact]
    public async Task OnTenantHealthChanged_PassesTenantId()
    {
        var evt = CreateTenantEvent(TenantHealthStatus.Healthy, TenantHealthStatus.Degraded);

        await _sink.OnTenantHealthChanged(evt);

        _tenantMetrics.StatusChanges.Single().TenantId
            .Should().Be(TestTenant.ToString());
    }

    [Fact]
    public async Task OnTenantHealthChanged_PassesFromStatus()
    {
        var evt = CreateTenantEvent(TenantHealthStatus.Degraded, TenantHealthStatus.Unavailable);

        await _sink.OnTenantHealthChanged(evt);

        _tenantMetrics.StatusChanges.Single().FromStatus
            .Should().Be("Degraded");
    }

    [Fact]
    public async Task OnTenantHealthChanged_PassesToStatus()
    {
        var evt = CreateTenantEvent(TenantHealthStatus.Healthy, TenantHealthStatus.Unavailable);

        await _sink.OnTenantHealthChanged(evt);

        _tenantMetrics.StatusChanges.Single().ToStatus
            .Should().Be("Unavailable");
    }

    [Fact]
    public async Task OnTenantHealthChanged_MultipleEvents_DelegatesAll()
    {
        await _sink.OnTenantHealthChanged(CreateTenantEvent(TenantHealthStatus.Healthy, TenantHealthStatus.Degraded));
        await _sink.OnTenantHealthChanged(CreateTenantEvent(TenantHealthStatus.Degraded, TenantHealthStatus.Unavailable));

        _tenantMetrics.StatusChanges.Should().HaveCount(2);
    }

    // ───────────────────────────────────────────────────────────────
    // Null guards
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnHealthStateChanged_NullEvent_ThrowsArgumentNullException()
    {
        Func<Task> act = () => _sink.OnHealthStateChanged(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task OnTenantHealthChanged_NullEvent_ThrowsArgumentNullException()
    {
        Func<Task> act = () => _sink.OnTenantHealthChanged(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullStateMachineMetrics_ThrowsArgumentNullException()
    {
        Action act = () => new OpenTelemetryMetricEventSink(null!, _tenantMetrics);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("stateMachineMetrics");
    }

    [Fact]
    public void Constructor_NullTenantMetrics_ThrowsArgumentNullException()
    {
        Action act = () => new OpenTelemetryMetricEventSink(_stateMachineMetrics, null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("tenantMetrics");
    }

    // ───────────────────────────────────────────────────────────────
    // Cross-delegation isolation
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnHealthStateChanged_DoesNotDelegateToTenantMetrics()
    {
        await _sink.OnHealthStateChanged(CreateHealthEvent(HealthState.Healthy, HealthState.Degraded));

        _tenantMetrics.StatusChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task OnTenantHealthChanged_DoesNotDelegateToStateMachineMetrics()
    {
        await _sink.OnTenantHealthChanged(CreateTenantEvent(TenantHealthStatus.Healthy, TenantHealthStatus.Degraded));

        _stateMachineMetrics.StateTransitions.Should().BeEmpty();
    }

    // ───────────────────────────────────────────────────────────────
    // Helpers
    // ───────────────────────────────────────────────────────────────

    private static HealthEvent CreateHealthEvent(HealthState from, HealthState to)
        => new(TestDep, from, to, TestFixtures.BaseTime);

    private static TenantHealthEvent CreateTenantEvent(
        TenantHealthStatus from,
        TenantHealthStatus to)
        => new(TestDep, TestTenant, from, to, SuccessRate: 0.85, OccurredAt: TestFixtures.BaseTime);

    // ───────────────────────────────────────────────────────────────
    // Test doubles
    // ───────────────────────────────────────────────────────────────

    private sealed class FakeStateMachineMetrics : IStateMachineMetrics
    {
        public List<(string Component, string FromState, string ToState)> StateTransitions { get; } = [];

        public void RecordStateTransition(string component, string fromState, string toState)
            => StateTransitions.Add((component, fromState, toState));

        public void RecordRecoveryProbeAttempt(string component) { }
        public void RecordRecoveryProbeSuccess(string component) { }
        public void RecordEventSinkDispatch() { }
        public void RecordEventSinkFailure(string sinkType) { }
        public void RecordShutdownGateEvaluation(string gate, bool approved) { }
    }

    private sealed class FakeTenantMetrics : ITenantMetrics
    {
        public List<(string Component, string TenantId, string FromStatus, string ToStatus)> StatusChanges { get; } = [];

        public void RecordTenantStatusChange(string component, string tenantId, string fromStatus, string toStatus)
            => StatusChanges.Add((component, tenantId, fromStatus, toStatus));

        public void SetTenantCount(string component, int count) { }
    }
}

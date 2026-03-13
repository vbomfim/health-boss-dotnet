// <copyright file="PollyStateAdapterTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;
using HealthBoss.Core.Contracts;
using Polly.CircuitBreaker;

namespace HealthBoss.Polly.Tests;

public sealed class PollyStateAdapterTests
{
    private readonly FakeClock _clock = new();
    private readonly DependencyId _depId = DependencyId.Create("payments-api");

    [Fact]
    public void Closed_state_maps_to_success_signal()
    {
        var signal = PollyStateAdapter.ToSignal(CircuitState.Closed, _depId, _clock);

        signal.Outcome.Should().Be(SignalOutcome.Success);
        signal.Metadata.Should().Be("Circuit closed");
        signal.DependencyId.Should().Be(_depId);
        signal.Timestamp.Should().Be(_clock.UtcNow);
    }

    [Fact]
    public void Open_state_maps_to_failure_signal()
    {
        var signal = PollyStateAdapter.ToSignal(CircuitState.Open, _depId, _clock);

        signal.Outcome.Should().Be(SignalOutcome.Failure);
        signal.Metadata.Should().Be("Circuit opened");
        signal.DependencyId.Should().Be(_depId);
    }

    [Fact]
    public void HalfOpen_state_maps_to_success_signal()
    {
        var signal = PollyStateAdapter.ToSignal(CircuitState.HalfOpen, _depId, _clock);

        signal.Outcome.Should().Be(SignalOutcome.Success);
        signal.Metadata.Should().Be("Circuit half-open");
        signal.DependencyId.Should().Be(_depId);
    }

    [Fact]
    public void Isolated_state_maps_to_failure_signal()
    {
        var signal = PollyStateAdapter.ToSignal(CircuitState.Isolated, _depId, _clock);

        signal.Outcome.Should().Be(SignalOutcome.Failure);
        signal.Metadata.Should().Be("Circuit isolated");
        signal.DependencyId.Should().Be(_depId);
    }

    [Fact]
    public void Signal_uses_clock_timestamp()
    {
        var fixedTime = new DateTimeOffset(2025, 6, 1, 10, 30, 0, TimeSpan.Zero);
        _clock.UtcNow = fixedTime;

        var signal = PollyStateAdapter.ToSignal(CircuitState.Open, _depId, _clock);

        signal.Timestamp.Should().Be(fixedTime);
    }

    [Fact]
    public void Signal_has_no_latency()
    {
        var signal = PollyStateAdapter.ToSignal(CircuitState.Open, _depId, _clock);

        signal.Latency.Should().BeNull();
    }

    [Fact]
    public void Signal_has_zero_http_status_code()
    {
        var signal = PollyStateAdapter.ToSignal(CircuitState.Closed, _depId, _clock);

        signal.HttpStatusCode.Should().Be(0);
    }

    [Fact]
    public void Signal_has_no_grpc_status()
    {
        var signal = PollyStateAdapter.ToSignal(CircuitState.Closed, _depId, _clock);

        signal.GrpcStatus.Should().BeNull();
    }
}

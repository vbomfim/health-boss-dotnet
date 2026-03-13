// <copyright file="PollyHealthBossExtensionsTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;
using HealthBoss.Core;
using HealthBoss.Core.Contracts;
using Polly;
using Polly.CircuitBreaker;

namespace HealthBoss.Polly.Tests;

public sealed class PollyHealthBossExtensionsTests
{
    private readonly FakeSignalRecorder _recorder = new();
    private readonly FakeClock _clock = new();
    private readonly DependencyId _depId = DependencyId.Create("payments-api");

    [Fact]
    public void WithHealthBossTracking_null_recorder_throws_ArgumentNullException()
    {
        var options = new CircuitBreakerStrategyOptions();

        var act = () => options.WithHealthBossTracking(null!, _depId, _clock);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("recorder");
    }

    [Fact]
    public void WithHealthBossTracking_null_clock_throws_ArgumentNullException()
    {
        var options = new CircuitBreakerStrategyOptions();

        var act = () => options.WithHealthBossTracking(_recorder, _depId, null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("clock");
    }

    [Fact]
    public void WithHealthBossTracking_returns_same_options_for_chaining()
    {
        var options = new CircuitBreakerStrategyOptions();

        var result = options.WithHealthBossTracking(_recorder, _depId, _clock);

        result.Should().BeSameAs(options);
    }

    [Fact]
    public void OnOpened_callback_records_failure_signal()
    {
        var options = new CircuitBreakerStrategyOptions();
        options.WithHealthBossTracking(_recorder, _depId, _clock);

        // Simulate the callback
        var args = CreateOnOpenedArgs();
        options.OnOpened!.Invoke(args);

        _recorder.Signals.Should().ContainSingle();
        var signal = _recorder.Signals[0];
        signal.Outcome.Should().Be(SignalOutcome.Failure);
        signal.Metadata.Should().Be("Circuit opened");
        signal.DependencyId.Should().Be(_depId);
    }

    [Fact]
    public void OnClosed_callback_records_success_signal()
    {
        var options = new CircuitBreakerStrategyOptions();
        options.WithHealthBossTracking(_recorder, _depId, _clock);

        var args = CreateOnClosedArgs();
        options.OnClosed!.Invoke(args);

        _recorder.Signals.Should().ContainSingle();
        var signal = _recorder.Signals[0];
        signal.Outcome.Should().Be(SignalOutcome.Success);
        signal.Metadata.Should().Be("Circuit closed");
    }

    [Fact]
    public void OnHalfOpened_callback_records_success_signal()
    {
        var options = new CircuitBreakerStrategyOptions();
        options.WithHealthBossTracking(_recorder, _depId, _clock);

        var args = CreateOnHalfOpenedArgs();
        options.OnHalfOpened!.Invoke(args);

        _recorder.Signals.Should().ContainSingle();
        var signal = _recorder.Signals[0];
        signal.Outcome.Should().Be(SignalOutcome.Success);
        signal.Metadata.Should().Be("Circuit half-open");
    }

    [Fact]
    public void Multiple_dependencies_produce_isolated_signals()
    {
        var depA = DependencyId.Create("service-a");
        var depB = DependencyId.Create("service-b");
        var recorderA = new FakeSignalRecorder();
        var recorderB = new FakeSignalRecorder();

        var optionsA = new CircuitBreakerStrategyOptions();
        optionsA.WithHealthBossTracking(recorderA, depA, _clock);

        var optionsB = new CircuitBreakerStrategyOptions();
        optionsB.WithHealthBossTracking(recorderB, depB, _clock);

        // Trigger open on both
        optionsA.OnOpened!.Invoke(CreateOnOpenedArgs());
        optionsB.OnOpened!.Invoke(CreateOnOpenedArgs());

        recorderA.Signals.Should().ContainSingle()
            .Which.DependencyId.Should().Be(depA);
        recorderB.Signals.Should().ContainSingle()
            .Which.DependencyId.Should().Be(depB);
    }

    [Fact]
    public void WithHealthBossTracking_preserves_existing_OnOpened_callback()
    {
        bool existingCalled = false;
        var options = new CircuitBreakerStrategyOptions
        {
            OnOpened = _ =>
            {
                existingCalled = true;
                return default;
            },
        };

        options.WithHealthBossTracking(_recorder, _depId, _clock);
        options.OnOpened!.Invoke(CreateOnOpenedArgs());

        existingCalled.Should().BeTrue("existing callback must still fire");
        _recorder.Signals.Should().ContainSingle("HealthBoss tracking must also fire");
    }

    [Fact]
    public void WithHealthBossTracking_preserves_existing_OnClosed_callback()
    {
        bool existingCalled = false;
        var options = new CircuitBreakerStrategyOptions
        {
            OnClosed = _ =>
            {
                existingCalled = true;
                return default;
            },
        };

        options.WithHealthBossTracking(_recorder, _depId, _clock);
        options.OnClosed!.Invoke(CreateOnClosedArgs());

        existingCalled.Should().BeTrue();
        _recorder.Signals.Should().ContainSingle();
    }

    [Fact]
    public void WithHealthBossTracking_preserves_existing_OnHalfOpened_callback()
    {
        bool existingCalled = false;
        var options = new CircuitBreakerStrategyOptions
        {
            OnHalfOpened = _ =>
            {
                existingCalled = true;
                return default;
            },
        };

        options.WithHealthBossTracking(_recorder, _depId, _clock);
        options.OnHalfOpened!.Invoke(CreateOnHalfOpenedArgs());

        existingCalled.Should().BeTrue();
        _recorder.Signals.Should().ContainSingle();
    }

    [Fact]
    public async Task Integration_circuit_break_records_failure_signal()
    {
        // Full integration: create a real Polly pipeline, trigger a circuit break
        var options = new CircuitBreakerStrategyOptions
        {
            FailureRatio = 1.0,
            MinimumThroughput = 2,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(5),
        };
        options.WithHealthBossTracking(_recorder, _depId, _clock);

        var pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(options)
            .Build();

        // Trigger enough failures to open the circuit
        for (int i = 0; i < 2; i++)
        {
            try
            {
                await pipeline.ExecuteAsync(_ => throw new InvalidOperationException("boom"));
            }
            catch (InvalidOperationException)
            {
                // expected — let failures accumulate
            }
        }

        // The circuit should now be open, which triggers the OnOpened callback
        _recorder.Signals.Should().Contain(s =>
            s.Outcome == SignalOutcome.Failure &&
            s.Metadata == "Circuit opened" &&
            s.DependencyId == _depId);
    }

    // --- Helpers to construct callback argument types ---

    private static OnCircuitOpenedArguments<object> CreateOnOpenedArgs()
    {
        return new OnCircuitOpenedArguments<object>(
            context: ResilienceContextPool.Shared.Get(),
            outcome: Outcome.FromResult<object>(null!),
            breakDuration: TimeSpan.FromSeconds(30),
            isManual: false);
    }

    private static OnCircuitClosedArguments<object> CreateOnClosedArgs()
    {
        return new OnCircuitClosedArguments<object>(
            context: ResilienceContextPool.Shared.Get(),
            outcome: Outcome.FromResult<object>(null!),
            isManual: false);
    }

    private static OnCircuitHalfOpenedArguments CreateOnHalfOpenedArgs()
    {
        return new OnCircuitHalfOpenedArguments(
            context: ResilienceContextPool.Shared.Get());
    }
}

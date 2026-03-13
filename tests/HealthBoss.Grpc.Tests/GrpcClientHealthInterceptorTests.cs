// <copyright file="GrpcClientHealthInterceptorTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;
using Grpc.Core;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Grpc.Tests;

public sealed class GrpcClientHealthInterceptorTests
{
    private readonly FakeSignalRecorder _recorder = new();
    private readonly FakeClock _clock = new();

    [Fact]
    public void Constructor_throws_when_recorder_is_null()
    {
        var act = () => new GrpcClientHealthInterceptor(null!, _clock, "grpc_backend_pool");

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("recorder");
    }

    [Fact]
    public void Constructor_throws_when_clock_is_null()
    {
        var act = () => new GrpcClientHealthInterceptor(_recorder, null!, "grpc_backend_pool");

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("clock");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_throws_when_componentName_is_invalid(string? name)
    {
        var act = () => new GrpcClientHealthInterceptor(_recorder, _clock, name!);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(StatusCode.OK, SignalOutcome.Success)]
    [InlineData(StatusCode.NotFound, SignalOutcome.Failure)]
    [InlineData(StatusCode.Unavailable, SignalOutcome.Failure)]
    [InlineData(StatusCode.Internal, SignalOutcome.Failure)]
    [InlineData(StatusCode.PermissionDenied, SignalOutcome.Failure)]
    [InlineData(StatusCode.Unauthenticated, SignalOutcome.Failure)]
    [InlineData(StatusCode.ResourceExhausted, SignalOutcome.Rejected)]
    [InlineData(StatusCode.DeadlineExceeded, SignalOutcome.Timeout)]
    [InlineData(StatusCode.Cancelled, SignalOutcome.Failure)]
    [InlineData(StatusCode.Unimplemented, SignalOutcome.Failure)]
    [InlineData(StatusCode.DataLoss, SignalOutcome.Failure)]
    [InlineData(StatusCode.Aborted, SignalOutcome.Failure)]
    [InlineData(StatusCode.FailedPrecondition, SignalOutcome.Failure)]
    [InlineData(StatusCode.OutOfRange, SignalOutcome.Failure)]
    [InlineData(StatusCode.AlreadyExists, SignalOutcome.Failure)]
    [InlineData(StatusCode.InvalidArgument, SignalOutcome.Failure)]
    [InlineData(StatusCode.Unknown, SignalOutcome.Failure)]
    public void MapStatusCode_maps_grpc_status_to_signal_outcome(
        StatusCode statusCode,
        SignalOutcome expectedOutcome)
    {
        var outcome = GrpcClientHealthInterceptor.MapStatusCode(statusCode);

        outcome.Should().Be(expectedOutcome);
    }

    [Fact]
    public void MapStatusCode_maps_OK_to_success()
    {
        GrpcClientHealthInterceptor.MapStatusCode(StatusCode.OK)
            .Should().Be(SignalOutcome.Success);
    }

    [Fact]
    public void MapStatusCode_maps_DeadlineExceeded_to_timeout()
    {
        GrpcClientHealthInterceptor.MapStatusCode(StatusCode.DeadlineExceeded)
            .Should().Be(SignalOutcome.Timeout);
    }

    [Fact]
    public void MapStatusCode_maps_ResourceExhausted_to_rejected()
    {
        GrpcClientHealthInterceptor.MapStatusCode(StatusCode.ResourceExhausted)
            .Should().Be(SignalOutcome.Rejected);
    }

    [Fact]
    public void MapStatusCode_maps_Unavailable_to_failure()
    {
        GrpcClientHealthInterceptor.MapStatusCode(StatusCode.Unavailable)
            .Should().Be(SignalOutcome.Failure);
    }

    [Fact]
    public void RecordCallResult_records_signal_with_correct_dependency()
    {
        var interceptor = CreateInterceptor();
        var start = _clock.UtcNow;
        _clock.UtcNow = start.AddMilliseconds(150);

        interceptor.RecordCallResult(StatusCode.OK, start);

        _recorder.Signals.Should().ContainSingle();
        var signal = _recorder.Signals[0];
        signal.DependencyId.Value.Should().Be("grpc_backend_pool");
    }

    [Fact]
    public void RecordCallResult_records_signal_with_correct_outcome()
    {
        var interceptor = CreateInterceptor();
        var start = _clock.UtcNow;
        _clock.UtcNow = start.AddMilliseconds(100);

        interceptor.RecordCallResult(StatusCode.Unavailable, start);

        _recorder.Signals.Should().ContainSingle();
        _recorder.Signals[0].Outcome.Should().Be(SignalOutcome.Failure);
    }

    [Fact]
    public void RecordCallResult_records_latency()
    {
        var interceptor = CreateInterceptor();
        var start = _clock.UtcNow;
        _clock.UtcNow = start.AddMilliseconds(250);

        interceptor.RecordCallResult(StatusCode.OK, start);

        _recorder.Signals[0].Latency.Should().Be(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void RecordCallResult_records_sanitized_grpc_status()
    {
        var interceptor = CreateInterceptor();
        var start = _clock.UtcNow;
        _clock.UtcNow = start.AddMilliseconds(50);

        interceptor.RecordCallResult(StatusCode.OK, start);

        _recorder.Signals[0].GrpcStatus.Should().Be("OK");
    }

    [Fact]
    public void RecordCallResult_uses_status_code_name_not_raw_error_details()
    {
        var interceptor = CreateInterceptor();
        var start = _clock.UtcNow;
        _clock.UtcNow = start.AddMilliseconds(50);

        interceptor.RecordCallResult(StatusCode.Unavailable, start);

        // Must use sanitized status code name, never raw error detail strings
        _recorder.Signals[0].GrpcStatus.Should().Be("Unavailable");
        _recorder.Signals[0].GrpcStatus.Should().NotContain("connection refused");
    }

    [Fact]
    public void RecordCallResult_records_timestamp_from_clock()
    {
        var fixedTime = new DateTimeOffset(2025, 7, 1, 14, 0, 0, TimeSpan.Zero);
        _clock.UtcNow = fixedTime;
        var interceptor = CreateInterceptor();
        var start = fixedTime.AddMilliseconds(-100);
        _clock.UtcNow = fixedTime;

        interceptor.RecordCallResult(StatusCode.OK, start);

        _recorder.Signals[0].Timestamp.Should().Be(fixedTime);
    }

    [Fact]
    public void RecordCallResult_records_zero_http_status()
    {
        var interceptor = CreateInterceptor();

        interceptor.RecordCallResult(StatusCode.OK, _clock.UtcNow);

        _recorder.Signals[0].HttpStatusCode.Should().Be(0);
    }

    [Fact]
    public async Task RecordCallResult_is_thread_safe_under_concurrent_calls()
    {
        var interceptor = CreateInterceptor();
        const int callCount = 100;

        var tasks = Enumerable.Range(0, callCount)
            .Select(_ => Task.Run(() =>
            {
                interceptor.RecordCallResult(StatusCode.OK, _clock.UtcNow);
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        _recorder.Signals.Should().HaveCount(callCount);
    }

    private GrpcClientHealthInterceptor CreateInterceptor(string componentName = "grpc_backend_pool") =>
        new(_recorder, _clock, componentName);
}

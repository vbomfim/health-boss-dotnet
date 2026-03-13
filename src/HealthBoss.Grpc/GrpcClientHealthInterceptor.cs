// <copyright file="GrpcClientHealthInterceptor.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using Grpc.Core;
using Grpc.Core.Interceptors;
using HealthBoss.Core;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Grpc;

/// <summary>
/// A gRPC client interceptor that records call-level success/failure signals
/// for HealthBoss health evaluation.
/// </summary>
/// <remarks>
/// <para>
/// Intercepts unary gRPC calls to record a
/// <see cref="HealthSignal"/> on completion. The gRPC <see cref="StatusCode"/>
/// is mapped to a <see cref="SignalOutcome"/> using <see cref="MapStatusCode"/>.
/// </para>
/// <para>
/// The <see cref="HealthSignal.GrpcStatus"/> field uses the sanitized status code name
/// (e.g., "OK", "Unavailable") — never raw error details — to prevent information
/// disclosure (Security Finding #9).
/// </para>
/// <para>
/// This class is thread-safe. Signal recording is delegated to the
/// <see cref="ISignalRecorder"/> which guarantees O(1) lock-free writes.
/// </para>
/// </remarks>
// TODO: Sprint 6+ — add client-streaming, server-streaming, and duplex interceptors
public sealed class GrpcClientHealthInterceptor : Interceptor
{
    private readonly ISignalRecorder _recorder;
    private readonly ISystemClock _clock;
    private readonly DependencyId _dependencyId;

    /// <summary>
    /// Initializes a new instance of the <see cref="GrpcClientHealthInterceptor"/> class.
    /// </summary>
    /// <param name="recorder">The signal recorder to write health signals to.</param>
    /// <param name="clock">Clock used for timestamps and latency measurement.</param>
    /// <param name="componentName">The logical component name for the gRPC backend pool
    /// (e.g., "grpc_backend_pool"). Must be a valid dependency identifier.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="recorder"/> or <paramref name="clock"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="componentName"/> is not a valid dependency identifier.
    /// </exception>
    public GrpcClientHealthInterceptor(
        ISignalRecorder recorder,
        ISystemClock clock,
        string componentName)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(clock);
        _dependencyId = DependencyId.Create(componentName);

        _recorder = recorder;
        _clock = clock;
    }

    /// <summary>
    /// Intercepts a unary gRPC call to record its outcome as a health signal.
    /// </summary>
    /// <typeparam name="TRequest">The request message type.</typeparam>
    /// <typeparam name="TResponse">The response message type.</typeparam>
    /// <param name="request">The outgoing request message.</param>
    /// <param name="context">The client interceptor context.</param>
    /// <param name="continuation">The continuation to invoke the next interceptor or handler.</param>
    /// <returns>The gRPC call representing the async unary operation.</returns>
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var startTime = _clock.UtcNow;
        var call = continuation(request, context);

        var wrappedResponseAsync = WrapUnaryResponseAsync(call.ResponseAsync, startTime);

        return new AsyncUnaryCall<TResponse>(
            wrappedResponseAsync,
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    /// <summary>
    /// Records the result of a gRPC call as a health signal.
    /// Exposed internally for testability without requiring a full gRPC channel.
    /// </summary>
    /// <param name="statusCode">The gRPC status code of the completed call.</param>
    /// <param name="startTime">The time the call started.</param>
    internal void RecordCallResult(StatusCode statusCode, DateTimeOffset startTime)
    {
        var now = _clock.UtcNow;
        var outcome = MapStatusCode(statusCode);

        _recorder.Record(new HealthSignal(
            timestamp: now,
            dependencyId: _dependencyId,
            outcome: outcome,
            latency: now - startTime,
            grpcStatus: statusCode.ToString()));
    }

    /// <summary>
    /// Maps a gRPC <see cref="StatusCode"/> to a HealthBoss <see cref="SignalOutcome"/>.
    /// </summary>
    /// <param name="statusCode">The gRPC status code to map.</param>
    /// <returns>The corresponding signal outcome.</returns>
    /// <remarks>
    /// <list type="bullet">
    /// <item><see cref="StatusCode.OK"/> → <see cref="SignalOutcome.Success"/></item>
    /// <item><see cref="StatusCode.DeadlineExceeded"/> → <see cref="SignalOutcome.Timeout"/></item>
    /// <item><see cref="StatusCode.ResourceExhausted"/> → <see cref="SignalOutcome.Rejected"/></item>
    /// <item>All other codes → <see cref="SignalOutcome.Failure"/></item>
    /// </list>
    /// </remarks>
    public static SignalOutcome MapStatusCode(StatusCode statusCode) =>
        statusCode switch
        {
            StatusCode.OK => SignalOutcome.Success,
            StatusCode.DeadlineExceeded => SignalOutcome.Timeout,
            StatusCode.ResourceExhausted => SignalOutcome.Rejected,
            _ => SignalOutcome.Failure,
        };

    private async Task<TResponse> WrapUnaryResponseAsync<TResponse>(
        Task<TResponse> responseTask,
        DateTimeOffset startTime)
    {
        try
        {
            var response = await responseTask.ConfigureAwait(false);
            RecordCallResult(StatusCode.OK, startTime);
            return response;
        }
        catch (RpcException ex)
        {
            RecordCallResult(ex.StatusCode, startTime);
            throw;
        }
    }
}

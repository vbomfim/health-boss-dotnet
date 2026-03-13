// <copyright file="HealthBossDelegatingHandler.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core;
using HealthBoss.Core.Contracts;

namespace HealthBoss.AspNetCore;

/// <summary>
/// A <see cref="DelegatingHandler"/> that records outbound HTTP call results
/// as <see cref="HealthSignal"/> instances into an <see cref="ISignalBuffer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each outbound HTTP call produces exactly one health signal with the outcome,
/// latency, and HTTP status code. On success (default: status &lt; 500), a
/// <see cref="SignalOutcome.Success"/> signal is recorded. On server errors or
/// when the custom <see cref="OutboundTrackingOptions.IsFailure"/> predicate
/// returns <c>true</c>, a <see cref="SignalOutcome.Failure"/> signal is recorded.
/// </para>
/// <para>
/// Timeout exceptions (<see cref="TaskCanceledException"/> not caused by user
/// cancellation) produce a <see cref="SignalOutcome.Timeout"/> signal.
/// Connection failures (<see cref="HttpRequestException"/>) produce a
/// <see cref="SignalOutcome.Failure"/> signal. All exceptions are re-thrown
/// after recording.
/// </para>
/// </remarks>
public sealed class HealthBossDelegatingHandler : DelegatingHandler
{
    private readonly ISignalBuffer _signalBuffer;
    private readonly ISystemClock _clock;
    private readonly DependencyId _dependencyId;
    private readonly Func<HttpResponseMessage, bool> _isFailure;
    private readonly IComponentMetrics? _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="HealthBossDelegatingHandler"/> class.
    /// </summary>
    /// <param name="signalBuffer">The buffer to record health signals into.</param>
    /// <param name="clock">The clock used for timestamps and latency measurement.</param>
    /// <param name="options">The tracking options including component name and failure predicate.</param>
    /// <param name="metrics">Optional metrics recorder for outbound request tracking.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="signalBuffer"/>, <paramref name="clock"/>,
    /// or <paramref name="options"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <see cref="OutboundTrackingOptions.ComponentName"/> is not a valid dependency identifier.
    /// </exception>
    public HealthBossDelegatingHandler(
        ISignalBuffer signalBuffer,
        ISystemClock clock,
        OutboundTrackingOptions options,
        IComponentMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(signalBuffer);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

        _signalBuffer = signalBuffer;
        _clock = clock;
        _dependencyId = DependencyId.Create(options.ComponentName);
        _isFailure = options.IsFailure ?? DefaultIsFailure;
        _metrics = metrics;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var startTime = _clock.UtcNow;

        try
        {
            var response = await base.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            var outcome = _isFailure(response)
                ? SignalOutcome.Failure
                : SignalOutcome.Success;

            RecordSignal(startTime, outcome, (int)response.StatusCode);

            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RecordSignal(startTime, SignalOutcome.Timeout);
            throw;
        }
        catch (HttpRequestException)
        {
            RecordSignal(startTime, SignalOutcome.Failure);
            throw;
        }
    }

    private void RecordSignal(
        DateTimeOffset startTime,
        SignalOutcome outcome,
        int httpStatusCode = 0)
    {
        var now = _clock.UtcNow;

        _signalBuffer.Record(new HealthSignal(
            timestamp: now,
            dependencyId: _dependencyId,
            outcome: outcome,
            latency: now - startTime,
            httpStatusCode: httpStatusCode));

        _metrics?.RecordOutboundRequestDuration(_dependencyId.Value, (now - startTime).TotalSeconds);
    }

    private static bool DefaultIsFailure(HttpResponseMessage response) =>
        (int)response.StatusCode >= 500;
}

// <copyright file="OutboundHandlerEdgeCaseTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

// ──────────────────────────────────────────────────────────────────────────
// QA Guardian — Missing edge case tests for feature/outbound-handler.
//
// Target branch: feature/outbound-handler
// File location: tests/HealthBoss.AspNetCore.Tests/EdgeCases/OutboundHandlerEdgeCaseTests.cs
//
// Prerequisites: FakeSignalBuffer, FakeSystemClock, FakeHttpMessageHandler from
//   tests/HealthBoss.AspNetCore.Tests/Fakes/ on the outbound-handler branch.
//
// These tests cover gaps G03, G04, G05 from QA coverage analysis:
//   G03: DNS failure (HttpRequestException with inner SocketException)
//   G04: Connection reset (HttpRequestException with IOException)
//   G05: Outbound handler → signal buffer → evaluation integration
// ──────────────────────────────────────────────────────────────────────────

using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using HealthBoss.AspNetCore.Tests.Fakes;
using HealthBoss.Core.Contracts;

namespace HealthBoss.AspNetCore.Tests.EdgeCases;

/// <summary>
/// [EDGE] Edge case tests for HealthBossDelegatingHandler:
/// DNS failures, connection resets, socket exceptions, and
/// signal pipeline integration scenarios.
/// </summary>
public sealed class OutboundHandlerEdgeCaseTests : IDisposable
{
    private readonly FakeSignalBuffer _buffer = new();
    private readonly FakeSystemClock _clock = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            d.Dispose();
        }
    }

    private HttpMessageInvoker CreateInvoker(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        Action<OutboundTrackingOptions>? configure = null)
    {
        var options = new OutboundTrackingOptions { ComponentName = "test-dependency" };
        configure?.Invoke(options);

        var innerHandler = new FakeHttpMessageHandler(handler);
        var delegatingHandler = new HealthBossDelegatingHandler(_buffer, _clock, options)
        {
            InnerHandler = innerHandler,
        };

        var invoker = new HttpMessageInvoker(delegatingHandler);
        _disposables.Add(invoker);
        _disposables.Add(delegatingHandler);
        _disposables.Add(innerHandler);
        return invoker;
    }

    private static HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, "https://api.example.com/health");

    // ────────────────────────────────────────────────────────────
    // [EDGE] DNS Failure — HttpRequestException wrapping SocketException
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// [EDGE] DNS resolution failure produces a Failure signal with httpStatusCode=0.
    /// Real-world scenario: target hostname doesn't resolve.
    /// </summary>
    [Fact]
    public async Task SendAsync_dns_failure_records_failure_signal()
    {
        var invoker = CreateInvoker((_, _) =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            throw new HttpRequestException(
                "Name or service not known (api.example.com:443)",
                new SocketException((int)SocketError.HostNotFound));
        });

        var act = () => invoker.SendAsync(CreateRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        _buffer.Signals.Should().ContainSingle();
        _buffer.Signals[0].Outcome.Should().Be(SignalOutcome.Failure);
        _buffer.Signals[0].HttpStatusCode.Should().Be(0,
            "DNS failures have no HTTP status code");
        _buffer.Signals[0].Latency.Should().Be(TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// [EDGE] DNS failure with SocketError.HostUnreachable variant.
    /// </summary>
    [Fact]
    public async Task SendAsync_host_unreachable_records_failure_signal()
    {
        var invoker = CreateInvoker((_, _) =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(50));
            throw new HttpRequestException(
                "No route to host",
                new SocketException((int)SocketError.HostUnreachable));
        });

        var act = () => invoker.SendAsync(CreateRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        _buffer.Signals.Should().ContainSingle()
            .Which.Outcome.Should().Be(SignalOutcome.Failure);
    }

    // ────────────────────────────────────────────────────────────
    // [EDGE] Connection Reset — HttpRequestException wrapping IOException
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// [EDGE] Connection reset by peer (IOException inside HttpRequestException).
    /// Real-world scenario: server closes TCP connection abruptly.
    /// </summary>
    [Fact]
    public async Task SendAsync_connection_reset_records_failure_signal()
    {
        var invoker = CreateInvoker((_, _) =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(200));
            throw new HttpRequestException(
                "Connection reset by peer",
                new IOException("Unable to read data from the transport connection: Connection reset by peer."));
        });

        var act = () => invoker.SendAsync(CreateRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        _buffer.Signals.Should().ContainSingle();
        _buffer.Signals[0].Outcome.Should().Be(SignalOutcome.Failure);
        _buffer.Signals[0].HttpStatusCode.Should().Be(0);
        _buffer.Signals[0].Latency.Should().Be(TimeSpan.FromMilliseconds(200));
    }

    /// <summary>
    /// [EDGE] Connection refused (server not listening on port).
    /// </summary>
    [Fact]
    public async Task SendAsync_connection_refused_records_failure_signal()
    {
        var invoker = CreateInvoker((_, _) =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(10));
            throw new HttpRequestException(
                "Connection refused (127.0.0.1:8080)",
                new SocketException((int)SocketError.ConnectionRefused));
        });

        var act = () => invoker.SendAsync(CreateRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        _buffer.Signals.Should().ContainSingle()
            .Which.Outcome.Should().Be(SignalOutcome.Failure);
    }

    // ────────────────────────────────────────────────────────────
    // [EDGE] TLS/SSL Handshake Failure
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// [EDGE] TLS handshake failure (expired cert, etc.).
    /// </summary>
    [Fact]
    public async Task SendAsync_tls_failure_records_failure_signal()
    {
        var invoker = CreateInvoker((_, _) =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(150));
            throw new HttpRequestException(
                "The SSL connection could not be established, see inner exception.",
                new System.Security.Authentication.AuthenticationException(
                    "The remote certificate was rejected by the provided RemoteCertificateValidationCallback."));
        });

        var act = () => invoker.SendAsync(CreateRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        _buffer.Signals.Should().ContainSingle()
            .Which.Outcome.Should().Be(SignalOutcome.Failure);
    }

    // ────────────────────────────────────────────────────────────
    // [EDGE] Rapid sequential calls — each produces distinct signal
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// [EDGE] Rapid sequential outbound calls each produce one signal
    /// with cumulative latency.
    /// </summary>
    [Fact]
    public async Task SendAsync_rapid_sequential_calls_each_produce_distinct_signal()
    {
        int callCount = 0;
        var invoker = CreateInvoker((_, _) =>
        {
            callCount++;
            _clock.Advance(TimeSpan.FromMilliseconds(10 * callCount));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        // Act — 10 rapid calls
        for (int i = 0; i < 10; i++)
        {
            await invoker.SendAsync(CreateRequest(), CancellationToken.None);
        }

        // Assert
        _buffer.Signals.Should().HaveCount(10,
            "each outbound call produces exactly one signal");
        _buffer.Signals.Should().OnlyContain(s =>
            s.Outcome == SignalOutcome.Success);
    }

    // ────────────────────────────────────────────────────────────
    // [EDGE] Mixed exception types in sequence
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// [EDGE] Alternating success and failure calls maintain correct
    /// signal classification.
    /// </summary>
    [Fact]
    public async Task SendAsync_alternating_success_and_failure_maintains_correct_classification()
    {
        int callCount = 0;
        var invoker = CreateInvoker((_, _) =>
        {
            callCount++;
            _clock.Advance(TimeSpan.FromMilliseconds(50));

            if (callCount % 2 == 0)
            {
                throw new HttpRequestException("Connection refused");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        // Act — 6 calls: 1=success, 2=failure, 3=success, 4=failure, 5=success, 6=failure
        for (int i = 0; i < 6; i++)
        {
            try
            {
                await invoker.SendAsync(CreateRequest(), CancellationToken.None);
            }
            catch (HttpRequestException)
            {
                // Expected for even calls
            }
        }

        // Assert
        _buffer.Signals.Should().HaveCount(6);
        _buffer.Signals.Count(s => s.Outcome == SignalOutcome.Success).Should().Be(3);
        _buffer.Signals.Count(s => s.Outcome == SignalOutcome.Failure).Should().Be(3);
    }

    // ────────────────────────────────────────────────────────────
    // [INTEGRATION] Signal pipeline — outbound handler produces
    // signals consumable by evaluation
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// [AC-13][INTEGRATION] Outbound handler → signal buffer produces signals
    /// with correct success rate for downstream evaluation.
    /// 8 success + 2 failure = 80% success rate.
    /// </summary>
    [Fact]
    public async Task AC13_Outbound_signals_have_correct_distribution_for_evaluation()
    {
        int callCount = 0;
        var invoker = CreateInvoker((_, _) =>
        {
            var idx = Interlocked.Increment(ref callCount);
            _clock.Advance(TimeSpan.FromMilliseconds(50));

            return Task.FromResult(new HttpResponseMessage(
                idx <= 8 ? HttpStatusCode.OK : HttpStatusCode.InternalServerError));
        });

        // Act — 10 outbound calls
        for (int i = 0; i < 10; i++)
        {
            await invoker.SendAsync(CreateRequest(), CancellationToken.None);
        }

        // Assert — signals match expected distribution for PolicyEvaluator
        _buffer.Signals.Should().HaveCount(10);
        var successCount = _buffer.Signals.Count(s => s.Outcome == SignalOutcome.Success);
        var failureCount = _buffer.Signals.Count(s => s.Outcome == SignalOutcome.Failure);

        successCount.Should().Be(8);
        failureCount.Should().Be(2);

        // All signals carry the same DependencyId
        _buffer.Signals.Should().OnlyContain(s =>
            s.DependencyId.Value == "test-dependency");

        // All signals have valid latency
        _buffer.Signals.Should().OnlyContain(s => s.Latency > TimeSpan.Zero);
    }
}

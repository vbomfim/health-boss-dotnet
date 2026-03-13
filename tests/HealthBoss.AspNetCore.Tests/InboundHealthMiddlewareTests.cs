using FluentAssertions;
using HealthBoss.AspNetCore;
using HealthBoss.Core;
using HealthBoss.Core.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace HealthBoss.AspNetCore.Tests;

/// <summary>
/// TDD tests for InboundHealthMiddleware.
/// Covers: success signals, failure signals, custom predicates,
/// excluded paths, latency recording, route mapping, default component,
/// and null-safety for missing recorder.
/// </summary>
public sealed class InboundHealthMiddlewareTests : IDisposable
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly RecordingSignalBuffer _buffer = new();

    public InboundHealthMiddlewareTests()
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        // No unmanaged resources; satisfies IDisposable pattern for test cleanup.
    }

    // ─── Success / Failure Signals ───────────────────────────────────────

    [Fact]
    public async Task Request_returning_200_records_success_signal()
    {
        using var server = CreateServer(statusCode: 200);
        var client = server.CreateClient();

        await client.GetAsync("/api/orders");

        _buffer.Signals.Should().ContainSingle();
        var signal = _buffer.Signals[0];
        signal.Outcome.Should().Be(SignalOutcome.Success);
        signal.HttpStatusCode.Should().Be(200);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(204)]
    [InlineData(301)]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(499)]
    public async Task Request_returning_non_5xx_records_success_signal(int statusCode)
    {
        using var server = CreateServer(statusCode: statusCode);
        var client = server.CreateClient();

        await client.GetAsync("/api/data");

        _buffer.Signals.Should().ContainSingle()
            .Which.Outcome.Should().Be(SignalOutcome.Success);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task Request_returning_5xx_records_failure_signal(int statusCode)
    {
        using var server = CreateServer(statusCode: statusCode);
        var client = server.CreateClient();

        await client.GetAsync("/api/data");

        _buffer.Signals.Should().ContainSingle();
        var signal = _buffer.Signals[0];
        signal.Outcome.Should().Be(SignalOutcome.Failure);
        signal.HttpStatusCode.Should().Be(statusCode);
    }

    // ─── Custom IsFailure Predicate ──────────────────────────────────────

    [Fact]
    public async Task Custom_IsFailure_predicate_classifies_4xx_as_failure()
    {
        using var server = CreateServer(
            statusCode: 429,
            configureOptions: opts => opts.IsFailure = statusCode => statusCode >= 400);
        var client = server.CreateClient();

        await client.GetAsync("/api/data");

        _buffer.Signals.Should().ContainSingle()
            .Which.Outcome.Should().Be(SignalOutcome.Failure);
    }

    // ─── Excluded Paths ──────────────────────────────────────────────────

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/healthz/ready")]
    public async Task Excluded_paths_produce_no_signals(string path)
    {
        using var server = CreateServer(statusCode: 200);
        var client = server.CreateClient();

        await client.GetAsync(path);

        _buffer.Signals.Should().BeEmpty();
    }

    [Fact]
    public async Task Non_excluded_path_still_records_signal()
    {
        using var server = CreateServer(statusCode: 200);
        var client = server.CreateClient();

        await client.GetAsync("/api/users");

        _buffer.Signals.Should().ContainSingle();
    }

    [Fact]
    public async Task Excluded_paths_with_subpath_wildcard_match()
    {
        using var server = CreateServer(
            statusCode: 200,
            configureOptions: opts =>
                opts.ExcludePaths = ["/metrics", "/metrics/prometheus"]);
        var client = server.CreateClient();

        await client.GetAsync("/metrics");
        await client.GetAsync("/metrics/prometheus");

        _buffer.Signals.Should().BeEmpty();
    }

    // ─── Duration / Latency ──────────────────────────────────────────────

    [Fact]
    public async Task Signal_records_positive_latency()
    {
        using var server = CreateServer(statusCode: 200);
        var client = server.CreateClient();

        await client.GetAsync("/api/items");

        _buffer.Signals.Should().ContainSingle()
            .Which.Latency.Should().BeGreaterOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task Signal_timestamp_uses_clock()
    {
        using var server = CreateServer(statusCode: 200);
        var client = server.CreateClient();

        await client.GetAsync("/api/items");

        _buffer.Signals.Should().ContainSingle()
            .Which.Timestamp.Should().BeCloseTo(
                _timeProvider.GetUtcNow(),
                precision: TimeSpan.FromSeconds(5));
    }

    // ─── Per-Endpoint Route Mapping ──────────────────────────────────────

    [Fact]
    public async Task Mapped_route_uses_correct_component_name()
    {
        using var server = CreateServer(
            statusCode: 200,
            configureOptions: opts =>
            {
                opts.Map("/api/payments", "payments-handler");
                opts.Map("/api/users", "users-handler");
            });
        var client = server.CreateClient();

        await client.GetAsync("/api/payments/charge");

        _buffer.Signals.Should().ContainSingle()
            .Which.DependencyId.Value.Should().Be("payments-handler");
    }

    [Fact]
    public async Task Second_mapped_route_uses_correct_component_name()
    {
        using var server = CreateServer(
            statusCode: 200,
            configureOptions: opts =>
            {
                opts.Map("/api/payments", "payments-handler");
                opts.Map("/api/users", "users-handler");
            });
        var client = server.CreateClient();

        await client.GetAsync("/api/users/123");

        _buffer.Signals.Should().ContainSingle()
            .Which.DependencyId.Value.Should().Be("users-handler");
    }

    [Fact]
    public async Task Unmapped_route_uses_default_component()
    {
        using var server = CreateServer(
            statusCode: 200,
            configureOptions: opts =>
            {
                opts.Map("/api/payments", "payments-handler");
                opts.DefaultComponent = "api-gateway";
            });
        var client = server.CreateClient();

        await client.GetAsync("/api/unknown/resource");

        _buffer.Signals.Should().ContainSingle()
            .Which.DependencyId.Value.Should().Be("api-gateway");
    }

    [Fact]
    public async Task Default_component_name_is_api_when_not_configured()
    {
        using var server = CreateServer(statusCode: 200);
        var client = server.CreateClient();

        await client.GetAsync("/api/anything");

        _buffer.Signals.Should().ContainSingle()
            .Which.DependencyId.Value.Should().Be("api");
    }

    // ─── Null / Missing Recorder Safety ──────────────────────────────────

    [Fact]
    public async Task Null_signal_buffer_does_not_throw()
    {
        using var server = CreateServerWithoutBuffer(statusCode: 200);
        var client = server.CreateClient();

        var act = () => client.GetAsync("/api/data");

        await act.Should().NotThrowAsync();
    }

    // ─── Exception in Downstream ─────────────────────────────────────────

    [Fact]
    public async Task Exception_in_downstream_records_failure_signal()
    {
        using var server = CreateServer(throwException: true);
        var client = server.CreateClient();

        try
        {
            await client.GetAsync("/api/data");
        }
        catch
        {
            // Expected — TestHost propagates exceptions.
        }

        _buffer.Signals.Should().ContainSingle()
            .Which.Outcome.Should().Be(SignalOutcome.Failure);
    }

    // ─── Multiple Requests ───────────────────────────────────────────────

    [Fact]
    public async Task Multiple_requests_each_produce_a_signal()
    {
        using var server = CreateServer(statusCode: 200);
        var client = server.CreateClient();

        await client.GetAsync("/api/a");
        await client.GetAsync("/api/b");
        await client.GetAsync("/api/c");

        _buffer.Signals.Should().HaveCount(3);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private TestServer CreateServer(
        int statusCode = 200,
        bool throwException = false,
        Action<InboundTrackingOptions>? configureOptions = null)
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ISignalBuffer>(_buffer);
                services.AddSingleton<TimeProvider>(_timeProvider);
            })
            .Configure(app =>
            {
                app.UseHealthBossInboundTracking(opts =>
                {
                    configureOptions?.Invoke(opts);
                });

                app.Run(ctx =>
                {
                    if (throwException)
                    {
                        throw new InvalidOperationException("Downstream failure");
                    }

                    ctx.Response.StatusCode = statusCode;
                    return Task.CompletedTask;
                });
            });

        return new TestServer(builder);
    }

    private TestServer CreateServerWithoutBuffer(int statusCode = 200)
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                // Intentionally NOT registering ISignalBuffer
                services.AddSingleton<TimeProvider>(_timeProvider);
            })
            .Configure(app =>
            {
                app.UseHealthBossInboundTracking();

                app.Run(ctx =>
                {
                    ctx.Response.StatusCode = statusCode;
                    return Task.CompletedTask;
                });
            });

        return new TestServer(builder);
    }
}

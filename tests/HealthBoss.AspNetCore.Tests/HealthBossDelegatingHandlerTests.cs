using System.Net;
using FluentAssertions;
using HealthBoss.AspNetCore.Tests.Fakes;
using HealthBoss.Core.Contracts;

namespace HealthBoss.AspNetCore.Tests;

public sealed class HealthBossDelegatingHandlerTests : IDisposable
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
        HttpResponseMessage? response = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? handler = null,
        Action<OutboundTrackingOptions>? configure = null)
    {
        var options = new OutboundTrackingOptions { ComponentName = "test-api" };
        configure?.Invoke(options);

        var innerHandler = handler is not null
            ? new FakeHttpMessageHandler(handler)
            : new FakeHttpMessageHandler(response ?? new HttpResponseMessage(HttpStatusCode.OK));

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
    // Success signals
    // ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task SendAsync_non_server_error_records_success_signal(HttpStatusCode statusCode)
    {
        var invoker = CreateInvoker(new HttpResponseMessage(statusCode));

        await invoker.SendAsync(CreateRequest(), CancellationToken.None);

        _buffer.Signals.Should().ContainSingle();
        _buffer.Signals[0].Outcome.Should().Be(SignalOutcome.Success);
    }

    [Fact]
    public async Task SendAsync_2xx_records_correct_dependency_id()
    {
        var invoker = CreateInvoker(
            configure: o => o.ComponentName = "payment-service");

        await invoker.SendAsync(CreateRequest(), CancellationToken.None);

        _buffer.Signals.Should().ContainSingle();
        _buffer.Signals[0].DependencyId.Value.Should().Be("payment-service");
    }

    [Fact]
    public async Task SendAsync_records_latency()
    {
        var expectedLatency = TimeSpan.FromMilliseconds(150);

        var invoker = CreateInvoker(handler: (_, _) =>
        {
            _clock.Advance(expectedLatency);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        await invoker.SendAsync(CreateRequest(), CancellationToken.None);

        _buffer.Signals.Should().ContainSingle();
        _buffer.Signals[0].Latency.Should().Be(expectedLatency);
    }

    [Fact]
    public async Task SendAsync_records_http_status_code()
    {
        var invoker = CreateInvoker(new HttpResponseMessage(HttpStatusCode.OK));

        await invoker.SendAsync(CreateRequest(), CancellationToken.None);

        _buffer.Signals.Should().ContainSingle();
        _buffer.Signals[0].HttpStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task SendAsync_returns_response_to_caller()
    {
        var expected = new HttpResponseMessage(HttpStatusCode.Accepted);
        var invoker = CreateInvoker(expected);

        var actual = await invoker.SendAsync(CreateRequest(), CancellationToken.None);

        actual.Should().BeSameAs(expected);
    }

    // ────────────────────────────────────────────────────────────
    // Failure signals — server errors
    // ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task SendAsync_5xx_records_failure_signal(HttpStatusCode statusCode)
    {
        var invoker = CreateInvoker(new HttpResponseMessage(statusCode));

        await invoker.SendAsync(CreateRequest(), CancellationToken.None);

        _buffer.Signals.Should().ContainSingle();
        _buffer.Signals[0].Outcome.Should().Be(SignalOutcome.Failure);
        _buffer.Signals[0].HttpStatusCode.Should().Be((int)statusCode);
    }

    // ────────────────────────────────────────────────────────────
    // Failure signals — exceptions
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_timeout_exception_records_timeout_signal()
    {
        var invoker = CreateInvoker(handler: (_, _) =>
        {
            _clock.Advance(TimeSpan.FromSeconds(30));
            throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout.",
                new TimeoutException("A task was canceled."));
        });

        var act = () => invoker.SendAsync(CreateRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<TaskCanceledException>();
        _buffer.Signals.Should().ContainSingle();
        _buffer.Signals[0].Outcome.Should().Be(SignalOutcome.Timeout);
        _buffer.Signals[0].Latency.Should().Be(TimeSpan.FromSeconds(30));
        _buffer.Signals[0].HttpStatusCode.Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_connection_exception_records_failure_signal()
    {
        var invoker = CreateInvoker(handler: (_, _) =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(500));
            throw new HttpRequestException("Connection refused");
        });

        var act = () => invoker.SendAsync(CreateRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        _buffer.Signals.Should().ContainSingle();
        _buffer.Signals[0].Outcome.Should().Be(SignalOutcome.Failure);
        _buffer.Signals[0].Latency.Should().Be(TimeSpan.FromMilliseconds(500));
        _buffer.Signals[0].HttpStatusCode.Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_rethrows_timeout_exception()
    {
        var innerException = new TimeoutException("timed out");
        var invoker = CreateInvoker(handler: (_, _) =>
            throw new TaskCanceledException("timeout", innerException));

        var act = () => invoker.SendAsync(CreateRequest(), CancellationToken.None);

        (await act.Should().ThrowAsync<TaskCanceledException>())
            .Which.InnerException.Should().BeSameAs(innerException);
    }

    [Fact]
    public async Task SendAsync_rethrows_connection_exception()
    {
        var invoker = CreateInvoker(handler: (_, _) =>
            throw new HttpRequestException("Connection refused"));

        var act = () => invoker.SendAsync(CreateRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("Connection refused");
    }

    [Fact]
    public async Task SendAsync_user_cancellation_does_not_record_signal()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var invoker = CreateInvoker(handler: (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var act = () => invoker.SendAsync(CreateRequest(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _buffer.Signals.Should().BeEmpty();
    }

    // ────────────────────────────────────────────────────────────
    // Custom IsFailure predicate
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_custom_IsFailure_overrides_default_for_4xx()
    {
        var invoker = CreateInvoker(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            configure: o => o.IsFailure = r => (int)r.StatusCode >= 400);

        await invoker.SendAsync(CreateRequest(), CancellationToken.None);

        _buffer.Signals.Should().ContainSingle();
        _buffer.Signals[0].Outcome.Should().Be(SignalOutcome.Failure);
    }

    [Fact]
    public async Task SendAsync_custom_IsFailure_can_treat_5xx_as_success()
    {
        var invoker = CreateInvoker(
            new HttpResponseMessage(HttpStatusCode.InternalServerError),
            configure: o => o.IsFailure = _ => false);

        await invoker.SendAsync(CreateRequest(), CancellationToken.None);

        _buffer.Signals.Should().ContainSingle();
        _buffer.Signals[0].Outcome.Should().Be(SignalOutcome.Success);
    }

    // ────────────────────────────────────────────────────────────
    // Component name mapping
    // ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("redis-cache")]
    [InlineData("payment-gateway")]
    [InlineData("downstream-api")]
    public async Task SendAsync_component_name_maps_to_dependency_id(string componentName)
    {
        var invoker = CreateInvoker(
            configure: o => o.ComponentName = componentName);

        await invoker.SendAsync(CreateRequest(), CancellationToken.None);

        _buffer.Signals.Should().ContainSingle();
        _buffer.Signals[0].DependencyId.Value.Should().Be(componentName);
    }

    // ────────────────────────────────────────────────────────────
    // Timestamp correctness
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_signal_timestamp_is_after_call_completes()
    {
        var callDuration = TimeSpan.FromMilliseconds(200);
        var expectedTimestamp = _clock.UtcNow + callDuration;

        var invoker = CreateInvoker(handler: (_, _) =>
        {
            _clock.Advance(callDuration);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        await invoker.SendAsync(CreateRequest(), CancellationToken.None);

        _buffer.Signals.Should().ContainSingle();
        _buffer.Signals[0].Timestamp.Should().Be(expectedTimestamp);
    }

    // ────────────────────────────────────────────────────────────
    // Constructor validation
    // ────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_null_buffer_throws()
    {
        var act = () => new HealthBossDelegatingHandler(
            null!, _clock, new OutboundTrackingOptions { ComponentName = "test" });

        act.Should().Throw<ArgumentNullException>().WithParameterName("signalBuffer");
    }

    [Fact]
    public void Constructor_null_clock_throws()
    {
        var act = () => new HealthBossDelegatingHandler(
            _buffer, null!, new OutboundTrackingOptions { ComponentName = "test" });

        act.Should().Throw<ArgumentNullException>().WithParameterName("clock");
    }

    [Fact]
    public void Constructor_null_options_throws()
    {
        var act = () => new HealthBossDelegatingHandler(_buffer, _clock, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("options");
    }

    [Fact]
    public void Constructor_empty_component_name_throws()
    {
        var act = () => new HealthBossDelegatingHandler(
            _buffer, _clock, new OutboundTrackingOptions { ComponentName = "" });

        act.Should().Throw<ArgumentException>();
    }
}

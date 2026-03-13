using System.Net;
using System.Text.Json;
using FluentAssertions;
using HealthBoss.Core.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static HealthBoss.AspNetCore.Tests.TestFixtures;

namespace HealthBoss.AspNetCore.Tests;

public sealed class ProbeEndpointIntegrationTests : IAsyncDisposable
{
    // ── Liveness ──────────────────────────────────────────────

    [Fact]
    public async Task Liveness_returns_200_for_healthy()
    {
        await using var env = await CreateTestEnv();

        var response = await env.Client.GetAsync("/healthz/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Liveness_returns_503_for_unhealthy()
    {
        var provider = new FakeHealthReportProvider
        {
            HealthReport = CreateUnhealthyReport(),
        };
        await using var env = await CreateTestEnv(provider);

        var response = await env.Client.GetAsync("/healthz/live");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Liveness_returns_200_for_degraded()
    {
        var provider = new FakeHealthReportProvider
        {
            HealthReport = CreateDegradedReport(),
        };
        await using var env = await CreateTestEnv(provider);

        var response = await env.Client.GetAsync("/healthz/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Liveness_body_contains_healthy_status()
    {
        await using var env = await CreateTestEnv();

        var response = await env.Client.GetAsync("/healthz/live");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Be("""{"status":"healthy"}""");
    }

    // ── Readiness ─────────────────────────────────────────────

    [Fact]
    public async Task Readiness_returns_200_for_ready()
    {
        await using var env = await CreateTestEnv();

        var response = await env.Client.GetAsync("/healthz/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_returns_503_for_not_ready()
    {
        var provider = new FakeHealthReportProvider
        {
            ReadinessReport = CreateNotReadyReport(),
        };
        await using var env = await CreateTestEnv(provider);

        var response = await env.Client.GetAsync("/healthz/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // ── Startup ───────────────────────────────────────────────

    [Fact]
    public async Task Startup_returns_200_for_starting()
    {
        var tracker = new StartupTracker(); // default is Starting
        await using var env = await CreateTestEnv(tracker: tracker);

        var response = await env.Client.GetAsync("/healthz/startup");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("""{"status":"starting"}""");
    }

    [Fact]
    public async Task Startup_returns_200_for_ready()
    {
        var tracker = new StartupTracker();
        tracker.MarkReady();
        await using var env = await CreateTestEnv(tracker: tracker);

        var response = await env.Client.GetAsync("/healthz/startup");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("""{"status":"ready"}""");
    }

    [Fact]
    public async Task Startup_returns_503_for_failed()
    {
        var tracker = new StartupTracker();
        tracker.MarkFailed();
        await using var env = await CreateTestEnv(tracker: tracker);

        var response = await env.Client.GetAsync("/healthz/startup");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("""{"status":"failed"}""");
    }

    // ── Custom Paths ──────────────────────────────────────────

    [Fact]
    public async Task Custom_paths_are_honored()
    {
        await using var env = await CreateTestEnv(configure: opts =>
        {
            opts.LivenessPath = "/custom/live";
            opts.ReadinessPath = "/custom/ready";
            opts.StartupPath = "/custom/startup";
        });

        var liveness = await env.Client.GetAsync("/custom/live");
        var readiness = await env.Client.GetAsync("/custom/ready");
        var startup = await env.Client.GetAsync("/custom/startup");

        liveness.StatusCode.Should().Be(HttpStatusCode.OK);
        readiness.StatusCode.Should().Be(HttpStatusCode.OK);
        startup.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Default_paths_return_404_when_custom_paths_are_used()
    {
        await using var env = await CreateTestEnv(configure: opts =>
        {
            opts.LivenessPath = "/custom/live";
        });

        var response = await env.Client.GetAsync("/healthz/live");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Content Type ──────────────────────────────────────────

    [Fact]
    public async Task Response_content_type_is_application_json()
    {
        await using var env = await CreateTestEnv();

        var response = await env.Client.GetAsync("/healthz/live");

        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    // ── Default Detail Level ──────────────────────────────────

    [Fact]
    public async Task Default_detail_level_is_status_only()
    {
        await using var env = await CreateTestEnv();

        var response = await env.Client.GetAsync("/healthz/live");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain("dependencies");
        body.Should().NotContain("success_rate");
    }

    [Fact]
    public async Task Summary_detail_level_includes_dependencies()
    {
        await using var env = await CreateTestEnv(configure: opts =>
        {
            opts.DefaultDetailLevel = DetailLevel.Summary;
        });

        var response = await env.Client.GetAsync("/healthz/live");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        doc.RootElement.GetProperty("dependencies").GetArrayLength().Should().Be(2);
    }

    // ── Error Handling ────────────────────────────────────────

    [Fact]
    public async Task Exception_does_not_leak_into_liveness_response()
    {
        var provider = new ThrowingHealthReportProvider();
        await using var env = await CreateTestEnv(provider);

        var response = await env.Client.GetAsync("/healthz/live");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().Be("""{"status":"unhealthy"}""");
        body.Should().NotContain("Exception");
        body.Should().NotContain("Simulated");
        body.Should().NotContain("stack");
    }

    [Fact]
    public async Task Exception_does_not_leak_into_readiness_response()
    {
        var provider = new ThrowingHealthReportProvider();
        await using var env = await CreateTestEnv(provider);

        var response = await env.Client.GetAsync("/healthz/ready");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().Be("""{"status":"unhealthy"}""");
        body.Should().NotContain("Exception");
    }

    // ── snake_case Integration ─────────────────────────────────

    [Fact]
    public async Task Full_detail_response_uses_snake_case_keys()
    {
        await using var env = await CreateTestEnv(configure: opts =>
        {
            opts.DefaultDetailLevel = DetailLevel.Full;
        });

        var response = await env.Client.GetAsync("/healthz/live");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("success_rate");
        body.Should().Contain("total_signals");
        body.Should().NotContain("SuccessRate");
        body.Should().NotContain("TotalSignals");
    }

    // ── Test Infrastructure ───────────────────────────────────

    private readonly List<IAsyncDisposable> _disposables = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }
    }

    private async Task<TestEnv> CreateTestEnv(
        IHealthReportProvider? provider = null,
        IStartupTracker? tracker = null,
        Action<HealthBossEndpointOptions>? configure = null)
    {
        provider ??= new FakeHealthReportProvider();
        tracker ??= new StartupTracker();

        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<HealthBoss.Core.IHealthReportProvider>(provider);
        builder.Services.AddSingleton<HealthBoss.Core.IStartupTracker>((HealthBoss.Core.IStartupTracker)tracker);

        var app = builder.Build();
        app.MapHealthBossEndpoints(configure);
        await app.StartAsync();

        var client = app.GetTestClient();
        var env = new TestEnv(client, app);
        _disposables.Add(env);
        return env;
    }

    private sealed class TestEnv(HttpClient client, WebApplication app) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

using System.Text.Json;
using FluentAssertions;
using HealthBoss.Core.Contracts;

namespace HealthBoss.AspNetCore.Tests;

public sealed class ProbeResponseWriterTests
{
    // ── Liveness — StatusOnly ─────────────────────────────────

    [Fact]
    public void WriteLiveness_StatusOnly_healthy_returns_minimal_json()
    {
        var report = TestFixtures.CreateHealthyReport();

        var json = ProbeResponseWriter.WriteLivenessResponse(report, DetailLevel.StatusOnly);

        json.Should().Be("""{"status":"healthy"}""");
    }

    [Fact]
    public void WriteLiveness_StatusOnly_unhealthy_returns_minimal_json()
    {
        var report = TestFixtures.CreateUnhealthyReport();

        var json = ProbeResponseWriter.WriteLivenessResponse(report, DetailLevel.StatusOnly);

        json.Should().Be("""{"status":"unhealthy"}""");
    }

    [Fact]
    public void WriteLiveness_StatusOnly_degraded_returns_minimal_json()
    {
        var report = TestFixtures.CreateDegradedReport();

        var json = ProbeResponseWriter.WriteLivenessResponse(report, DetailLevel.StatusOnly);

        json.Should().Be("""{"status":"degraded"}""");
    }

    [Fact]
    public void WriteLiveness_StatusOnly_has_no_dependencies_field()
    {
        var report = TestFixtures.CreateHealthyReport();

        var json = ProbeResponseWriter.WriteLivenessResponse(report, DetailLevel.StatusOnly);

        json.Should().NotContain("dependencies");
    }

    // ── Liveness — Summary ────────────────────────────────────

    [Fact]
    public void WriteLiveness_Summary_includes_dependency_names_and_states()
    {
        var report = TestFixtures.CreateHealthyReport();

        var json = ProbeResponseWriter.WriteLivenessResponse(report, DetailLevel.Summary);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("healthy");

        var deps = root.GetProperty("dependencies");
        deps.GetArrayLength().Should().Be(2);
        deps[0].GetProperty("name").GetString().Should().Be("postgres-main");
        deps[0].GetProperty("state").GetString().Should().Be("healthy");
        deps[1].GetProperty("name").GetString().Should().Be("redis-cache");
        deps[1].GetProperty("state").GetString().Should().Be("healthy");
    }

    [Fact]
    public void WriteLiveness_Summary_does_not_include_metrics()
    {
        var report = TestFixtures.CreateHealthyReport();

        var json = ProbeResponseWriter.WriteLivenessResponse(report, DetailLevel.Summary);

        json.Should().NotContain("success_rate");
        json.Should().NotContain("total_signals");
        json.Should().NotContain("failure_count");
        json.Should().NotContain("success_count");
    }

    // ── Liveness — Full ───────────────────────────────────────

    [Fact]
    public void WriteLiveness_Full_includes_metrics()
    {
        var report = TestFixtures.CreateHealthyReport();

        var json = ProbeResponseWriter.WriteLivenessResponse(report, DetailLevel.Full);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var deps = root.GetProperty("dependencies");

        deps[0].GetProperty("name").GetString().Should().Be("postgres-main");
        deps[0].GetProperty("state").GetString().Should().Be("healthy");
        deps[0].GetProperty("success_rate").GetDouble().Should().Be(0.99);
        deps[0].GetProperty("total_signals").GetInt32().Should().Be(100);
        deps[0].GetProperty("failure_count").GetInt32().Should().Be(1);
        deps[0].GetProperty("success_count").GetInt32().Should().Be(99);
    }

    // ── Readiness — StatusOnly ────────────────────────────────

    [Fact]
    public void WriteReadiness_StatusOnly_ready_returns_minimal_json()
    {
        var report = TestFixtures.CreateReadyReport();

        var json = ProbeResponseWriter.WriteReadinessResponse(report, DetailLevel.StatusOnly);

        json.Should().Be("""{"status":"ready"}""");
    }

    [Fact]
    public void WriteReadiness_StatusOnly_not_ready_returns_minimal_json()
    {
        var report = TestFixtures.CreateNotReadyReport();

        var json = ProbeResponseWriter.WriteReadinessResponse(report, DetailLevel.StatusOnly);

        json.Should().Be("""{"status":"not_ready"}""");
    }

    // ── Readiness — Summary ───────────────────────────────────

    [Fact]
    public void WriteReadiness_Summary_includes_startup_and_drain_status()
    {
        var report = TestFixtures.CreateReadyReport();

        var json = ProbeResponseWriter.WriteReadinessResponse(report, DetailLevel.Summary);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("ready");
        root.GetProperty("startup_status").GetString().Should().Be("ready");
        root.GetProperty("drain_status").GetString().Should().Be("idle");
        root.GetProperty("dependencies").GetArrayLength().Should().Be(2);
    }

    // ── Readiness — Full ──────────────────────────────────────

    [Fact]
    public void WriteReadiness_Full_includes_metrics()
    {
        var report = TestFixtures.CreateReadyReport();

        var json = ProbeResponseWriter.WriteReadinessResponse(report, DetailLevel.Full);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("startup_status").GetString().Should().Be("ready");
        root.GetProperty("drain_status").GetString().Should().Be("idle");

        var deps = root.GetProperty("dependencies");
        deps[0].GetProperty("success_rate").GetDouble().Should().Be(0.99);
        deps[0].GetProperty("total_signals").GetInt32().Should().Be(100);
    }

    // ── Startup ───────────────────────────────────────────────

    [Theory]
    [InlineData(StartupStatus.Starting, """{"status":"starting"}""")]
    [InlineData(StartupStatus.Ready, """{"status":"ready"}""")]
    [InlineData(StartupStatus.Failed, """{"status":"failed"}""")]
    public void WriteStartup_returns_correct_json(StartupStatus status, string expected)
    {
        var json = ProbeResponseWriter.WriteStartupResponse(status);

        json.Should().Be(expected);
    }

    // ── snake_case Verification ───────────────────────────────

    [Fact]
    public void Json_keys_are_snake_case()
    {
        var report = TestFixtures.CreateHealthyReport();

        var json = ProbeResponseWriter.WriteLivenessResponse(report, DetailLevel.Full);

        json.Should().Contain("success_rate");
        json.Should().Contain("total_signals");
        json.Should().Contain("failure_count");
        json.Should().Contain("success_count");
        json.Should().NotContain("SuccessRate");
        json.Should().NotContain("TotalSignals");
    }

    [Fact]
    public void Readiness_json_keys_are_snake_case()
    {
        var report = TestFixtures.CreateReadyReport();

        var json = ProbeResponseWriter.WriteReadinessResponse(report, DetailLevel.Summary);

        json.Should().Contain("startup_status");
        json.Should().Contain("drain_status");
        json.Should().NotContain("StartupStatus");
        json.Should().NotContain("DrainStatus");
    }

    // ── Status Code Mapping ───────────────────────────────────

    [Theory]
    [InlineData(HealthStatus.Healthy, 200)]
    [InlineData(HealthStatus.Degraded, 200)]
    [InlineData(HealthStatus.Unhealthy, 503)]
    public void GetLivenessStatusCode_returns_expected(HealthStatus status, int expected)
    {
        ProbeResponseWriter.GetLivenessStatusCode(status).Should().Be(expected);
    }

    [Theory]
    [InlineData(ReadinessStatus.Ready, 200)]
    [InlineData(ReadinessStatus.NotReady, 503)]
    public void GetReadinessStatusCode_returns_expected(ReadinessStatus status, int expected)
    {
        ProbeResponseWriter.GetReadinessStatusCode(status).Should().Be(expected);
    }

    [Theory]
    [InlineData(StartupStatus.Starting, 200)]
    [InlineData(StartupStatus.Ready, 200)]
    [InlineData(StartupStatus.Failed, 503)]
    public void GetStartupStatusCode_returns_expected(StartupStatus status, int expected)
    {
        ProbeResponseWriter.GetStartupStatusCode(status).Should().Be(expected);
    }

    // ── Error Response ────────────────────────────────────────

    [Fact]
    public void WriteErrorResponse_returns_unhealthy_status()
    {
        var json = ProbeResponseWriter.WriteErrorResponse();

        json.Should().Be("""{"status":"unhealthy"}""");
    }
}

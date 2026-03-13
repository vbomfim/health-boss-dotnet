// <copyright file="TenantHealthResponseWriterTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using System.Text.Json;
using FluentAssertions;
using HealthBoss.Core;
using HealthBoss.Core.Contracts;
using static HealthBoss.AspNetCore.Tests.TestFixtures;

namespace HealthBoss.AspNetCore.Tests;

/// <summary>
/// Unit tests for <see cref="TenantHealthResponseWriter"/>.
/// Covers JSON serialization, snake_case keys, detail level gating, and edge cases.
/// </summary>
public sealed class TenantHealthResponseWriterTests
{
    // ── Tenant Health at Full Detail ───────────────────────────

    [Fact]
    public void WriteTenantHealth_with_provider_returns_tenant_data()
    {
        var report = CreateHealthyReport();
        var provider = new FakeTenantHealthProvider();
        provider.SetTenants(DbDependency, new Dictionary<TenantId, TenantHealthAssessment>
        {
            [new TenantId("contoso")] = new(new TenantId("contoso"), DbDependency, TenantHealthStatus.Degraded, 0.75, 100, 25, null),
        });

        var json = TenantHealthResponseWriter.WriteTenantHealthResponse(report, provider);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("components").GetArrayLength().Should().Be(2);
        var firstComponent = root.GetProperty("components")[0];
        firstComponent.GetProperty("component").GetString().Should().Be("postgres-main");
        firstComponent.GetProperty("active_tenants").GetInt32().Should().Be(1);

        var tenants = firstComponent.GetProperty("tenants");
        tenants.GetArrayLength().Should().Be(1);
        tenants[0].GetProperty("tenant_id").GetString().Should().Be("contoso");
        tenants[0].GetProperty("status").GetString().Should().Be("degraded");
        tenants[0].GetProperty("success_rate").GetDouble().Should().Be(0.75);
        tenants[0].GetProperty("total_signals").GetInt32().Should().Be(100);
        tenants[0].GetProperty("failure_count").GetInt32().Should().Be(25);
    }

    [Fact]
    public void WriteTenantHealth_null_provider_returns_empty_tenants()
    {
        var report = CreateHealthyReport();

        var json = TenantHealthResponseWriter.WriteTenantHealthResponse(report, provider: null);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var components = root.GetProperty("components");
        components.GetArrayLength().Should().Be(2);
        components[0].GetProperty("active_tenants").GetInt32().Should().Be(0);
        components[0].GetProperty("tenants").GetArrayLength().Should().Be(0);
        components[1].GetProperty("active_tenants").GetInt32().Should().Be(0);
        components[1].GetProperty("tenants").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void WriteTenantHealth_multiple_components_with_different_tenant_counts()
    {
        var report = CreateHealthyReport();
        var provider = new FakeTenantHealthProvider();

        // postgres-main has 2 tenants
        provider.SetTenants(DbDependency, new Dictionary<TenantId, TenantHealthAssessment>
        {
            [new TenantId("contoso")] = new(new TenantId("contoso"), DbDependency, TenantHealthStatus.Healthy, 0.99, 200, 2, null),
            [new TenantId("fabrikam")] = new(new TenantId("fabrikam"), DbDependency, TenantHealthStatus.Degraded, 0.80, 100, 20, null),
        });

        // redis-cache has 3 tenants
        provider.SetTenants(CacheDependency, new Dictionary<TenantId, TenantHealthAssessment>
        {
            [new TenantId("northwind")] = new(new TenantId("northwind"), CacheDependency, TenantHealthStatus.Healthy, 1.0, 50, 0, null),
            [new TenantId("adventure-works")] = new(new TenantId("adventure-works"), CacheDependency, TenantHealthStatus.Healthy, 0.95, 75, 4, null),
            [new TenantId("wide-world")] = new(new TenantId("wide-world"), CacheDependency, TenantHealthStatus.Unavailable, 0.20, 60, 48, null),
        });

        var json = TenantHealthResponseWriter.WriteTenantHealthResponse(report, provider);
        using var doc = JsonDocument.Parse(json);
        var components = doc.RootElement.GetProperty("components");

        components[0].GetProperty("active_tenants").GetInt32().Should().Be(2);
        components[0].GetProperty("tenants").GetArrayLength().Should().Be(2);

        components[1].GetProperty("active_tenants").GetInt32().Should().Be(3);
        components[1].GetProperty("tenants").GetArrayLength().Should().Be(3);
    }

    // ── snake_case Verification ───────────────────────────────

    [Fact]
    public void Tenant_json_keys_are_snake_case()
    {
        var report = CreateHealthyReport();
        var provider = new FakeTenantHealthProvider();
        provider.SetTenants(DbDependency, new Dictionary<TenantId, TenantHealthAssessment>
        {
            [new TenantId("contoso")] = new(new TenantId("contoso"), DbDependency, TenantHealthStatus.Healthy, 0.99, 100, 1, null),
        });

        var json = TenantHealthResponseWriter.WriteTenantHealthResponse(report, provider);

        json.Should().Contain("active_tenants");
        json.Should().Contain("tenant_id");
        json.Should().Contain("success_rate");
        json.Should().Contain("total_signals");
        json.Should().Contain("failure_count");
        json.Should().NotContain("ActiveTenants");
        json.Should().NotContain("TenantId");
        json.Should().NotContain("SuccessRate");
        json.Should().NotContain("TotalSignals");
        json.Should().NotContain("FailureCount");
    }

    // ── Forbidden Response ────────────────────────────────────

    [Fact]
    public void WriteForbiddenResponse_returns_error_message()
    {
        var json = TenantHealthResponseWriter.WriteForbiddenResponse();

        json.Should().Contain("error");
        json.Should().Contain("tenant_health_requires_full_detail_level");
    }

    // ── Status String Mapping ─────────────────────────────────

    [Fact]
    public void Tenant_status_healthy_serializes_as_snake_case()
    {
        var report = CreateHealthyReport();
        var provider = new FakeTenantHealthProvider();
        provider.SetTenants(DbDependency, new Dictionary<TenantId, TenantHealthAssessment>
        {
            [new TenantId("t1")] = new(new TenantId("t1"), DbDependency, TenantHealthStatus.Healthy, 1.0, 50, 0, null),
        });

        var json = TenantHealthResponseWriter.WriteTenantHealthResponse(report, provider);
        using var doc = JsonDocument.Parse(json);
        var tenant = doc.RootElement.GetProperty("components")[0].GetProperty("tenants")[0];

        tenant.GetProperty("status").GetString().Should().Be("healthy");
    }

    [Fact]
    public void Tenant_status_unavailable_serializes_as_snake_case()
    {
        var report = CreateHealthyReport();
        var provider = new FakeTenantHealthProvider();
        provider.SetTenants(DbDependency, new Dictionary<TenantId, TenantHealthAssessment>
        {
            [new TenantId("t1")] = new(new TenantId("t1"), DbDependency, TenantHealthStatus.Unavailable, 0.10, 80, 72, null),
        });

        var json = TenantHealthResponseWriter.WriteTenantHealthResponse(report, provider);
        using var doc = JsonDocument.Parse(json);
        var tenant = doc.RootElement.GetProperty("components")[0].GetProperty("tenants")[0];

        tenant.GetProperty("status").GetString().Should().Be("unavailable");
    }

    // ── Empty Report (no dependencies) ────────────────────────

    [Fact]
    public void WriteTenantHealth_empty_report_returns_empty_components()
    {
        var report = CreateEmptyReport(HealthStatus.Healthy);
        var provider = new FakeTenantHealthProvider();

        var json = TenantHealthResponseWriter.WriteTenantHealthResponse(report, provider);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("components").GetArrayLength().Should().Be(0);
    }

    // ── Provider returns null for unknown component ───────────

    [Fact]
    public void WriteTenantHealth_provider_returns_null_for_untracked_component()
    {
        var report = CreateHealthyReport();
        var provider = new FakeTenantHealthProvider(); // no tenants set

        var json = TenantHealthResponseWriter.WriteTenantHealthResponse(report, provider);
        using var doc = JsonDocument.Parse(json);
        var firstComponent = doc.RootElement.GetProperty("components")[0];

        firstComponent.GetProperty("active_tenants").GetInt32().Should().Be(0);
        firstComponent.GetProperty("tenants").GetArrayLength().Should().Be(0);
    }

    // ── Thread Safety ─────────────────────────────────────────

    [Fact]
    public async Task WriteTenantHealth_concurrent_reads_do_not_throw()
    {
        var report = CreateHealthyReport();
        var provider = new FakeTenantHealthProvider();
        provider.SetTenants(DbDependency, new Dictionary<TenantId, TenantHealthAssessment>
        {
            [new TenantId("contoso")] = new(new TenantId("contoso"), DbDependency, TenantHealthStatus.Healthy, 0.99, 100, 1, null),
        });

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() =>
            {
                var json = TenantHealthResponseWriter.WriteTenantHealthResponse(report, provider);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("components").GetArrayLength();
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(count => count == 2);
    }
}

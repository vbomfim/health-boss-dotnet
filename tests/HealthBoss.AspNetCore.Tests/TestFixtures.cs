using HealthBoss.Core.Contracts;

namespace HealthBoss.AspNetCore.Tests;

/// <summary>
/// Shared test fixtures for HealthBoss.AspNetCore tests.
/// </summary>
internal static class TestFixtures
{
    internal static readonly DateTimeOffset BaseTime =
        new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

    internal static readonly DependencyId DbDependency = new("postgres-main");
    internal static readonly DependencyId CacheDependency = new("redis-cache");

    // ── Health Reports ────────────────────────────────────────

    internal static HealthReport CreateHealthyReport() => new(
        HealthStatus.Healthy,
        CreateHealthyDependencies(),
        BaseTime);

    internal static HealthReport CreateDegradedReport() => new(
        HealthStatus.Degraded,
        CreateDegradedDependencies(),
        BaseTime);

    internal static HealthReport CreateUnhealthyReport() => new(
        HealthStatus.Unhealthy,
        CreateUnhealthyDependencies(),
        BaseTime);

    internal static HealthReport CreateEmptyReport(HealthStatus status) => new(
        status,
        Array.Empty<DependencySnapshot>(),
        BaseTime);

    // ── Readiness Reports ─────────────────────────────────────

    internal static ReadinessReport CreateReadyReport() => new(
        ReadinessStatus.Ready,
        CreateHealthyDependencies(),
        BaseTime,
        StartupStatus.Ready,
        DrainStatus.Idle);

    internal static ReadinessReport CreateNotReadyReport() => new(
        ReadinessStatus.NotReady,
        CreateUnhealthyDependencies(),
        BaseTime,
        StartupStatus.Ready,
        DrainStatus.Draining);

    // ── Dependency Snapshots ──────────────────────────────────

    internal static IReadOnlyList<DependencySnapshot> CreateHealthyDependencies() =>
    [
        CreateSnapshot(DbDependency, HealthState.Healthy, 0.99, 100, 1, 99),
        CreateSnapshot(CacheDependency, HealthState.Healthy, 1.0, 50, 0, 50),
    ];

    internal static IReadOnlyList<DependencySnapshot> CreateDegradedDependencies() =>
    [
        CreateSnapshot(DbDependency, HealthState.Degraded, 0.85, 100, 15, 85),
        CreateSnapshot(CacheDependency, HealthState.Healthy, 1.0, 50, 0, 50),
    ];

    internal static IReadOnlyList<DependencySnapshot> CreateUnhealthyDependencies() =>
    [
        CreateSnapshot(DbDependency, HealthState.CircuitOpen, 0.3, 100, 70, 30),
        CreateSnapshot(CacheDependency, HealthState.Healthy, 1.0, 50, 0, 50),
    ];

    private static DependencySnapshot CreateSnapshot(
        DependencyId id,
        HealthState state,
        double successRate,
        int totalSignals,
        int failureCount,
        int successCount) => new(
            id,
            state,
            new HealthAssessment(
                id,
                successRate,
                totalSignals,
                failureCount,
                successCount,
                TimeSpan.FromMinutes(5),
                BaseTime,
                state,
                state,
                null),
            BaseTime,
            state == HealthState.Healthy ? 0 : failureCount);

    // ── Fake Provider ─────────────────────────────────────────

    internal sealed class FakeHealthReportProvider : IHealthReportProvider
    {
        public HealthReport HealthReport { get; set; } = CreateHealthyReport();
        public ReadinessReport ReadinessReport { get; set; } = CreateReadyReport();

        public HealthReport GetHealthReport() => HealthReport;
        public ReadinessReport GetReadinessReport() => ReadinessReport;
    }

    internal sealed class ThrowingHealthReportProvider : IHealthReportProvider
    {
        public HealthReport GetHealthReport() =>
            throw new InvalidOperationException("Simulated failure");

        public ReadinessReport GetReadinessReport() =>
            throw new InvalidOperationException("Simulated failure");
    }

    // ── Fake Tenant Health Provider ───────────────────────────

    internal sealed class FakeTenantHealthProvider : Core.ITenantHealthProvider
    {
        private readonly Dictionary<DependencyId, Dictionary<TenantId, TenantHealthAssessment>> _data = [];

        public void SetTenants(
            DependencyId component,
            Dictionary<TenantId, TenantHealthAssessment> tenants)
        {
            _data[component] = tenants;
        }

        public IReadOnlyDictionary<TenantId, TenantHealthAssessment>? GetAllTenantHealth(DependencyId component) =>
            _data.TryGetValue(component, out var tenants) ? tenants : null;

        public int ActiveTenantCount(DependencyId component) =>
            _data.TryGetValue(component, out var tenants) ? tenants.Count : 0;
    }
}

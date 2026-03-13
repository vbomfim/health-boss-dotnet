using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using HealthBoss.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace HealthBoss.Core.Tests;

/// <summary>
/// Tests for <see cref="TenantHealthStore"/> covering:
/// AC31: Per-tenant signal recording
/// AC32: Tenant health does NOT affect service-level probes
/// AC33: LRU eviction when MaxTenantsPerComponent exceeded
/// AC34: TTL eviction after inactivity
/// AC35: Both LRU + TTL work together
/// AC36: Per-tenant metrics emitted
/// AC37: Tenant health event dispatched to IHealthEventSink
/// </summary>
public sealed class TenantHealthStoreTests : IDisposable
{
    private static readonly DependencyId ComponentA = new("component-a");
    private static readonly DependencyId ComponentB = new("component-b");
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly TenantId Tenant2 = new("tenant-2");
    private static readonly TenantId Tenant3 = new("tenant-3");

    private static readonly TenantEvictionConfig DefaultConfig = new(
        MaxTenants: 10_000,
        Ttl: TimeSpan.FromMinutes(30));

    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ISystemClock _clock;
    private readonly FakeHealthEventSink _eventSink = new();

    public TenantHealthStoreTests()
    {
        _timeProvider.SetUtcNow(TestFixtures.BaseTime);
        _clock = new SystemClock(_timeProvider);
    }

    public void Dispose()
    {
        // Any stores created in tests are disposed individually
    }

    private TenantHealthStore CreateStore(
        TenantEvictionConfig? config = null,
        IHealthEventSink? eventSink = null) =>
        new(
            _clock,
            config ?? DefaultConfig,
            eventSink,
            scavengeInterval: Timeout.InfiniteTimeSpan); // Disable background timer for deterministic tests

    // ───────────────────────────────────────────────────────────────
    // AC31: Per-tenant signal recording
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void RecordSuccess_CreatesAssessment_WithCorrectCounts()
    {
        using var store = CreateStore();

        store.RecordSuccess(ComponentA, Tenant1);
        store.RecordSuccess(ComponentA, Tenant1);
        store.RecordSuccess(ComponentA, Tenant1);

        var assessment = store.GetTenantHealth(ComponentA, Tenant1);

        assessment.TenantId.Should().Be(Tenant1);
        assessment.Component.Should().Be(ComponentA);
        assessment.Status.Should().Be(TenantHealthStatus.Healthy);
        assessment.SuccessRate.Should().Be(1.0);
        assessment.TotalSignals.Should().Be(3);
        assessment.FailureCount.Should().Be(0);
        assessment.LastSignalAt.Should().Be(_clock.UtcNow);
    }

    [Fact]
    public void RecordFailure_CreatesAssessment_WithCorrectCounts()
    {
        using var store = CreateStore();

        store.RecordFailure(ComponentA, Tenant1, "timeout");
        store.RecordFailure(ComponentA, Tenant1, "connection refused");

        var assessment = store.GetTenantHealth(ComponentA, Tenant1);

        assessment.TenantId.Should().Be(Tenant1);
        assessment.Component.Should().Be(ComponentA);
        assessment.Status.Should().Be(TenantHealthStatus.Unavailable);
        assessment.SuccessRate.Should().Be(0.0);
        assessment.TotalSignals.Should().Be(2);
        assessment.FailureCount.Should().Be(2);
        assessment.LastSignalAt.Should().Be(_clock.UtcNow);
    }

    [Fact]
    public void MixedSignals_ProducesCorrectSuccessRate()
    {
        using var store = CreateStore();

        // 8 success, 2 failure = 80% success rate → Degraded (below 0.9)
        for (int i = 0; i < 8; i++)
        {
            store.RecordSuccess(ComponentA, Tenant1);
        }

        for (int i = 0; i < 2; i++)
        {
            store.RecordFailure(ComponentA, Tenant1);
        }

        var assessment = store.GetTenantHealth(ComponentA, Tenant1);

        assessment.SuccessRate.Should().Be(0.8);
        assessment.TotalSignals.Should().Be(10);
        assessment.FailureCount.Should().Be(2);
        assessment.Status.Should().Be(TenantHealthStatus.Degraded);
    }

    [Fact]
    public void Status_Healthy_WhenSuccessRateAtOrAboveThreshold()
    {
        using var store = CreateStore();

        // 9 success, 1 failure = 90% = HealthyThreshold → Healthy
        for (int i = 0; i < 9; i++)
        {
            store.RecordSuccess(ComponentA, Tenant1);
        }

        store.RecordFailure(ComponentA, Tenant1);

        var assessment = store.GetTenantHealth(ComponentA, Tenant1);
        assessment.Status.Should().Be(TenantHealthStatus.Healthy);
        assessment.SuccessRate.Should().Be(0.9);
    }

    [Fact]
    public void Status_Degraded_WhenSuccessRateBelowHealthyAboveDegraded()
    {
        using var store = CreateStore();

        // 6 success, 4 failure = 60% → Degraded (below 0.9, above 0.5)
        for (int i = 0; i < 6; i++)
        {
            store.RecordSuccess(ComponentA, Tenant1);
        }

        for (int i = 0; i < 4; i++)
        {
            store.RecordFailure(ComponentA, Tenant1);
        }

        store.GetTenantHealth(ComponentA, Tenant1).Status
            .Should().Be(TenantHealthStatus.Degraded);
    }

    [Fact]
    public void Status_Unavailable_WhenSuccessRateBelowDegradedThreshold()
    {
        using var store = CreateStore();

        // 2 success, 8 failure = 20% → Unavailable (below 0.5)
        for (int i = 0; i < 2; i++)
        {
            store.RecordSuccess(ComponentA, Tenant1);
        }

        for (int i = 0; i < 8; i++)
        {
            store.RecordFailure(ComponentA, Tenant1);
        }

        store.GetTenantHealth(ComponentA, Tenant1).Status
            .Should().Be(TenantHealthStatus.Unavailable);
    }

    [Fact]
    public void GetTenantHealth_UnknownTenant_ReturnsDefaultHealthy()
    {
        using var store = CreateStore();

        var assessment = store.GetTenantHealth(ComponentA, Tenant1);

        assessment.Status.Should().Be(TenantHealthStatus.Healthy);
        assessment.SuccessRate.Should().Be(1.0);
        assessment.TotalSignals.Should().Be(0);
        assessment.FailureCount.Should().Be(0);
        assessment.LastSignalAt.Should().BeNull();
    }

    [Fact]
    public void LastSignalAt_AdvancesWithTime()
    {
        using var store = CreateStore();

        store.RecordSuccess(ComponentA, Tenant1);
        var firstTime = _clock.UtcNow;

        _timeProvider.Advance(TimeSpan.FromMinutes(5));

        store.RecordFailure(ComponentA, Tenant1);
        var secondTime = _clock.UtcNow;

        var assessment = store.GetTenantHealth(ComponentA, Tenant1);
        assessment.LastSignalAt.Should().Be(secondTime);
        secondTime.Should().BeAfter(firstTime);
    }

    // ───────────────────────────────────────────────────────────────
    // AC32: Tenant health does NOT affect service-level probes
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void TenantHealthStore_IsArchitecturallyIsolated_FromServiceProbes()
    {
        // TenantHealthStore does not implement any service-level interface.
        // This guarantees tenant health is a completely isolated dimension.
        using var store = CreateStore();

        store.Should().BeAssignableTo<ITenantHealthTracker>();
        store.Should().NotBeAssignableTo<IHealthReportProvider>();
        store.Should().NotBeAssignableTo<IDependencyMonitor>();
    }

    [Fact]
    public void TenantFailures_DoNotAffect_DependencyMonitorState()
    {
        // Service-level monitor is completely independent
        var buffer = new SignalBuffer(_clock);
        var monitor = new DependencyMonitor(
            ComponentA,
            buffer,
            new PolicyEvaluator(),
            new TransitionEngine(new DefaultStateGraph()),
            TestFixtures.ZeroJitterPolicy,
            _clock);

        using var store = CreateStore();

        // Record massive tenant failures
        for (int i = 0; i < 100; i++)
        {
            store.RecordFailure(ComponentA, Tenant1);
        }

        // Service-level state remains healthy (no signals recorded there)
        var snapshot = monitor.GetSnapshot();
        snapshot.CurrentState.Should().Be(HealthState.Healthy);
    }

    // ───────────────────────────────────────────────────────────────
    // Tenant isolation (tenant A failure doesn't affect tenant B)
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void TenantIsolation_FailuresForOneTenant_DoNotAffectAnother()
    {
        using var store = CreateStore();

        // Tenant1: all failures
        for (int i = 0; i < 10; i++)
        {
            store.RecordFailure(ComponentA, Tenant1);
        }

        // Tenant2: all successes
        for (int i = 0; i < 10; i++)
        {
            store.RecordSuccess(ComponentA, Tenant2);
        }

        var t1 = store.GetTenantHealth(ComponentA, Tenant1);
        var t2 = store.GetTenantHealth(ComponentA, Tenant2);

        t1.Status.Should().Be(TenantHealthStatus.Unavailable);
        t1.SuccessRate.Should().Be(0.0);

        t2.Status.Should().Be(TenantHealthStatus.Healthy);
        t2.SuccessRate.Should().Be(1.0);
    }

    [Fact]
    public void ComponentIsolation_SameTenantDifferentComponents()
    {
        using var store = CreateStore();

        // Same tenant, different health per component
        for (int i = 0; i < 10; i++)
        {
            store.RecordFailure(ComponentA, Tenant1);
        }

        for (int i = 0; i < 10; i++)
        {
            store.RecordSuccess(ComponentB, Tenant1);
        }

        store.GetTenantHealth(ComponentA, Tenant1).Status
            .Should().Be(TenantHealthStatus.Unavailable);
        store.GetTenantHealth(ComponentB, Tenant1).Status
            .Should().Be(TenantHealthStatus.Healthy);
    }

    // ───────────────────────────────────────────────────────────────
    // AC33: LRU eviction when MaxTenantsPerComponent exceeded
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void LruEviction_OldestTenantEvicted_WhenCapacityExceeded()
    {
        var config = new TenantEvictionConfig(MaxTenants: 3, Ttl: TimeSpan.FromMinutes(30));
        using var store = CreateStore(config);

        // Add 3 tenants at different times
        store.RecordSuccess(ComponentA, new TenantId("first"));
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        store.RecordSuccess(ComponentA, new TenantId("second"));
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        store.RecordSuccess(ComponentA, new TenantId("third"));

        store.ActiveTenantCount(ComponentA).Should().Be(3);

        // Adding a 4th tenant should evict the oldest ("first")
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        store.RecordSuccess(ComponentA, new TenantId("fourth"));

        store.ActiveTenantCount(ComponentA).Should().Be(3);

        // "first" should be evicted; "fourth" should exist
        store.GetTenantHealth(ComponentA, new TenantId("first"))
            .TotalSignals.Should().Be(0, "first tenant should have been evicted");
        store.GetTenantHealth(ComponentA, new TenantId("fourth"))
            .TotalSignals.Should().Be(1, "fourth tenant should be tracked");
    }

    [Fact]
    public void LruEviction_RefreshedTenantSurvives()
    {
        var config = new TenantEvictionConfig(MaxTenants: 2, Ttl: TimeSpan.FromMinutes(30));
        using var store = CreateStore(config);

        // Add tenant-1 and tenant-2
        store.RecordSuccess(ComponentA, Tenant1);
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        store.RecordSuccess(ComponentA, Tenant2);

        // Refresh tenant-1 (now it's the most recent)
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        store.RecordSuccess(ComponentA, Tenant1);

        // Add tenant-3 → tenant-2 should be evicted (it's the LRU)
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        store.RecordSuccess(ComponentA, Tenant3);

        store.ActiveTenantCount(ComponentA).Should().Be(2);
        store.GetTenantHealth(ComponentA, Tenant1).TotalSignals.Should().Be(2, "tenant-1 was refreshed");
        store.GetTenantHealth(ComponentA, Tenant2).TotalSignals.Should().Be(0, "tenant-2 was evicted");
        store.GetTenantHealth(ComponentA, Tenant3).TotalSignals.Should().Be(1, "tenant-3 was just added");
    }

    [Fact]
    public void LruEviction_PerComponent_DoesNotAffectOtherComponents()
    {
        var config = new TenantEvictionConfig(MaxTenants: 2, Ttl: TimeSpan.FromMinutes(30));
        using var store = CreateStore(config);

        // Fill component A to capacity
        store.RecordSuccess(ComponentA, Tenant1);
        store.RecordSuccess(ComponentA, Tenant2);

        // Add to component B — should NOT trigger eviction on component A
        store.RecordSuccess(ComponentB, Tenant1);
        store.RecordSuccess(ComponentB, Tenant2);
        store.RecordSuccess(ComponentB, Tenant3);

        // Component B gets evicted, not component A
        store.ActiveTenantCount(ComponentA).Should().Be(2);
        store.ActiveTenantCount(ComponentB).Should().Be(2);
    }

    // ───────────────────────────────────────────────────────────────
    // AC34: TTL eviction after inactivity
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void TtlEviction_InactiveTenantRemoved()
    {
        var config = new TenantEvictionConfig(MaxTenants: 10_000, Ttl: TimeSpan.FromMinutes(5));
        using var store = CreateStore(config);

        store.RecordSuccess(ComponentA, Tenant1);
        store.RecordSuccess(ComponentA, Tenant2);

        store.ActiveTenantCount(ComponentA).Should().Be(2);

        // Advance time past TTL
        _timeProvider.Advance(TimeSpan.FromMinutes(6));

        // Trigger scavenging
        store.ScavengeStaleTenants();

        store.ActiveTenantCount(ComponentA).Should().Be(0);
        store.GetTenantHealth(ComponentA, Tenant1).TotalSignals.Should().Be(0);
    }

    [Fact]
    public void TtlEviction_ActiveTenantSurvives()
    {
        var config = new TenantEvictionConfig(MaxTenants: 10_000, Ttl: TimeSpan.FromMinutes(5));
        using var store = CreateStore(config);

        store.RecordSuccess(ComponentA, Tenant1);
        store.RecordSuccess(ComponentA, Tenant2);

        // Advance 4 minutes (within TTL)
        _timeProvider.Advance(TimeSpan.FromMinutes(4));

        // Refresh tenant-1
        store.RecordSuccess(ComponentA, Tenant1);

        // Advance 2 more minutes (now 6 total — tenant-2 is stale, tenant-1 was refreshed at T+4)
        _timeProvider.Advance(TimeSpan.FromMinutes(2));

        store.ScavengeStaleTenants();

        store.ActiveTenantCount(ComponentA).Should().Be(1);
        store.GetTenantHealth(ComponentA, Tenant1).TotalSignals.Should().Be(2);
        store.GetTenantHealth(ComponentA, Tenant2).TotalSignals.Should().Be(0, "tenant-2 was inactive");
    }

    // ───────────────────────────────────────────────────────────────
    // AC35: Both LRU + TTL work together
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void LruAndTtl_WorkTogether_BeltAndSuspenders()
    {
        var config = new TenantEvictionConfig(MaxTenants: 3, Ttl: TimeSpan.FromMinutes(5));
        using var store = CreateStore(config);

        // T=0: Add 3 tenants at capacity
        store.RecordSuccess(ComponentA, Tenant1);
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        store.RecordSuccess(ComponentA, Tenant2);
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        store.RecordSuccess(ComponentA, Tenant3);

        store.ActiveTenantCount(ComponentA).Should().Be(3);

        // T=3min: Refresh tenant-1
        _timeProvider.Advance(TimeSpan.FromMinutes(3));
        store.RecordSuccess(ComponentA, Tenant1);

        // T=6min: Scavenge — tenant-2 and tenant-3 are stale (last signal > 5min ago)
        _timeProvider.Advance(TimeSpan.FromMinutes(3));
        store.ScavengeStaleTenants();

        store.ActiveTenantCount(ComponentA).Should().Be(1, "only tenant-1 survived TTL");
        store.GetTenantHealth(ComponentA, Tenant1).TotalSignals
            .Should().Be(2, "tenant-1 was refreshed");

        // Now fill back to capacity and trigger LRU
        var tenantX = new TenantId("tenant-x");
        var tenantY = new TenantId("tenant-y");
        var tenantZ = new TenantId("tenant-z");

        store.RecordSuccess(ComponentA, tenantX);
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        store.RecordSuccess(ComponentA, tenantY);

        store.ActiveTenantCount(ComponentA).Should().Be(3, "at capacity");

        // Add one more — LRU evicts tenant-1 (oldest last signal at T=3min)
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        store.RecordSuccess(ComponentA, tenantZ);

        store.ActiveTenantCount(ComponentA).Should().Be(3, "hard cap maintained");
        store.GetTenantHealth(ComponentA, Tenant1).TotalSignals
            .Should().Be(0, "tenant-1 evicted by LRU");
    }

    // ───────────────────────────────────────────────────────────────
    // AC33 (Security Finding #3): MaxTenantsPerComponent hard cap enforced
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void MaxTenantsPerComponent_HardCapEnforced()
    {
        var config = new TenantEvictionConfig(MaxTenants: 5, Ttl: TimeSpan.FromMinutes(30));
        using var store = CreateStore(config);

        // Add exactly MaxTenants
        for (int i = 0; i < 5; i++)
        {
            store.RecordSuccess(ComponentA, new TenantId($"tenant-{i}"));
            _timeProvider.Advance(TimeSpan.FromSeconds(1));
        }

        store.ActiveTenantCount(ComponentA).Should().Be(5);

        // Add 100 more — count should never exceed MaxTenants
        for (int i = 5; i < 105; i++)
        {
            store.RecordSuccess(ComponentA, new TenantId($"tenant-{i}"));
            _timeProvider.Advance(TimeSpan.FromSeconds(1));
        }

        store.ActiveTenantCount(ComponentA).Should().Be(5, "hard cap must be enforced");
    }

    [Fact]
    public void MaxTenantsPerComponent_EvictionCountIncreases()
    {
        var config = new TenantEvictionConfig(MaxTenants: 2, Ttl: TimeSpan.FromMinutes(30));
        using var store = CreateStore(config);

        store.RecordSuccess(ComponentA, Tenant1);
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        store.RecordSuccess(ComponentA, Tenant2);
        _timeProvider.Advance(TimeSpan.FromSeconds(1));

        // These trigger LRU evictions
        store.RecordSuccess(ComponentA, Tenant3);
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        store.RecordSuccess(ComponentA, new TenantId("tenant-4"));

        store.GetEvictionCount(ComponentA).Should().BeGreaterOrEqualTo(2);
    }

    // ───────────────────────────────────────────────────────────────
    // AC36: Per-tenant metrics emitted
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void ActiveTenantCount_ReflectsGauge()
    {
        using var store = CreateStore();

        store.ActiveTenantCount(ComponentA).Should().Be(0);

        store.RecordSuccess(ComponentA, Tenant1);
        store.ActiveTenantCount(ComponentA).Should().Be(1);

        store.RecordSuccess(ComponentA, Tenant2);
        store.ActiveTenantCount(ComponentA).Should().Be(2);
    }

    [Fact]
    public void EvictionCount_ReflectsCounter()
    {
        var config = new TenantEvictionConfig(MaxTenants: 1, Ttl: TimeSpan.FromMinutes(30));
        using var store = CreateStore(config);

        store.GetEvictionCount(ComponentA).Should().Be(0);

        store.RecordSuccess(ComponentA, Tenant1);
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        store.RecordSuccess(ComponentA, Tenant2); // Evicts Tenant1

        store.GetEvictionCount(ComponentA).Should().Be(1);
    }

    // ───────────────────────────────────────────────────────────────
    // TenantId validation at boundary
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void RecordSuccess_DefaultComponent_Throws()
    {
        using var store = CreateStore();
        var act = () => store.RecordSuccess(default, Tenant1);
        act.Should().Throw<ArgumentException>().WithParameterName("component");
    }

    [Fact]
    public void RecordSuccess_DefaultTenantId_Throws()
    {
        using var store = CreateStore();
        var act = () => store.RecordSuccess(ComponentA, default);
        act.Should().Throw<ArgumentException>().WithParameterName("tenantId");
    }

    [Fact]
    public void RecordFailure_DefaultComponent_Throws()
    {
        using var store = CreateStore();
        var act = () => store.RecordFailure(default, Tenant1);
        act.Should().Throw<ArgumentException>().WithParameterName("component");
    }

    [Fact]
    public void RecordFailure_DefaultTenantId_Throws()
    {
        using var store = CreateStore();
        var act = () => store.RecordFailure(ComponentA, default);
        act.Should().Throw<ArgumentException>().WithParameterName("tenantId");
    }

    [Fact]
    public void GetTenantHealth_DefaultComponent_Throws()
    {
        using var store = CreateStore();
        var act = () => store.GetTenantHealth(default, Tenant1);
        act.Should().Throw<ArgumentException>().WithParameterName("component");
    }

    [Fact]
    public void GetTenantHealth_DefaultTenantId_Throws()
    {
        using var store = CreateStore();
        var act = () => store.GetTenantHealth(ComponentA, default);
        act.Should().Throw<ArgumentException>().WithParameterName("tenantId");
    }

    [Fact]
    public void GetAllTenantHealth_DefaultComponent_Throws()
    {
        using var store = CreateStore();
        var act = () => store.GetAllTenantHealth(default);
        act.Should().Throw<ArgumentException>().WithParameterName("component");
    }

    [Fact]
    public void ActiveTenantCount_DefaultComponent_Throws()
    {
        using var store = CreateStore();
        var act = () => store.ActiveTenantCount(default);
        act.Should().Throw<ArgumentException>().WithParameterName("component");
    }

    [Fact]
    public void TenantId_ValidationEnforced_AtConstruction()
    {
        // TenantId validates on construction via HealthBossValidator
        var act = () => new TenantId("invalid id with spaces!");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TenantId_MaxLength128_Enforced()
    {
        var longId = new string('a', 129);
        var act = () => new TenantId(longId);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TenantId_ValidAt128Characters()
    {
        var validId = new string('a', 128);
        var tenantId = new TenantId(validId);
        tenantId.Value.Should().HaveLength(128);
    }

    // ───────────────────────────────────────────────────────────────
    // Config validation
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_MaxTenantsZero_Throws()
    {
        var config = new TenantEvictionConfig(MaxTenants: 0, Ttl: TimeSpan.FromMinutes(5));
        var act = () => new TenantHealthStore(_clock, config, null, scavengeInterval: Timeout.InfiniteTimeSpan);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_NegativeTtl_Throws()
    {
        var config = new TenantEvictionConfig(MaxTenants: 10, Ttl: TimeSpan.FromMinutes(-1));
        var act = () => new TenantHealthStore(_clock, config, null, scavengeInterval: Timeout.InfiniteTimeSpan);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_ZeroTtl_Throws()
    {
        var config = new TenantEvictionConfig(MaxTenants: 10, Ttl: TimeSpan.Zero);
        var act = () => new TenantHealthStore(_clock, config, null, scavengeInterval: Timeout.InfiniteTimeSpan);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_NullClock_Throws()
    {
        var config = new TenantEvictionConfig(MaxTenants: 10, Ttl: TimeSpan.FromMinutes(5));
        var act = () => new TenantHealthStore(null!, config, null, scavengeInterval: Timeout.InfiniteTimeSpan);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullConfig_Throws()
    {
        var act = () => new TenantHealthStore(_clock, null!, null, scavengeInterval: Timeout.InfiniteTimeSpan);
        act.Should().Throw<ArgumentNullException>();
    }

    // ───────────────────────────────────────────────────────────────
    // AC37: Tenant health event dispatched to IHealthEventSink
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void StatusChange_EventDispatched_ToSink()
    {
        using var store = CreateStore(eventSink: _eventSink);

        // Record failures to transition from Healthy → Unavailable
        for (int i = 0; i < 10; i++)
        {
            store.RecordFailure(ComponentA, Tenant1);
        }

        _eventSink.EventCount.Should().BeGreaterOrEqualTo(1);

        var evt = _eventSink.Events.First();
        evt.Component.Should().Be(ComponentA);
        evt.TenantId.Should().Be(Tenant1);
        evt.NewStatus.Should().Be(TenantHealthStatus.Unavailable);
    }

    [Fact]
    public void NoStatusChange_NoEventDispatched()
    {
        using var store = CreateStore(eventSink: _eventSink);

        // All successes — status stays Healthy, no event
        for (int i = 0; i < 10; i++)
        {
            store.RecordSuccess(ComponentA, Tenant1);
        }

        _eventSink.EventCount.Should().Be(0, "status remained Healthy — no change event");
    }

    [Fact]
    public void StatusRecovery_EventDispatched()
    {
        using var store = CreateStore(eventSink: _eventSink);

        // 1 failure → Unavailable (first signal as failure triggers status change)
        store.RecordFailure(ComponentA, Tenant1);

        // Now recover with many successes to bring rate above 0.9
        for (int i = 0; i < 20; i++)
        {
            store.RecordSuccess(ComponentA, Tenant1);
        }

        // Should have at least 2 events: Healthy→Unavailable, then Unavailable→something
        _eventSink.EventCount.Should().BeGreaterOrEqualTo(2);

        var lastEvent = _eventSink.Events.Last();
        lastEvent.NewStatus.Should().Be(TenantHealthStatus.Healthy);
    }

    [Fact]
    public void EventSink_Null_NoException()
    {
        // When no event sink is configured, recording should not throw
        using var store = CreateStore(eventSink: null);

        for (int i = 0; i < 10; i++)
        {
            store.RecordFailure(ComponentA, Tenant1);
        }

        // No exception — null sink is handled gracefully
        store.GetTenantHealth(ComponentA, Tenant1).Status
            .Should().Be(TenantHealthStatus.Unavailable);
    }

    // ───────────────────────────────────────────────────────────────
    // GetAllTenantHealth
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void GetAllTenantHealth_ReturnsAllActiveTenants()
    {
        using var store = CreateStore();

        store.RecordSuccess(ComponentA, Tenant1);
        store.RecordFailure(ComponentA, Tenant2);
        store.RecordSuccess(ComponentA, Tenant3);

        var all = store.GetAllTenantHealth(ComponentA);

        all.Should().HaveCount(3);
        all.Should().ContainKey(Tenant1);
        all.Should().ContainKey(Tenant2);
        all.Should().ContainKey(Tenant3);

        all[Tenant1].Status.Should().Be(TenantHealthStatus.Healthy);
        all[Tenant2].Status.Should().Be(TenantHealthStatus.Unavailable);
        all[Tenant3].Status.Should().Be(TenantHealthStatus.Healthy);
    }

    [Fact]
    public void GetAllTenantHealth_ExcludesOtherComponents()
    {
        using var store = CreateStore();

        store.RecordSuccess(ComponentA, Tenant1);
        store.RecordSuccess(ComponentB, Tenant2);

        var allA = store.GetAllTenantHealth(ComponentA);
        allA.Should().HaveCount(1);
        allA.Should().ContainKey(Tenant1);
    }

    [Fact]
    public void GetAllTenantHealth_EmptyComponent_ReturnsEmpty()
    {
        using var store = CreateStore();

        var all = store.GetAllTenantHealth(ComponentA);
        all.Should().BeEmpty();
    }

    // ───────────────────────────────────────────────────────────────
    // ActiveTenantCount correct after evictions
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void ActiveTenantCount_CorrectAfterLruEviction()
    {
        var config = new TenantEvictionConfig(MaxTenants: 3, Ttl: TimeSpan.FromMinutes(30));
        using var store = CreateStore(config);

        for (int i = 0; i < 10; i++)
        {
            store.RecordSuccess(ComponentA, new TenantId($"t-{i}"));
            _timeProvider.Advance(TimeSpan.FromSeconds(1));
        }

        store.ActiveTenantCount(ComponentA).Should().Be(3, "hard cap enforced");
    }

    [Fact]
    public void ActiveTenantCount_CorrectAfterTtlEviction()
    {
        var config = new TenantEvictionConfig(MaxTenants: 10_000, Ttl: TimeSpan.FromMinutes(5));
        using var store = CreateStore(config);

        store.RecordSuccess(ComponentA, Tenant1);
        store.RecordSuccess(ComponentA, Tenant2);
        store.RecordSuccess(ComponentA, Tenant3);

        _timeProvider.Advance(TimeSpan.FromMinutes(6));
        store.ScavengeStaleTenants();

        store.ActiveTenantCount(ComponentA).Should().Be(0);
    }

    [Fact]
    public void ActiveTenantCount_CorrectAfterMixedEvictions()
    {
        var config = new TenantEvictionConfig(MaxTenants: 3, Ttl: TimeSpan.FromMinutes(5));
        using var store = CreateStore(config);

        // Add 3 tenants
        store.RecordSuccess(ComponentA, Tenant1);
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        store.RecordSuccess(ComponentA, Tenant2);
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        store.RecordSuccess(ComponentA, Tenant3);

        // TTL scavenge after 6 minutes → all evicted
        _timeProvider.Advance(TimeSpan.FromMinutes(6));
        store.ScavengeStaleTenants();
        store.ActiveTenantCount(ComponentA).Should().Be(0);

        // Add new tenants → LRU should work on fresh state
        store.RecordSuccess(ComponentA, new TenantId("new-1"));
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        store.RecordSuccess(ComponentA, new TenantId("new-2"));
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        store.RecordSuccess(ComponentA, new TenantId("new-3"));
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        store.RecordSuccess(ComponentA, new TenantId("new-4"));

        store.ActiveTenantCount(ComponentA).Should().Be(3, "LRU evicted one after TTL cleanup");
    }

    // ───────────────────────────────────────────────────────────────
    // Thread safety: concurrent tenant recording
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void ConcurrentRecording_SameTenant_NoDataLoss()
    {
        using var store = CreateStore();
        const int threadCount = 10;
        const int signalsPerThread = 1000;

        Parallel.For(0, threadCount, _ =>
        {
            for (int i = 0; i < signalsPerThread; i++)
            {
                store.RecordSuccess(ComponentA, Tenant1);
            }
        });

        var assessment = store.GetTenantHealth(ComponentA, Tenant1);
        assessment.TotalSignals.Should().Be(threadCount * signalsPerThread);
        assessment.SuccessRate.Should().Be(1.0);
    }

    [Fact]
    public void ConcurrentRecording_ManyTenants_NoExceptions()
    {
        var config = new TenantEvictionConfig(MaxTenants: 50, Ttl: TimeSpan.FromMinutes(30));
        using var store = CreateStore(config);

        // 100 threads each adding a unique tenant → triggers LRU eviction under concurrency
        Parallel.For(0, 100, i =>
        {
            var tenantId = new TenantId($"concurrent-{i}");
            store.RecordSuccess(ComponentA, tenantId);
            store.RecordFailure(ComponentA, tenantId);
        });

        // Hard cap must be maintained
        store.ActiveTenantCount(ComponentA).Should().BeLessOrEqualTo(50);
    }

    [Fact]
    public void ConcurrentRecording_MixedOperations_ThreadSafe()
    {
        var config = new TenantEvictionConfig(MaxTenants: 100, Ttl: TimeSpan.FromMinutes(30));
        using var store = CreateStore(config, _eventSink);

        // Mix of reads and writes from many threads
        Parallel.For(0, 50, i =>
        {
            var tenantId = new TenantId($"mixed-{i % 20}");

            store.RecordSuccess(ComponentA, tenantId);
            store.RecordFailure(ComponentA, tenantId, "test error");
            _ = store.GetTenantHealth(ComponentA, tenantId);
            _ = store.ActiveTenantCount(ComponentA);
            _ = store.GetAllTenantHealth(ComponentA);
        });

        // No exceptions and consistent state
        store.ActiveTenantCount(ComponentA).Should().BeGreaterThan(0);
        store.ActiveTenantCount(ComponentA).Should().BeLessOrEqualTo(100);
    }

    // ───────────────────────────────────────────────────────────────
    // ITenantHealthTracker interface
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void TenantHealthStore_Implements_ITenantHealthTracker()
    {
        using var store = CreateStore();
        store.Should().BeAssignableTo<ITenantHealthTracker>();
    }

    [Fact]
    public void TenantHealthStore_Implements_IDisposable()
    {
        using var store = CreateStore();
        store.Should().BeAssignableTo<IDisposable>();
    }

    // ───────────────────────────────────────────────────────────────
    // Edge cases
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void MaxTenants_One_EvictsOnEveryNewTenant()
    {
        var config = new TenantEvictionConfig(MaxTenants: 1, Ttl: TimeSpan.FromMinutes(30));
        using var store = CreateStore(config);

        store.RecordSuccess(ComponentA, Tenant1);
        store.ActiveTenantCount(ComponentA).Should().Be(1);

        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        store.RecordSuccess(ComponentA, Tenant2);
        store.ActiveTenantCount(ComponentA).Should().Be(1);

        store.GetTenantHealth(ComponentA, Tenant1).TotalSignals.Should().Be(0, "evicted");
        store.GetTenantHealth(ComponentA, Tenant2).TotalSignals.Should().Be(1);
    }

    [Fact]
    public void ScavengeStaleTenants_TtlEvictionCountIncreases()
    {
        var config = new TenantEvictionConfig(MaxTenants: 10_000, Ttl: TimeSpan.FromMinutes(5));
        using var store = CreateStore(config);

        store.RecordSuccess(ComponentA, Tenant1);
        store.RecordSuccess(ComponentA, Tenant2);

        store.GetEvictionCount(ComponentA).Should().Be(0);

        _timeProvider.Advance(TimeSpan.FromMinutes(6));
        store.ScavengeStaleTenants();

        store.GetEvictionCount(ComponentA).Should().Be(2);
    }

    [Fact]
    public void ScavengeStaleTenants_NoStaleEntries_NoEviction()
    {
        var config = new TenantEvictionConfig(MaxTenants: 10_000, Ttl: TimeSpan.FromMinutes(5));
        using var store = CreateStore(config);

        store.RecordSuccess(ComponentA, Tenant1);

        // Advance less than TTL
        _timeProvider.Advance(TimeSpan.FromMinutes(2));
        store.ScavengeStaleTenants();

        store.ActiveTenantCount(ComponentA).Should().Be(1);
        store.GetEvictionCount(ComponentA).Should().Be(0);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var store = CreateStore();
        store.Dispose();
        store.Dispose(); // Should not throw
    }
}

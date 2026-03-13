using FluentAssertions;
using HealthBoss.Core;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace HealthBoss.Core.Tests;

/// <summary>
/// Tests for the HealthBossBuilder fluent configuration API and DI registration.
/// Covers: service registration, fluent builders, validation, defaults, TimeProvider injection.
/// </summary>
public sealed class HealthBossBuilderTests
{
    // ──────────────────────────────────────────────
    // DI Registration
    // ──────────────────────────────────────────────

    [Fact]
    public void AddHealthBoss_registers_all_expected_singleton_services()
    {
        var services = new ServiceCollection();

        services.AddHealthBoss(opts =>
        {
            opts.AddComponent("redis");
        });

        using var provider = services.BuildServiceProvider();

        provider.GetService<ISystemClock>().Should().NotBeNull();
        provider.GetService<IPolicyEvaluator>().Should().NotBeNull();
        provider.GetService<IStateGraph>().Should().NotBeNull();
        provider.GetService<ITransitionEngine>().Should().NotBeNull();
        provider.GetService<IStartupTracker>().Should().NotBeNull();
        provider.GetService<ITimerBudgetValidator>().Should().NotBeNull();
        provider.GetService<IOptions<HealthBossOptions>>().Should().NotBeNull();
    }

    [Fact]
    public void AddHealthBoss_registers_services_as_singletons()
    {
        var services = new ServiceCollection();

        services.AddHealthBoss(opts => opts.AddComponent("redis"));

        using var provider = services.BuildServiceProvider();

        var clock1 = provider.GetRequiredService<ISystemClock>();
        var clock2 = provider.GetRequiredService<ISystemClock>();
        clock1.Should().BeSameAs(clock2);

        var evaluator1 = provider.GetRequiredService<IPolicyEvaluator>();
        var evaluator2 = provider.GetRequiredService<IPolicyEvaluator>();
        evaluator1.Should().BeSameAs(evaluator2);

        var tracker1 = provider.GetRequiredService<IStartupTracker>();
        var tracker2 = provider.GetRequiredService<IStartupTracker>();
        tracker1.Should().BeSameAs(tracker2);
    }

    [Fact]
    public void AddHealthBoss_registers_keyed_signal_buffers_per_component()
    {
        var services = new ServiceCollection();

        services.AddHealthBoss(opts =>
        {
            opts.AddComponent("redis");
            opts.AddComponent("sql-server");
        });

        using var provider = services.BuildServiceProvider();

        var redisBuffer = provider.GetKeyedService<ISignalBuffer>("redis");
        var sqlBuffer = provider.GetKeyedService<ISignalBuffer>("sql-server");

        redisBuffer.Should().NotBeNull();
        sqlBuffer.Should().NotBeNull();
        redisBuffer.Should().NotBeSameAs(sqlBuffer);
    }

    [Fact]
    public void AddHealthBoss_with_no_components_registers_infrastructure_services()
    {
        var services = new ServiceCollection();

        services.AddHealthBoss(_ => { });

        using var provider = services.BuildServiceProvider();

        provider.GetService<ISystemClock>().Should().NotBeNull();
        provider.GetService<IPolicyEvaluator>().Should().NotBeNull();
        provider.GetService<IStateGraph>().Should().NotBeNull();
        provider.GetService<ITransitionEngine>().Should().NotBeNull();
        provider.GetService<IStartupTracker>().Should().NotBeNull();
    }

    [Fact]
    public void AddHealthBoss_null_services_throws_ArgumentNullException()
    {
        IServiceCollection services = null!;

        var act = () => services.AddHealthBoss(_ => { });

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("services");
    }

    [Fact]
    public void AddHealthBoss_null_configure_throws_ArgumentNullException()
    {
        var services = new ServiceCollection();

        var act = () => services.AddHealthBoss(null!);

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("configure");
    }

    // ──────────────────────────────────────────────
    // Fluent API — ComponentBuilder builds correct HealthPolicy
    // ──────────────────────────────────────────────

    [Fact]
    public void Fluent_api_builds_correct_health_policy()
    {
        HealthBossOptions? capturedOptions = null;

        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.AddComponent("my-service", c => c
                .Window(TimeSpan.FromMinutes(10))
                .HealthyAbove(0.95)
                .DegradedAbove(0.6)
                .MinimumSignals(10));
            capturedOptions = opts;
        });

        var registration = capturedOptions!.Components["my-service"];
        var policy = registration.Policy;

        policy.SlidingWindow.Should().Be(TimeSpan.FromMinutes(10));
        policy.DegradedThreshold.Should().Be(0.95);
        policy.CircuitOpenThreshold.Should().Be(0.6);
        policy.MinSignalsForEvaluation.Should().Be(10);
        policy.ResponseTime.Should().BeNull();
    }

    [Fact]
    public void Fluent_api_method_chaining_returns_same_builder()
    {
        var builder = new ComponentBuilder();

        var result = builder
            .Window(TimeSpan.FromMinutes(1))
            .HealthyAbove(0.8)
            .DegradedAbove(0.4)
            .MinimumSignals(3);

        result.Should().BeSameAs(builder);
    }

    // ──────────────────────────────────────────────
    // WithResponseTime builds correct ResponseTimePolicy
    // ──────────────────────────────────────────────

    [Fact]
    public void WithResponseTime_builds_correct_policy()
    {
        HealthBossOptions? capturedOptions = null;

        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.AddComponent("api-gateway", c => c
                .WithResponseTime(rt => rt
                    .Percentile(0.99)
                    .DegradedAfter(TimeSpan.FromMilliseconds(200))
                    .UnhealthyAfter(TimeSpan.FromMilliseconds(1000))
                    .MinimumSignals(10)));
            capturedOptions = opts;
        });

        var policy = capturedOptions!.Components["api-gateway"].Policy;
        var rtPolicy = policy.ResponseTime;

        rtPolicy.Should().NotBeNull();
        rtPolicy!.Percentile.Should().Be(0.99);
        rtPolicy.DegradedThreshold.Should().Be(TimeSpan.FromMilliseconds(200));
        rtPolicy.UnhealthyThreshold.Should().Be(TimeSpan.FromMilliseconds(1000));
        rtPolicy.MinimumSignals.Should().Be(10);
    }

    [Fact]
    public void WithResponseTime_without_unhealthy_threshold_leaves_it_null()
    {
        HealthBossOptions? capturedOptions = null;

        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.AddComponent("cache", c => c
                .WithResponseTime(rt => rt
                    .DegradedAfter(TimeSpan.FromMilliseconds(100))));
            capturedOptions = opts;
        });

        var rtPolicy = capturedOptions!.Components["cache"].Policy.ResponseTime;

        rtPolicy.Should().NotBeNull();
        rtPolicy!.UnhealthyThreshold.Should().BeNull();
    }

    [Fact]
    public void ResponseTimePolicyBuilder_method_chaining_returns_same_builder()
    {
        var builder = new ResponseTimePolicyBuilder();

        var result = builder
            .Percentile(0.5)
            .DegradedAfter(TimeSpan.FromMilliseconds(100))
            .UnhealthyAfter(TimeSpan.FromMilliseconds(500))
            .MinimumSignals(3);

        result.Should().BeSameAs(builder);
    }

    // ──────────────────────────────────────────────
    // Validation — fail fast at registration time
    // ──────────────────────────────────────────────

    [Fact]
    public void Invalid_component_name_throws_at_registration()
    {
        var services = new ServiceCollection();

        var act = () => services.AddHealthBoss(opts =>
        {
            opts.AddComponent("invalid name with spaces!");
        });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Empty_component_name_throws_at_registration()
    {
        var services = new ServiceCollection();

        var act = () => services.AddHealthBoss(opts =>
        {
            opts.AddComponent("");
        });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Invalid_threshold_ordering_throws_at_registration()
    {
        var services = new ServiceCollection();

        // HealthyAbove (DegradedThreshold) must be > DegradedAbove (CircuitOpenThreshold)
        var act = () => services.AddHealthBoss(opts =>
        {
            opts.AddComponent("bad-thresholds", c => c
                .HealthyAbove(0.3)
                .DegradedAbove(0.8));
        });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Zero_window_throws_at_registration()
    {
        var services = new ServiceCollection();

        var act = () => services.AddHealthBoss(opts =>
        {
            opts.AddComponent("zero-window", c => c
                .Window(TimeSpan.Zero));
        });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Negative_minimum_signals_throws_at_registration()
    {
        var services = new ServiceCollection();

        var act = () => services.AddHealthBoss(opts =>
        {
            opts.AddComponent("neg-signals", c => c
                .MinimumSignals(-1));
        });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Invalid_response_time_percentile_throws_at_registration()
    {
        var services = new ServiceCollection();

        var act = () => services.AddHealthBoss(opts =>
        {
            opts.AddComponent("bad-percentile", c => c
                .WithResponseTime(rt => rt.Percentile(1.5)));
        });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Response_time_unhealthy_below_degraded_throws_at_registration()
    {
        var services = new ServiceCollection();

        var act = () => services.AddHealthBoss(opts =>
        {
            opts.AddComponent("bad-rt", c => c
                .WithResponseTime(rt => rt
                    .DegradedAfter(TimeSpan.FromMilliseconds(500))
                    .UnhealthyAfter(TimeSpan.FromMilliseconds(200))));
        });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Response_time_zero_minimum_signals_throws_at_registration()
    {
        var services = new ServiceCollection();

        var act = () => services.AddHealthBoss(opts =>
        {
            opts.AddComponent("bad-rt-signals", c => c
                .WithResponseTime(rt => rt.MinimumSignals(0)));
        });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ──────────────────────────────────────────────
    // Default values work (minimal config)
    // ──────────────────────────────────────────────

    [Fact]
    public void Default_values_produce_valid_health_policy()
    {
        HealthBossOptions? capturedOptions = null;

        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.AddComponent("minimal");
            capturedOptions = opts;
        });

        var policy = capturedOptions!.Components["minimal"].Policy;

        policy.SlidingWindow.Should().Be(TimeSpan.FromMinutes(5));
        policy.DegradedThreshold.Should().Be(0.9);
        policy.CircuitOpenThreshold.Should().Be(0.5);
        policy.MinSignalsForEvaluation.Should().Be(5);
        policy.CooldownBeforeTransition.Should().Be(TimeSpan.FromSeconds(30));
        policy.RecoveryProbeInterval.Should().Be(TimeSpan.FromSeconds(10));
        policy.Jitter.MinDelay.Should().Be(TimeSpan.Zero);
        policy.Jitter.MaxDelay.Should().Be(TimeSpan.Zero);
        policy.ResponseTime.Should().BeNull();
    }

    [Fact]
    public void Default_response_time_policy_values_are_sensible()
    {
        HealthBossOptions? capturedOptions = null;

        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.AddComponent("with-rt-defaults", c => c
                .WithResponseTime(_ => { }));
            capturedOptions = opts;
        });

        var rtPolicy = capturedOptions!.Components["with-rt-defaults"].Policy.ResponseTime;

        rtPolicy.Should().NotBeNull();
        rtPolicy!.Percentile.Should().Be(0.95);
        rtPolicy.DegradedThreshold.Should().Be(TimeSpan.FromMilliseconds(500));
        rtPolicy.UnhealthyThreshold.Should().BeNull();
        rtPolicy.MinimumSignals.Should().Be(5);
    }

    // ──────────────────────────────────────────────
    // Custom TimeProvider injected correctly
    // ──────────────────────────────────────────────

    [Fact]
    public void Custom_TimeProvider_is_injected_into_SystemClock()
    {
        var fakeTime = new FakeTimeProvider(
            new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero));

        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.TimeProvider = fakeTime;
        });

        using var provider = services.BuildServiceProvider();
        var clock = provider.GetRequiredService<ISystemClock>();

        clock.UtcNow.Should().Be(fakeTime.GetUtcNow());
    }

    [Fact]
    public void Default_TimeProvider_uses_system_clock()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(_ => { });

        using var provider = services.BuildServiceProvider();
        var clock = provider.GetRequiredService<ISystemClock>();

        // System clock should return approximately "now"
        clock.UtcNow.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    // ──────────────────────────────────────────────
    // Aggregate delegates stored correctly
    // ──────────────────────────────────────────────

    [Fact]
    public void AggregateHealthResolver_delegate_stored_in_options()
    {
        Func<IReadOnlyList<DependencySnapshot>, HealthStatus> resolver =
            _ => HealthStatus.Healthy;

        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.AggregateHealthResolver = resolver;
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthBossOptions>>().Value;

        options.AggregateHealthResolver.Should().BeSameAs(resolver);
    }

    [Fact]
    public void AggregateReadinessResolver_delegate_stored_in_options()
    {
        Func<IReadOnlyList<DependencySnapshot>, ReadinessStatus> resolver =
            _ => ReadinessStatus.Ready;

        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.AggregateReadinessResolver = resolver;
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthBossOptions>>().Value;

        options.AggregateReadinessResolver.Should().BeSameAs(resolver);
    }

    [Fact]
    public void Null_aggregate_delegates_by_default()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(_ => { });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthBossOptions>>().Value;

        options.AggregateHealthResolver.Should().BeNull();
        options.AggregateReadinessResolver.Should().BeNull();
    }

    // ──────────────────────────────────────────────
    // Multiple components
    // ──────────────────────────────────────────────

    [Fact]
    public void Multiple_components_all_registered_with_distinct_policies()
    {
        HealthBossOptions? capturedOptions = null;

        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.AddComponent("redis", c => c
                .Window(TimeSpan.FromMinutes(3))
                .HealthyAbove(0.95));
            opts.AddComponent("postgres", c => c
                .Window(TimeSpan.FromMinutes(10))
                .HealthyAbove(0.8));
            capturedOptions = opts;
        });

        capturedOptions!.Components.Should().HaveCount(2);

        capturedOptions.Components["redis"].Policy.SlidingWindow
            .Should().Be(TimeSpan.FromMinutes(3));
        capturedOptions.Components["postgres"].Policy.SlidingWindow
            .Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Duplicate_component_name_overwrites_previous_registration()
    {
        HealthBossOptions? capturedOptions = null;

        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.AddComponent("redis", c => c.HealthyAbove(0.9));
            opts.AddComponent("redis", c => c.HealthyAbove(0.8));
            capturedOptions = opts;
        });

        capturedOptions!.Components.Should().HaveCount(1);
        capturedOptions.Components["redis"].Policy.DegradedThreshold
            .Should().Be(0.8);
    }

    // ──────────────────────────────────────────────
    // AddComponent returns options for chaining
    // ──────────────────────────────────────────────

    [Fact]
    public void AddComponent_returns_options_instance_for_chaining()
    {
        var options = new HealthBossOptions();

        var result = options.AddComponent("a").AddComponent("b");

        result.Should().BeSameAs(options);
        options.Components.Should().HaveCount(2);
    }

    // ──────────────────────────────────────────────
    // IStartupTracker behavior
    // ──────────────────────────────────────────────

    [Fact]
    public void StartupTracker_initial_status_is_Starting()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(_ => { });

        using var provider = services.BuildServiceProvider();
        var tracker = provider.GetRequiredService<IStartupTracker>();

        tracker.Status.Should().Be(StartupStatus.Starting);
    }

    [Fact]
    public void StartupTracker_MarkReady_transitions_to_Ready()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(_ => { });

        using var provider = services.BuildServiceProvider();
        var tracker = provider.GetRequiredService<IStartupTracker>();

        tracker.MarkReady();

        tracker.Status.Should().Be(StartupStatus.Ready);
    }

    [Fact]
    public void StartupTracker_MarkFailed_transitions_to_Failed()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(_ => { });

        using var provider = services.BuildServiceProvider();
        var tracker = provider.GetRequiredService<IStartupTracker>();

        tracker.MarkFailed("connection timeout");

        tracker.Status.Should().Be(StartupStatus.Failed);
    }

    // ──────────────────────────────────────────────
    // TransitionEngine wired to StateGraph
    // ──────────────────────────────────────────────

    [Fact]
    public void TransitionEngine_receives_StateGraph_from_DI()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(_ => { });

        using var provider = services.BuildServiceProvider();

        var engine = provider.GetRequiredService<ITransitionEngine>();
        var graph = provider.GetRequiredService<IStateGraph>();

        // Verify the engine works (it needs the graph internally)
        var assessment = TestFixtures.CreateAssessment(
            recommendedState: HealthState.Degraded);

        var decision = engine.Evaluate(
            HealthState.Healthy,
            assessment,
            TestFixtures.DefaultPolicy,
            TestFixtures.BaseTime);

        decision.Should().NotBeNull();
    }

    // ──────────────────────────────────────────────
    // Keyed signal buffer behavior
    // ──────────────────────────────────────────────

    [Fact]
    public void Keyed_signal_buffer_uses_injected_clock()
    {
        var fakeTime = new FakeTimeProvider(
            new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero));

        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.TimeProvider = fakeTime;
            opts.AddComponent("redis");
        });

        using var provider = services.BuildServiceProvider();
        var buffer = provider.GetRequiredKeyedService<ISignalBuffer>("redis");

        // Record a signal and verify the buffer works with the fake clock
        var signal = TestFixtures.CreateSignal(timestamp: fakeTime.GetUtcNow());
        buffer.Record(signal);
        buffer.Count.Should().Be(1);
    }

    [Fact]
    public void Unregistered_component_key_returns_null()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.AddComponent("redis");
        });

        using var provider = services.BuildServiceProvider();
        var buffer = provider.GetKeyedService<ISignalBuffer>("nonexistent");

        buffer.Should().BeNull();
    }

    // ──────────────────────────────────────────────
    // ValidateResponseTimePolicy edge cases
    // ──────────────────────────────────────────────

    [Fact]
    public void ResponseTimePolicy_percentile_at_zero_throws()
    {
        var services = new ServiceCollection();

        var act = () => services.AddHealthBoss(opts =>
        {
            opts.AddComponent("bad", c => c
                .WithResponseTime(rt => rt.Percentile(0.0)));
        });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ResponseTimePolicy_percentile_at_one_throws()
    {
        var services = new ServiceCollection();

        var act = () => services.AddHealthBoss(opts =>
        {
            opts.AddComponent("bad", c => c
                .WithResponseTime(rt => rt.Percentile(1.0)));
        });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ResponseTimePolicy_zero_degraded_threshold_throws()
    {
        var services = new ServiceCollection();

        var act = () => services.AddHealthBoss(opts =>
        {
            opts.AddComponent("bad", c => c
                .WithResponseTime(rt => rt.DegradedAfter(TimeSpan.Zero)));
        });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ResponseTimePolicy_unhealthy_equal_to_degraded_throws()
    {
        var services = new ServiceCollection();
        var threshold = TimeSpan.FromMilliseconds(500);

        var act = () => services.AddHealthBoss(opts =>
        {
            opts.AddComponent("bad", c => c
                .WithResponseTime(rt => rt
                    .DegradedAfter(threshold)
                    .UnhealthyAfter(threshold)));
        });

        act.Should().Throw<ArgumentException>();
    }
}

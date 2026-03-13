using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Tests;

public sealed class ValidationTests
{
    [Theory]
    [InlineData("my-service")]
    [InlineData("service_1")]
    [InlineData("a")]
    [InlineData("redis-cache-01")]
    [InlineData("UPPERCASE")]
    public void ValidateDependencyId_accepts_valid_names(string value)
    {
        var act = () => HealthBossValidator.ValidateDependencyId(value);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateDependencyId_rejects_null_or_empty(string? value)
    {
        var act = () => HealthBossValidator.ValidateDependencyId(value!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ValidateDependencyId_rejects_over_200_chars()
    {
        var longName = new string('a', 201);
        var act = () => HealthBossValidator.ValidateDependencyId(longName);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("has.dots")]
    [InlineData("has/slashes")]
    [InlineData("has@special")]
    public void ValidateDependencyId_rejects_invalid_chars(string value)
    {
        var act = () => HealthBossValidator.ValidateDependencyId(value);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ValidateDependencyId_accepts_exactly_200_chars()
    {
        var maxName = new string('a', 200);
        var act = () => HealthBossValidator.ValidateDependencyId(maxName);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateHealthPolicy_accepts_valid_policy()
    {
        var act = () => HealthBossValidator.ValidateHealthPolicy(TestFixtures.DefaultPolicy);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateHealthPolicy_rejects_degraded_below_circuit_open()
    {
        // DegradedThreshold must be > CircuitOpenThreshold
        var policy = TestFixtures.DefaultPolicy with
        {
            DegradedThreshold = 0.3,
            CircuitOpenThreshold = 0.5,
        };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ValidateHealthPolicy_rejects_thresholds_out_of_range()
    {
        var policy = TestFixtures.DefaultPolicy with { DegradedThreshold = 1.5 };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ValidateHealthPolicy_rejects_negative_threshold()
    {
        var policy = TestFixtures.DefaultPolicy with { CircuitOpenThreshold = -0.1 };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ValidateHealthPolicy_rejects_zero_window()
    {
        var policy = TestFixtures.DefaultPolicy with { SlidingWindow = TimeSpan.Zero };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ValidateHealthPolicy_rejects_negative_min_signals()
    {
        var policy = TestFixtures.DefaultPolicy with { MinSignalsForEvaluation = -1 };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ValidateJitterConfig_accepts_valid_config()
    {
        var config = new JitterConfig(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));
        var act = () => HealthBossValidator.ValidateJitterConfig(config);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateJitterConfig_rejects_negative_min()
    {
        var config = new JitterConfig(TimeSpan.FromMilliseconds(-1), TimeSpan.FromMilliseconds(500));
        var act = () => HealthBossValidator.ValidateJitterConfig(config);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ValidateJitterConfig_rejects_max_less_than_min()
    {
        var config = new JitterConfig(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(100));
        var act = () => HealthBossValidator.ValidateJitterConfig(config);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ValidateJitterConfig_accepts_equal_min_and_max()
    {
        var config = new JitterConfig(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        var act = () => HealthBossValidator.ValidateJitterConfig(config);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateJitterConfig_accepts_zero_min_and_max()
    {
        var config = new JitterConfig(TimeSpan.Zero, TimeSpan.Zero);
        var act = () => HealthBossValidator.ValidateJitterConfig(config);
        act.Should().NotThrow();
    }

    // ──────────────────────────────────────────────────────────────────
    // Fix 3: CooldownBeforeTransition & RecoveryProbeInterval validation
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateHealthPolicy_rejects_negative_cooldown()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            CooldownBeforeTransition = TimeSpan.FromSeconds(-1),
        };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ValidateHealthPolicy_accepts_zero_cooldown()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            CooldownBeforeTransition = TimeSpan.Zero,
        };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateHealthPolicy_rejects_zero_recovery_probe_interval()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            RecoveryProbeInterval = TimeSpan.Zero,
        };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ValidateHealthPolicy_rejects_negative_recovery_probe_interval()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            RecoveryProbeInterval = TimeSpan.FromSeconds(-1),
        };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ValidateHealthPolicy_accepts_positive_recovery_probe_interval()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            RecoveryProbeInterval = TimeSpan.FromSeconds(1),
        };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().NotThrow();
    }

    // ──────────────────────────────────────────────────────────────────
    // Fix 4: SanitizeString tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void SanitizeString_null_returns_null()
    {
        HealthBossValidator.SanitizeString(null).Should().BeNull();
    }

    [Fact]
    public void SanitizeString_normal_string_unchanged()
    {
        HealthBossValidator.SanitizeString("hello world").Should().Be("hello world");
    }

    [Fact]
    public void SanitizeString_strips_control_characters()
    {
        var input = "hello\x00world\x07test";
        HealthBossValidator.SanitizeString(input).Should().Be("helloworldtest");
    }

    [Fact]
    public void SanitizeString_preserves_tabs_and_spaces()
    {
        var input = "hello\tworld test";
        HealthBossValidator.SanitizeString(input).Should().Be("hello\tworld test");
    }

    [Fact]
    public void SanitizeString_truncates_oversized_string()
    {
        var input = new string('a', 2000);
        var result = HealthBossValidator.SanitizeString(input, maxLength: 100);
        result.Should().HaveLength(100);
    }

    [Fact]
    public void SanitizeString_empty_returns_empty()
    {
        HealthBossValidator.SanitizeString("").Should().Be("");
    }

    [Fact]
    public void SanitizeString_clean_string_returns_same_reference()
    {
        // Fast-path: no control characters → should return the same string instance (zero allocation)
        var input = "clean-string-no-control-chars";
        var result = HealthBossValidator.SanitizeString(input);
        result.Should().BeSameAs(input);
    }

    [Fact]
    public void SanitizeString_tabs_and_spaces_only_returns_same_reference()
    {
        // Tabs and spaces are allowed — fast-path should still return same reference
        var input = "hello\tworld test";
        var result = HealthBossValidator.SanitizeString(input);
        result.Should().BeSameAs(input);
    }

    // ──────────────────────────────────────────────────────────────────
    // Fix 4: ValidateTenantId tests
    // ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("my-tenant")]
    [InlineData("tenant_1")]
    [InlineData("a")]
    [InlineData("UPPERCASE")]
    public void ValidateTenantId_accepts_valid_names(string value)
    {
        var act = () => HealthBossValidator.ValidateTenantId(value);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateTenantId_rejects_null_or_empty(string? value)
    {
        var act = () => HealthBossValidator.ValidateTenantId(value!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ValidateTenantId_rejects_over_128_chars()
    {
        var longName = new string('a', 129);
        var act = () => HealthBossValidator.ValidateTenantId(longName);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ValidateTenantId_accepts_exactly_128_chars()
    {
        var maxName = new string('a', 128);
        var act = () => HealthBossValidator.ValidateTenantId(maxName);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("has.dots")]
    [InlineData("has/slashes")]
    public void ValidateTenantId_rejects_invalid_chars(string value)
    {
        var act = () => HealthBossValidator.ValidateTenantId(value);
        act.Should().Throw<ArgumentException>();
    }

    // ──────────────────────────────────────────────────────────────────
    // Fix 2: DependencyId / TenantId constructor validation tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void DependencyId_constructor_validates()
    {
        var act = () => new DependencyId(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DependencyId_Create_validates()
    {
        var act = () => DependencyId.Create("has spaces");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DependencyId_constructor_accepts_valid()
    {
        var id = new DependencyId("valid-id");
        id.Value.Should().Be("valid-id");
    }

    [Fact]
    public void TenantId_constructor_validates()
    {
        var act = () => new TenantId(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TenantId_Create_validates()
    {
        var act = () => TenantId.Create("has spaces");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TenantId_constructor_accepts_valid()
    {
        var id = new TenantId("valid-tenant");
        id.Value.Should().Be("valid-tenant");
    }

    // ──────────────────────────────────────────────────────────────────
    // Fix 6: Null-guard tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void SignalBuffer_Constructor_ThrowsOnNullClock()
    {
        var act = () => new SignalBuffer(null!, maxCapacity: 100);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void TransitionEngine_Constructor_ThrowsOnNullStateGraph()
    {
        var act = () => new TransitionEngine(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SystemClock_Constructor_ThrowsOnNullTimeProvider()
    {
        var act = () => new SystemClock(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void PolicyEvaluator_Evaluate_ThrowsOnNullSignals()
    {
        var evaluator = new PolicyEvaluator();
        var act = () => evaluator.Evaluate(
            null!, TestFixtures.DefaultPolicy, HealthState.Healthy, TestFixtures.BaseTime);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void PolicyEvaluator_Evaluate_ThrowsOnNullPolicy()
    {
        var evaluator = new PolicyEvaluator();
        var act = () => evaluator.Evaluate(
            new List<HealthSignal>(), null!, HealthState.Healthy, TestFixtures.BaseTime);
        act.Should().Throw<ArgumentNullException>();
    }

    // ──────────────────────────────────────────────────────────────────
    // Fix 1: default(DependencyId) / default(TenantId) sentinel tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Default_DependencyId_IsDefault_returns_true()
    {
        var id = default(DependencyId);
        id.IsDefault.Should().BeTrue();
    }

    [Fact]
    public void Default_DependencyId_ToString_returns_empty_string()
    {
        var id = default(DependencyId);
        id.ToString().Should().Be(string.Empty);
    }

    [Fact]
    public void Default_TenantId_IsDefault_returns_true()
    {
        var id = default(TenantId);
        id.IsDefault.Should().BeTrue();
    }

    [Fact]
    public void Default_TenantId_ToString_returns_empty_string()
    {
        var id = default(TenantId);
        id.ToString().Should().Be(string.Empty);
    }

    [Fact]
    public void Validated_DependencyId_IsDefault_returns_false()
    {
        var id = new DependencyId("valid-id");
        id.IsDefault.Should().BeFalse();
    }

    [Fact]
    public void Validated_TenantId_IsDefault_returns_false()
    {
        var id = new TenantId("valid-tenant");
        id.IsDefault.Should().BeFalse();
    }

    // ──────────────────────────────────────────────────────────────────
    // Fix 2: HealthSignal sanitization tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void HealthSignal_strips_control_chars_from_Metadata()
    {
        var signal = new HealthSignal(
            timestamp: TestFixtures.BaseTime,
            dependencyId: TestFixtures.DefaultDependencyId,
            outcome: SignalOutcome.Success,
            latency: TimeSpan.FromMilliseconds(50),
            metadata: "key=value\x00injected");

        signal.Metadata.Should().Be("key=valueinjected");
    }

    [Fact]
    public void HealthSignal_truncates_oversized_GrpcStatus_to_128()
    {
        var oversized = new string('g', 200);

        var signal = new HealthSignal(
            timestamp: TestFixtures.BaseTime,
            dependencyId: TestFixtures.DefaultDependencyId,
            outcome: SignalOutcome.Success,
            latency: TimeSpan.FromMilliseconds(50),
            grpcStatus: oversized);

        signal.GrpcStatus.Should().HaveLength(128);
    }

    [Fact]
    public void HealthSignal_null_Metadata_stays_null()
    {
        var signal = new HealthSignal(
            timestamp: TestFixtures.BaseTime,
            dependencyId: TestFixtures.DefaultDependencyId,
            outcome: SignalOutcome.Success,
            latency: TimeSpan.FromMilliseconds(50),
            metadata: null);

        signal.Metadata.Should().BeNull();
    }

    // ──────────────────────────────────────────────────────────────────
    // Fix 4: Null-guard tests for Record() and Evaluate()
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void SignalBuffer_Record_ThrowsOnNullSignal()
    {
        var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        timeProvider.SetUtcNow(TestFixtures.BaseTime);
        var clock = new SystemClock(timeProvider);
        var buffer = new SignalBuffer(clock, maxCapacity: 100);

        var act = () => buffer.Record(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void TransitionEngine_Evaluate_ThrowsOnNullAssessment()
    {
        var engine = new TransitionEngine(new DefaultStateGraph());

        var act = () => engine.Evaluate(
            HealthState.Healthy, null!, TestFixtures.DefaultPolicy, TestFixtures.BaseTime);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void TransitionEngine_Evaluate_ThrowsOnNullPolicy()
    {
        var engine = new TransitionEngine(new DefaultStateGraph());
        var assessment = TestFixtures.CreateAssessment();

        var act = () => engine.Evaluate(
            HealthState.Healthy, assessment, null!, TestFixtures.BaseTime);
        act.Should().Throw<ArgumentNullException>();
    }

    // ──────────────────────────────────────────────────────────────────
    // Fix 5: SanitizeString newline stripping test
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void SanitizeString_strips_newlines_and_carriage_returns()
    {
        var result = HealthBossValidator.SanitizeString("line1\nline2\r\nline3");
        result.Should().Be("line1line2line3");
    }

    // ──────────────────────────────────────────────────────────────────
    // AC23: ResponseTimePolicy validation
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateResponseTimePolicy_accepts_valid_config()
    {
        var policy = TestFixtures.DefaultResponseTimePolicy;
        var act = () => HealthBossValidator.ValidateResponseTimePolicy(policy);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateResponseTimePolicy_accepts_no_unhealthy_threshold()
    {
        var policy = new ResponseTimePolicy(
            DegradedThreshold: TimeSpan.FromMilliseconds(200),
            UnhealthyThreshold: null);

        var act = () => HealthBossValidator.ValidateResponseTimePolicy(policy);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public void ValidateResponseTimePolicy_rejects_invalid_percentile(double percentile)
    {
        var policy = new ResponseTimePolicy(
            DegradedThreshold: TimeSpan.FromMilliseconds(200),
            Percentile: percentile);

        var act = () => HealthBossValidator.ValidateResponseTimePolicy(policy);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.50)]
    [InlineData(0.95)]
    [InlineData(0.99)]
    [InlineData(0.999)]
    public void ValidateResponseTimePolicy_accepts_valid_percentile(double percentile)
    {
        var policy = new ResponseTimePolicy(
            DegradedThreshold: TimeSpan.FromMilliseconds(200),
            Percentile: percentile);

        var act = () => HealthBossValidator.ValidateResponseTimePolicy(policy);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateResponseTimePolicy_rejects_zero_degraded_threshold()
    {
        var policy = new ResponseTimePolicy(
            DegradedThreshold: TimeSpan.Zero);

        var act = () => HealthBossValidator.ValidateResponseTimePolicy(policy);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ValidateResponseTimePolicy_rejects_negative_degraded_threshold()
    {
        var policy = new ResponseTimePolicy(
            DegradedThreshold: TimeSpan.FromMilliseconds(-100));

        var act = () => HealthBossValidator.ValidateResponseTimePolicy(policy);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ValidateResponseTimePolicy_rejects_unhealthy_equal_to_degraded()
    {
        var policy = new ResponseTimePolicy(
            DegradedThreshold: TimeSpan.FromMilliseconds(200),
            UnhealthyThreshold: TimeSpan.FromMilliseconds(200));

        var act = () => HealthBossValidator.ValidateResponseTimePolicy(policy);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ValidateResponseTimePolicy_rejects_unhealthy_less_than_degraded()
    {
        var policy = new ResponseTimePolicy(
            DegradedThreshold: TimeSpan.FromMilliseconds(200),
            UnhealthyThreshold: TimeSpan.FromMilliseconds(100));

        var act = () => HealthBossValidator.ValidateResponseTimePolicy(policy);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ValidateResponseTimePolicy_rejects_zero_minimum_signals()
    {
        var policy = new ResponseTimePolicy(
            DegradedThreshold: TimeSpan.FromMilliseconds(200),
            MinimumSignals: 0);

        var act = () => HealthBossValidator.ValidateResponseTimePolicy(policy);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ValidateResponseTimePolicy_rejects_negative_minimum_signals()
    {
        var policy = new ResponseTimePolicy(
            DegradedThreshold: TimeSpan.FromMilliseconds(200),
            MinimumSignals: -1);

        var act = () => HealthBossValidator.ValidateResponseTimePolicy(policy);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ValidateResponseTimePolicy_accepts_minimum_signals_one()
    {
        var policy = new ResponseTimePolicy(
            DegradedThreshold: TimeSpan.FromMilliseconds(200),
            MinimumSignals: 1);

        var act = () => HealthBossValidator.ValidateResponseTimePolicy(policy);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateHealthPolicy_validates_embedded_response_time_policy()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = new ResponseTimePolicy(
                DegradedThreshold: TimeSpan.FromMilliseconds(200),
                Percentile: 1.5), // invalid!
        };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ValidateHealthPolicy_accepts_policy_with_valid_response_time()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            ResponseTime = TestFixtures.DefaultResponseTimePolicy,
        };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateHealthPolicy_accepts_policy_without_response_time()
    {
        var act = () => HealthBossValidator.ValidateHealthPolicy(TestFixtures.DefaultPolicy);
        act.Should().NotThrow();
    }

    // ──────────────────────────────────────────────
    // JitterConfig: MaxDelay <= CooldownBeforeTransition
    // ──────────────────────────────────────────────

    [Fact]
    public void ValidateHealthPolicy_rejects_jitter_max_exceeding_cooldown()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            CooldownBeforeTransition = TimeSpan.FromSeconds(5),
            Jitter = new JitterConfig(TimeSpan.Zero, TimeSpan.FromSeconds(10)),
        };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().Throw<ArgumentException>()
           .WithMessage("*jitter*cooldown*");
    }

    [Fact]
    public void ValidateHealthPolicy_accepts_jitter_max_equal_to_cooldown()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            CooldownBeforeTransition = TimeSpan.FromSeconds(10),
            Jitter = new JitterConfig(TimeSpan.Zero, TimeSpan.FromSeconds(10)),
        };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateHealthPolicy_skips_jitter_cooldown_check_when_cooldown_is_zero()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            CooldownBeforeTransition = TimeSpan.Zero,
            Jitter = new JitterConfig(TimeSpan.Zero, TimeSpan.Zero),
        };

        var act = () => HealthBossValidator.ValidateHealthPolicy(policy);
        act.Should().NotThrow();
    }
}

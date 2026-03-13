using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Tests;

public sealed class TimerBudgetValidatorTests
{
    private readonly ITimerBudgetValidator _validator = new TimerBudgetValidator();

    private static IReadOnlyDictionary<DependencyId, HealthPolicy> SinglePolicy(
        HealthPolicy? policy = null,
        string name = "test-dep")
    {
        return new Dictionary<DependencyId, HealthPolicy>
        {
            [new DependencyId(name)] = policy ?? TestFixtures.DefaultPolicy,
        };
    }

    // ──────────────────────────────────────────────
    // AC30: Valid budget → no warnings
    // ──────────────────────────────────────────────

    [Fact]
    public void Validate_valid_budget_returns_no_warnings()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            CooldownBeforeTransition = TimeSpan.FromSeconds(30),
            RecoveryProbeInterval = TimeSpan.FromSeconds(10),
            Jitter = new JitterConfig(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500)),
        };

        var options = new TimerBudgetOptions(
            TerminationGracePeriod: TimeSpan.FromSeconds(60),
            LivenessFailureWindow: TimeSpan.FromSeconds(120),
            ShutdownTimeout: TimeSpan.FromSeconds(30),
            DrainTimeout: TimeSpan.FromSeconds(15),
            RecoveryRetryCount: 3,
            ForceShutdownTimeout: TimeSpan.FromSeconds(10));

        var warnings = _validator.Validate(SinglePolicy(policy), options);

        warnings.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────
    // Rule 1: RecoveryRetryCount × RecoveryProbeInterval + DrainTimeout > LivenessFailureWindow → WARN
    // ──────────────────────────────────────────────

    [Fact]
    public void Validate_recovery_budget_exceeds_liveness_window_returns_warning()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            RecoveryProbeInterval = TimeSpan.FromSeconds(10),
        };

        // 5 retries × 10s + 20s drain = 70s > 60s liveness window
        var options = new TimerBudgetOptions(
            LivenessFailureWindow: TimeSpan.FromSeconds(60),
            DrainTimeout: TimeSpan.FromSeconds(20),
            RecoveryRetryCount: 5);

        var warnings = _validator.Validate(SinglePolicy(policy), options);

        warnings.Should().ContainSingle(w =>
            w.RuleName == "LivenessWindowExceeded" &&
            w.Message.Contains("liveness", StringComparison.OrdinalIgnoreCase) &&
            w.ConfigPath.Contains("test-dep") &&
            !w.IsCritical);
    }

    // ──────────────────────────────────────────────
    // Rule 2: DrainTimeout + ForceShutdownTimeout > TerminationGracePeriod → WARN
    // ──────────────────────────────────────────────

    [Fact]
    public void Validate_drain_plus_shutdown_exceeds_termination_grace_returns_warning()
    {
        var policy = TestFixtures.DefaultPolicy;

        // 20s drain + 20s force = 40s > 30s grace
        var options = new TimerBudgetOptions(
            TerminationGracePeriod: TimeSpan.FromSeconds(30),
            DrainTimeout: TimeSpan.FromSeconds(20),
            ForceShutdownTimeout: TimeSpan.FromSeconds(20));

        var warnings = _validator.Validate(SinglePolicy(policy), options);

        warnings.Should().ContainSingle(w =>
            w.RuleName == "TerminationGracePeriodExceeded" &&
            w.Message.Contains("SIGKILL", StringComparison.OrdinalIgnoreCase) &&
            !w.IsCritical);
    }

    // ──────────────────────────────────────────────
    // Rule 3: CooldownBeforeTransition < RecoveryProbeInterval → WARN
    // ──────────────────────────────────────────────

    [Fact]
    public void Validate_cooldown_below_probe_interval_returns_warning()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            CooldownBeforeTransition = TimeSpan.FromSeconds(5),
            RecoveryProbeInterval = TimeSpan.FromSeconds(10),
        };

        var options = new TimerBudgetOptions();

        var warnings = _validator.Validate(SinglePolicy(policy), options);

        warnings.Should().ContainSingle(w =>
            w.RuleName == "CooldownBelowProbeInterval" &&
            w.Message.Contains("probe", StringComparison.OrdinalIgnoreCase) &&
            w.ConfigPath.Contains("test-dep") &&
            !w.IsCritical);
    }

    // ──────────────────────────────────────────────
    // Rule 4: Jitter.MaxDelay > CooldownBeforeTransition / 2 → WARN
    // ──────────────────────────────────────────────

    [Fact]
    public void Validate_jitter_dominates_cooldown_returns_warning()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            CooldownBeforeTransition = TimeSpan.FromSeconds(10),
            Jitter = new JitterConfig(TimeSpan.Zero, TimeSpan.FromSeconds(6)), // 6s > 10s/2 = 5s
        };

        var options = new TimerBudgetOptions();

        var warnings = _validator.Validate(SinglePolicy(policy), options);

        warnings.Should().ContainSingle(w =>
            w.RuleName == "JitterDominatesCooldown" &&
            w.Message.Contains("jitter", StringComparison.OrdinalIgnoreCase) &&
            w.ConfigPath.Contains("test-dep") &&
            !w.IsCritical);
    }

    // ──────────────────────────────────────────────
    // Unknown K8s values (0) → skip that check
    // ──────────────────────────────────────────────

    [Fact]
    public void Validate_unknown_liveness_window_skips_rule_1()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            RecoveryProbeInterval = TimeSpan.FromSeconds(10),
        };

        // LivenessFailureWindow = 0 (unknown) → skip rule 1
        var options = new TimerBudgetOptions(
            DrainTimeout: TimeSpan.FromSeconds(100),
            RecoveryRetryCount: 100);

        var warnings = _validator.Validate(SinglePolicy(policy), options);

        warnings.Should().NotContain(w => w.RuleName == "LivenessWindowExceeded");
    }

    [Fact]
    public void Validate_unknown_termination_grace_skips_rule_2()
    {
        // TerminationGracePeriod = 0 (unknown) → skip rule 2
        var options = new TimerBudgetOptions(
            DrainTimeout: TimeSpan.FromSeconds(100),
            ForceShutdownTimeout: TimeSpan.FromSeconds(100));

        var warnings = _validator.Validate(SinglePolicy(), options);

        warnings.Should().NotContain(w => w.RuleName == "TerminationGracePeriodExceeded");
    }

    [Fact]
    public void Validate_unknown_drain_timeout_skips_rules_1_and_2()
    {
        // DrainTimeout = 0 (unknown) → skip rules 1 and 2
        var options = new TimerBudgetOptions(
            LivenessFailureWindow: TimeSpan.FromSeconds(10),
            TerminationGracePeriod: TimeSpan.FromSeconds(10),
            RecoveryRetryCount: 100,
            ForceShutdownTimeout: TimeSpan.FromSeconds(100));

        var warnings = _validator.Validate(SinglePolicy(), options);

        warnings.Should().NotContain(w =>
            w.RuleName == "LivenessWindowExceeded" ||
            w.RuleName == "TerminationGracePeriodExceeded");
    }

    [Fact]
    public void Validate_unknown_recovery_retry_count_skips_rule_1()
    {
        // RecoveryRetryCount = 0 (unknown) → skip rule 1
        var options = new TimerBudgetOptions(
            LivenessFailureWindow: TimeSpan.FromSeconds(10),
            DrainTimeout: TimeSpan.FromSeconds(100));

        var warnings = _validator.Validate(SinglePolicy(), options);

        warnings.Should().NotContain(w => w.RuleName == "LivenessWindowExceeded");
    }

    [Fact]
    public void Validate_zero_cooldown_skips_rules_3_and_4()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            CooldownBeforeTransition = TimeSpan.Zero,
            Jitter = new JitterConfig(TimeSpan.Zero, TimeSpan.Zero),
        };

        var options = new TimerBudgetOptions();

        var warnings = _validator.Validate(SinglePolicy(policy), options);

        warnings.Should().NotContain(w =>
            w.RuleName == "CooldownBelowProbeInterval" ||
            w.RuleName == "JitterDominatesCooldown");
    }

    // ──────────────────────────────────────────────
    // Multiple policies → validates each independently
    // ──────────────────────────────────────────────

    [Fact]
    public void Validate_multiple_policies_validates_each_independently()
    {
        var goodPolicy = TestFixtures.DefaultPolicy with
        {
            CooldownBeforeTransition = TimeSpan.FromSeconds(30),
            RecoveryProbeInterval = TimeSpan.FromSeconds(10),
            Jitter = new JitterConfig(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500)),
        };

        var badPolicy = TestFixtures.DefaultPolicy with
        {
            CooldownBeforeTransition = TimeSpan.FromSeconds(5),
            RecoveryProbeInterval = TimeSpan.FromSeconds(10),
        };

        var policies = new Dictionary<DependencyId, HealthPolicy>
        {
            [new DependencyId("good-dep")] = goodPolicy,
            [new DependencyId("bad-dep")] = badPolicy,
        };

        var options = new TimerBudgetOptions();

        var warnings = _validator.Validate(policies, options);

        warnings.Should().OnlyContain(w => w.ConfigPath.Contains("bad-dep"));
        warnings.Should().NotContain(w => w.ConfigPath.Contains("good-dep"));
    }

    [Fact]
    public void Validate_multiple_bad_policies_collects_all_warnings()
    {
        var badPolicy1 = TestFixtures.DefaultPolicy with
        {
            CooldownBeforeTransition = TimeSpan.FromSeconds(5),
            RecoveryProbeInterval = TimeSpan.FromSeconds(10),
        };

        var badPolicy2 = TestFixtures.DefaultPolicy with
        {
            CooldownBeforeTransition = TimeSpan.FromSeconds(3),
            RecoveryProbeInterval = TimeSpan.FromSeconds(10),
        };

        var policies = new Dictionary<DependencyId, HealthPolicy>
        {
            [new DependencyId("dep-a")] = badPolicy1,
            [new DependencyId("dep-b")] = badPolicy2,
        };

        var options = new TimerBudgetOptions();

        var warnings = _validator.Validate(policies, options);

        warnings.Should().Contain(w => w.ConfigPath.Contains("dep-a"));
        warnings.Should().Contain(w => w.ConfigPath.Contains("dep-b"));
    }

    // ──────────────────────────────────────────────
    // Empty dictionary → no warnings
    // ──────────────────────────────────────────────

    [Fact]
    public void Validate_empty_policies_returns_no_warnings()
    {
        var options = new TimerBudgetOptions();
        var policies = new Dictionary<DependencyId, HealthPolicy>();

        var warnings = _validator.Validate(policies, options);

        warnings.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────
    // Null arguments → throws
    // ──────────────────────────────────────────────

    [Fact]
    public void Validate_null_policies_throws_ArgumentNullException()
    {
        var act = () => _validator.Validate(null!, new TimerBudgetOptions());

        act.Should().Throw<ArgumentNullException>().WithParameterName("policies");
    }

    [Fact]
    public void Validate_null_options_throws_ArgumentNullException()
    {
        var act = () => _validator.Validate(
            new Dictionary<DependencyId, HealthPolicy>(), null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("options");
    }

    // ──────────────────────────────────────────────
    // Exact boundary conditions
    // ──────────────────────────────────────────────

    [Fact]
    public void Validate_recovery_budget_exactly_equals_liveness_window_no_warning()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            RecoveryProbeInterval = TimeSpan.FromSeconds(10),
        };

        // 3 × 10s + 10s = 40s == 40s liveness window → no warning (not strictly greater)
        var options = new TimerBudgetOptions(
            LivenessFailureWindow: TimeSpan.FromSeconds(40),
            DrainTimeout: TimeSpan.FromSeconds(10),
            RecoveryRetryCount: 3);

        var warnings = _validator.Validate(SinglePolicy(policy), options);

        warnings.Should().NotContain(w => w.RuleName == "LivenessWindowExceeded");
    }

    [Fact]
    public void Validate_drain_plus_shutdown_exactly_equals_grace_no_warning()
    {
        // 15s + 15s = 30s == 30s grace → no warning
        var options = new TimerBudgetOptions(
            TerminationGracePeriod: TimeSpan.FromSeconds(30),
            DrainTimeout: TimeSpan.FromSeconds(15),
            ForceShutdownTimeout: TimeSpan.FromSeconds(15));

        var warnings = _validator.Validate(SinglePolicy(), options);

        warnings.Should().NotContain(w => w.RuleName == "TerminationGracePeriodExceeded");
    }

    [Fact]
    public void Validate_cooldown_equals_probe_interval_no_warning()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            CooldownBeforeTransition = TimeSpan.FromSeconds(10),
            RecoveryProbeInterval = TimeSpan.FromSeconds(10),
        };

        var warnings = _validator.Validate(SinglePolicy(policy), new TimerBudgetOptions());

        warnings.Should().NotContain(w => w.RuleName == "CooldownBelowProbeInterval");
    }

    [Fact]
    public void Validate_jitter_exactly_half_cooldown_no_warning()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            CooldownBeforeTransition = TimeSpan.FromSeconds(10),
            Jitter = new JitterConfig(TimeSpan.Zero, TimeSpan.FromSeconds(5)), // 5s == 10s/2 → no warning
        };

        var warnings = _validator.Validate(SinglePolicy(policy), new TimerBudgetOptions());

        warnings.Should().NotContain(w => w.RuleName == "JitterDominatesCooldown");
    }

    // ──────────────────────────────────────────────
    // Warning messages contain useful detail
    // ──────────────────────────────────────────────

    [Fact]
    public void Validate_liveness_warning_includes_computed_budget()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            RecoveryProbeInterval = TimeSpan.FromSeconds(10),
        };

        // 5 × 10s + 20s = 70s > 60s
        var options = new TimerBudgetOptions(
            LivenessFailureWindow: TimeSpan.FromSeconds(60),
            DrainTimeout: TimeSpan.FromSeconds(20),
            RecoveryRetryCount: 5);

        var warnings = _validator.Validate(SinglePolicy(policy), options);
        var warning = warnings.Should().ContainSingle(w => w.RuleName == "LivenessWindowExceeded").Subject;

        warning.Message.Should().Contain("70");
        warning.Message.Should().Contain("60");
    }

    [Fact]
    public void Validate_termination_warning_includes_computed_budget()
    {
        // 25s + 20s = 45s > 30s
        var options = new TimerBudgetOptions(
            TerminationGracePeriod: TimeSpan.FromSeconds(30),
            DrainTimeout: TimeSpan.FromSeconds(25),
            ForceShutdownTimeout: TimeSpan.FromSeconds(20));

        var warnings = _validator.Validate(SinglePolicy(), options);
        var warning = warnings.Should().ContainSingle(w => w.RuleName == "TerminationGracePeriodExceeded").Subject;

        warning.Message.Should().Contain("45");
        warning.Message.Should().Contain("30");
    }

    // ──────────────────────────────────────────────
    // Multiple rules fire for same policy
    // ──────────────────────────────────────────────

    [Fact]
    public void Validate_all_rules_fire_simultaneously()
    {
        var policy = TestFixtures.DefaultPolicy with
        {
            CooldownBeforeTransition = TimeSpan.FromSeconds(4),
            RecoveryProbeInterval = TimeSpan.FromSeconds(10),
            Jitter = new JitterConfig(TimeSpan.Zero, TimeSpan.FromSeconds(3)), // 3s > 4s/2 = 2s
        };

        // Rule 1: 5 × 10s + 20s = 70s > 60s
        // Rule 2: 20s + 15s = 35s > 30s
        // Rule 3: 4s < 10s
        // Rule 4: 3s > 4s/2 = 2s
        var options = new TimerBudgetOptions(
            TerminationGracePeriod: TimeSpan.FromSeconds(30),
            LivenessFailureWindow: TimeSpan.FromSeconds(60),
            DrainTimeout: TimeSpan.FromSeconds(20),
            RecoveryRetryCount: 5,
            ForceShutdownTimeout: TimeSpan.FromSeconds(15));

        var warnings = _validator.Validate(SinglePolicy(policy), options);

        warnings.Should().HaveCount(4);
        warnings.Select(w => w.RuleName).Should().BeEquivalentTo(
            "LivenessWindowExceeded",
            "TerminationGracePeriodExceeded",
            "CooldownBelowProbeInterval",
            "JitterDominatesCooldown");
    }
}

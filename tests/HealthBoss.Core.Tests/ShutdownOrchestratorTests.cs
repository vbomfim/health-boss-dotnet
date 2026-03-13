using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HealthBoss.Core.Tests;

/// <summary>
/// TDD tests for <see cref="ShutdownOrchestrator"/> — 3-gate safety chain.
/// Follows the acceptance criteria from issue #20.
/// </summary>
public sealed class ShutdownOrchestratorTests
{
    private static readonly DateTimeOffset Now =
        new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);

    // ──────────────────────────────────────────────
    //  Test helpers
    // ──────────────────────────────────────────────

    /// <summary>Minimal fake implementing <see cref="IHealthStateReader"/>.</summary>
    private sealed class FakeHealthStateReader : IHealthStateReader
    {
        public IReadOnlyCollection<DependencySnapshot> Snapshots { get; init; } =
            Array.Empty<DependencySnapshot>();

        public int TotalSignalCount { get; init; }
        public DateTimeOffset? LastTransitionTime { get; init; }
        public HealthState CurrentState { get; init; } = HealthState.Healthy;
        public ReadinessStatus ReadinessStatus { get; init; } = ReadinessStatus.Ready;

        public IReadOnlyCollection<DependencySnapshot> GetAllSnapshots() => Snapshots;
    }

    private static FakeTimeProvider CreateClock()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(Now);
        return clock;
    }

    private static ShutdownOrchestrator CreateOrchestrator(
        ShutdownConfig config,
        FakeTimeProvider clock,
        Func<IReadOnlyCollection<DependencySnapshot>, CancellationToken, Task<bool>>? confirmDelegate = null)
    {
        var systemClock = new SystemClock(clock);
        return new ShutdownOrchestrator(
            config,
            systemClock,
            NullLogger<ShutdownOrchestrator>.Instance,
            confirmDelegate);
    }

    private static FakeHealthStateReader ReaderWith(
        int totalSignals = 200,
        DateTimeOffset? lastTransition = null)
    {
        return new FakeHealthStateReader
        {
            TotalSignalCount = totalSignals,
            LastTransitionTime = lastTransition ?? Now.AddMinutes(-5),
        };
    }

    // ──────────────────────────────────────────────
    //  AC28 — All 3 gates must pass
    // ──────────────────────────────────────────────

    [Fact]
    public async Task All_3_gates_pass_returns_approved()
    {
        var config = new ShutdownConfig(MinSignals: 10, Cooldown: TimeSpan.FromSeconds(30), RequireConfirmDelegate: true);
        var clock = CreateClock();
        Func<IReadOnlyCollection<DependencySnapshot>, CancellationToken, Task<bool>> confirmDelegate =
            (_, _) => Task.FromResult(true);

        var orchestrator = CreateOrchestrator(config, clock, confirmDelegate);
        var reader = ReaderWith(totalSignals: 100, lastTransition: Now.AddSeconds(-60));

        var decision = await orchestrator.RequestShutdownAsync(reader, CancellationToken.None);

        decision.Approved.Should().BeTrue();
        decision.Gate.Should().Be("All");
    }

    // ──────────────────────────────────────────────
    //  Gate 1: MinSignals
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Gate1_insufficient_signals_blocks_shutdown()
    {
        var config = new ShutdownConfig(MinSignals: 100, Cooldown: TimeSpan.FromSeconds(30), RequireConfirmDelegate: false);
        var clock = CreateClock();

        var orchestrator = CreateOrchestrator(config, clock);
        var reader = ReaderWith(totalSignals: 50, lastTransition: Now.AddMinutes(-5));

        var decision = await orchestrator.RequestShutdownAsync(reader, CancellationToken.None);

        decision.Approved.Should().BeFalse();
        decision.Gate.Should().Be("MinSignals");
        decision.Reason.Should().Contain("50").And.Contain("100");
    }

    [Fact]
    public void Evaluate_gate1_insufficient_signals_blocks_shutdown()
    {
        var config = new ShutdownConfig(MinSignals: 100, Cooldown: TimeSpan.FromSeconds(30), RequireConfirmDelegate: false);
        var clock = CreateClock();

        var orchestrator = CreateOrchestrator(config, clock);
        var reader = ReaderWith(totalSignals: 50, lastTransition: Now.AddMinutes(-5));

        var decision = orchestrator.Evaluate(reader);

        decision.Approved.Should().BeFalse();
        decision.Gate.Should().Be("MinSignals");
        decision.Reason.Should().Contain("MinSignals");
    }

    // ──────────────────────────────────────────────
    //  Gate 2: Cooldown
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Gate2_cooldown_not_elapsed_blocks_shutdown()
    {
        var config = new ShutdownConfig(MinSignals: 10, Cooldown: TimeSpan.FromSeconds(60), RequireConfirmDelegate: false);
        var clock = CreateClock();

        var orchestrator = CreateOrchestrator(config, clock);
        var reader = ReaderWith(totalSignals: 200, lastTransition: Now.AddSeconds(-10));

        var decision = await orchestrator.RequestShutdownAsync(reader, CancellationToken.None);

        decision.Approved.Should().BeFalse();
        decision.Gate.Should().Be("Cooldown");
        decision.Reason.Should().Contain("Cooldown");
    }

    [Fact]
    public async Task Gate2_null_last_transition_passes_cooldown()
    {
        // No transition has occurred → no cooldown to enforce → gate passes.
        var config = new ShutdownConfig(MinSignals: 10, Cooldown: TimeSpan.FromSeconds(60), RequireConfirmDelegate: false);
        var clock = CreateClock();

        var orchestrator = CreateOrchestrator(config, clock);
        var reader = new FakeHealthStateReader { TotalSignalCount = 200, LastTransitionTime = null };

        var decision = await orchestrator.RequestShutdownAsync(reader, CancellationToken.None);

        decision.Approved.Should().BeTrue();
    }

    // ──────────────────────────────────────────────
    //  Gate 3: ConfirmDelegate
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Gate3_delegate_returns_false_blocks_shutdown()
    {
        var config = new ShutdownConfig(MinSignals: 10, Cooldown: TimeSpan.FromSeconds(30), RequireConfirmDelegate: true);
        var clock = CreateClock();
        Func<IReadOnlyCollection<DependencySnapshot>, CancellationToken, Task<bool>> confirmDelegate =
            (_, _) => Task.FromResult(false);

        var orchestrator = CreateOrchestrator(config, clock, confirmDelegate);
        var reader = ReaderWith(totalSignals: 200, lastTransition: Now.AddSeconds(-60));

        var decision = await orchestrator.RequestShutdownAsync(reader, CancellationToken.None);

        decision.Approved.Should().BeFalse();
        decision.Gate.Should().Be("ConfirmDelegate");
        decision.Reason.Should().Contain("denied");
    }

    [Fact]
    public async Task Gate3_delegate_timeout_blocks_shutdown()
    {
        var config = new ShutdownConfig(MinSignals: 10, Cooldown: TimeSpan.FromSeconds(30), RequireConfirmDelegate: true);
        var clock = CreateClock();

        // Delegate that hangs forever — will be timed out after 5 s.
        Func<IReadOnlyCollection<DependencySnapshot>, CancellationToken, Task<bool>> confirmDelegate =
            async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromMinutes(10), ct);
                return true;
            };

        var orchestrator = CreateOrchestrator(config, clock, confirmDelegate);
        var reader = ReaderWith(totalSignals: 200, lastTransition: Now.AddSeconds(-60));

        var decision = await orchestrator.RequestShutdownAsync(reader, CancellationToken.None);

        decision.Approved.Should().BeFalse();
        decision.Gate.Should().Be("ConfirmDelegate");
        decision.Reason.Should().Contain("timed out");
    }

    [Fact]
    public async Task Gate3_delegate_exception_blocks_shutdown()
    {
        var config = new ShutdownConfig(MinSignals: 10, Cooldown: TimeSpan.FromSeconds(30), RequireConfirmDelegate: true);
        var clock = CreateClock();
        Func<IReadOnlyCollection<DependencySnapshot>, CancellationToken, Task<bool>> confirmDelegate =
            (_, _) => throw new InvalidOperationException("Boom");

        var orchestrator = CreateOrchestrator(config, clock, confirmDelegate);
        var reader = ReaderWith(totalSignals: 200, lastTransition: Now.AddSeconds(-60));

        var decision = await orchestrator.RequestShutdownAsync(reader, CancellationToken.None);

        decision.Approved.Should().BeFalse();
        decision.Gate.Should().Be("ConfirmDelegate");
        decision.Reason.Should().Contain("exception");
    }

    [Fact]
    public async Task Gate3_required_but_no_delegate_provided_blocks_shutdown()
    {
        var config = new ShutdownConfig(MinSignals: 10, Cooldown: TimeSpan.FromSeconds(30), RequireConfirmDelegate: true);
        var clock = CreateClock();

        // No confirmDelegate provided.
        var orchestrator = CreateOrchestrator(config, clock, confirmDelegate: null);
        var reader = ReaderWith(totalSignals: 200, lastTransition: Now.AddSeconds(-60));

        var decision = await orchestrator.RequestShutdownAsync(reader, CancellationToken.None);

        decision.Approved.Should().BeFalse();
        decision.Gate.Should().Be("ConfirmDelegate");
        decision.Reason.Should().Contain("not provided");
    }

    // ──────────────────────────────────────────────
    //  RequireConfirmDelegate = false → gate 3 skipped
    // ──────────────────────────────────────────────

    [Fact]
    public async Task RequireConfirmDelegate_false_skips_gate3()
    {
        var config = new ShutdownConfig(MinSignals: 10, Cooldown: TimeSpan.FromSeconds(30), RequireConfirmDelegate: false);
        var clock = CreateClock();
        var delegateCalled = false;
        Func<IReadOnlyCollection<DependencySnapshot>, CancellationToken, Task<bool>> confirmDelegate =
            (_, _) =>
            {
                delegateCalled = true;
                return Task.FromResult(true);
            };

        var orchestrator = CreateOrchestrator(config, clock, confirmDelegate);
        var reader = ReaderWith(totalSignals: 200, lastTransition: Now.AddSeconds(-60));

        var decision = await orchestrator.RequestShutdownAsync(reader, CancellationToken.None);

        decision.Approved.Should().BeTrue();
        delegateCalled.Should().BeFalse("gate 3 should be skipped when RequireConfirmDelegate is false");
    }

    [Fact]
    public void Evaluate_sync_with_RequireConfirmDelegate_false_can_approve()
    {
        var config = new ShutdownConfig(MinSignals: 10, Cooldown: TimeSpan.FromSeconds(30), RequireConfirmDelegate: false);
        var clock = CreateClock();

        var orchestrator = CreateOrchestrator(config, clock);
        var reader = ReaderWith(totalSignals: 200, lastTransition: Now.AddSeconds(-60));

        var decision = orchestrator.Evaluate(reader);

        decision.Approved.Should().BeTrue();
        decision.Gate.Should().Be("All");
    }

    [Fact]
    public void Evaluate_sync_with_RequireConfirmDelegate_true_blocks()
    {
        // Evaluate (sync) cannot invoke the async delegate, so it must block.
        var config = new ShutdownConfig(MinSignals: 10, Cooldown: TimeSpan.FromSeconds(30), RequireConfirmDelegate: true);
        var clock = CreateClock();

        var orchestrator = CreateOrchestrator(config, clock);
        var reader = ReaderWith(totalSignals: 200, lastTransition: Now.AddSeconds(-60));

        var decision = orchestrator.Evaluate(reader);

        decision.Approved.Should().BeFalse();
        decision.Gate.Should().Be("ConfirmDelegate");
        decision.Reason.Should().Contain("RequestShutdownAsync");
    }

    // ──────────────────────────────────────────────
    //  Default config — conservative values
    // ──────────────────────────────────────────────

    [Fact]
    public void Default_config_has_conservative_values()
    {
        var config = ShutdownConfig.Default;

        config.MinSignals.Should().Be(100);
        config.Cooldown.Should().Be(TimeSpan.FromSeconds(60));
        config.RequireConfirmDelegate.Should().BeTrue();
    }

    // ──────────────────────────────────────────────
    //  Guard: null stateReader
    // ──────────────────────────────────────────────

    [Fact]
    public void Evaluate_null_stateReader_throws_ArgumentNullException()
    {
        var config = ShutdownConfig.Default;
        var clock = CreateClock();
        var orchestrator = CreateOrchestrator(config, clock);

        var act = () => orchestrator.Evaluate(null!);

        act.Should().Throw<ArgumentNullException>()
           .WithParameterName("stateReader");
    }

    [Fact]
    public async Task RequestShutdownAsync_null_stateReader_throws_ArgumentNullException()
    {
        var config = ShutdownConfig.Default;
        var clock = CreateClock();
        var orchestrator = CreateOrchestrator(config, clock);

        var act = () => orchestrator.RequestShutdownAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>()
               .WithParameterName("stateReader");
    }

    // ──────────────────────────────────────────────
    //  Thread safety: concurrent Evaluate calls
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_evaluate_calls_are_thread_safe()
    {
        var config = new ShutdownConfig(MinSignals: 10, Cooldown: TimeSpan.FromSeconds(30), RequireConfirmDelegate: false);
        var clock = CreateClock();
        var orchestrator = CreateOrchestrator(config, clock);
        var reader = ReaderWith(totalSignals: 200, lastTransition: Now.AddSeconds(-60));

        const int concurrency = 100;
        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(() => orchestrator.Evaluate(reader)))
            .ToArray();

        var decisions = await Task.WhenAll(tasks);

        decisions.Should().AllSatisfy(d =>
        {
            d.Approved.Should().BeTrue();
            d.Gate.Should().Be("All");
        });
    }

    [Fact]
    public async Task Concurrent_RequestShutdownAsync_calls_are_thread_safe()
    {
        var config = new ShutdownConfig(MinSignals: 10, Cooldown: TimeSpan.FromSeconds(30), RequireConfirmDelegate: true);
        var clock = CreateClock();
        Func<IReadOnlyCollection<DependencySnapshot>, CancellationToken, Task<bool>> confirmDelegate =
            (_, _) => Task.FromResult(true);
        var orchestrator = CreateOrchestrator(config, clock, confirmDelegate);
        var reader = ReaderWith(totalSignals: 200, lastTransition: Now.AddSeconds(-60));

        const int concurrency = 100;
        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => orchestrator.RequestShutdownAsync(reader, CancellationToken.None))
            .ToArray();

        var decisions = await Task.WhenAll(tasks);

        decisions.Should().AllSatisfy(d =>
        {
            d.Approved.Should().BeTrue();
            d.Gate.Should().Be("All");
        });
    }

    // ──────────────────────────────────────────────
    //  Gate 3: ConfirmDelegate receives snapshots
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Gate3_delegate_receives_correct_snapshots()
    {
        var config = new ShutdownConfig(MinSignals: 1, Cooldown: TimeSpan.Zero, RequireConfirmDelegate: true);
        var clock = CreateClock();
        var snapshot = new DependencySnapshot(
            new DependencyId("svc-a"),
            HealthState.Healthy,
            TestFixtures.CreateAssessment(),
            Now.AddMinutes(-10),
            ConsecutiveFailures: 0);

        IReadOnlyCollection<DependencySnapshot>? receivedSnapshots = null;
        Func<IReadOnlyCollection<DependencySnapshot>, CancellationToken, Task<bool>> confirmDelegate =
            (snapshots, _) =>
            {
                receivedSnapshots = snapshots;
                return Task.FromResult(true);
            };

        var reader = new FakeHealthStateReader
        {
            TotalSignalCount = 200,
            LastTransitionTime = Now.AddMinutes(-5),
            Snapshots = new[] { snapshot },
        };

        var orchestrator = CreateOrchestrator(config, clock, confirmDelegate);

        await orchestrator.RequestShutdownAsync(reader, CancellationToken.None);

        receivedSnapshots.Should().NotBeNull();
        receivedSnapshots.Should().ContainSingle()
            .Which.DependencyId.Should().Be(new DependencyId("svc-a"));
    }

    // ──────────────────────────────────────────────
    //  Edge cases
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Exact_min_signals_threshold_passes_gate1()
    {
        var config = new ShutdownConfig(MinSignals: 100, Cooldown: TimeSpan.Zero, RequireConfirmDelegate: false);
        var clock = CreateClock();

        var orchestrator = CreateOrchestrator(config, clock);
        var reader = ReaderWith(totalSignals: 100, lastTransition: Now.AddMinutes(-5));

        var decision = await orchestrator.RequestShutdownAsync(reader, CancellationToken.None);

        decision.Approved.Should().BeTrue();
    }

    [Fact]
    public async Task Exact_cooldown_threshold_passes_gate2()
    {
        var config = new ShutdownConfig(MinSignals: 10, Cooldown: TimeSpan.FromSeconds(60), RequireConfirmDelegate: false);
        var clock = CreateClock();

        var orchestrator = CreateOrchestrator(config, clock);
        var reader = ReaderWith(totalSignals: 200, lastTransition: Now.AddSeconds(-60));

        var decision = await orchestrator.RequestShutdownAsync(reader, CancellationToken.None);

        decision.Approved.Should().BeTrue();
    }

    [Fact]
    public async Task Cancellation_token_propagated_to_delegate()
    {
        var config = new ShutdownConfig(MinSignals: 10, Cooldown: TimeSpan.Zero, RequireConfirmDelegate: true);
        var clock = CreateClock();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<IReadOnlyCollection<DependencySnapshot>, CancellationToken, Task<bool>> confirmDelegate =
            async (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                return true;
            };

        var orchestrator = CreateOrchestrator(config, clock, confirmDelegate);
        var reader = ReaderWith(totalSignals: 200, lastTransition: Now.AddMinutes(-5));

        var decision = await orchestrator.RequestShutdownAsync(reader, cts.Token);

        decision.Approved.Should().BeFalse();
        decision.Gate.Should().Be("ConfirmDelegate");
    }
}

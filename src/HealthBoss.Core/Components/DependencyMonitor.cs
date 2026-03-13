// <copyright file="DependencyMonitor.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Components;

/// <summary>
/// Per-dependency health monitor that owns a signal buffer, evaluates signals
/// against a configured health policy, and maintains state via the transition engine.
/// Thread-safe: <see cref="RecordSignal"/> is lock-free (delegates to ConcurrentQueue).
/// <see cref="GetSnapshot"/> acquires <c>_syncRoot</c> to serialize the compound
/// read-evaluate-transition block and prevent torn reads of <c>_lastTransitionTime</c>.
/// </summary>
internal sealed class DependencyMonitor : IDependencyMonitor
{
    private readonly ISignalBuffer _buffer;
    private readonly IPolicyEvaluator _evaluator;
    private readonly ITransitionEngine _transitionEngine;
    private readonly HealthPolicy _policy;
    private readonly ISystemClock _clock;
    private readonly object _syncRoot = new();

    private volatile HealthState _currentState;
    private volatile HealthAssessment? _latestAssessment;
    private DateTimeOffset _lastTransitionTime;
    private int _consecutiveFailures;

    /// <summary>
    /// Initializes a new instance of the <see cref="DependencyMonitor"/> class.
    /// </summary>
    /// <param name="dependencyId">The dependency this monitor tracks.</param>
    /// <param name="buffer">The signal buffer for this dependency.</param>
    /// <param name="evaluator">The policy evaluator for assessing signals.</param>
    /// <param name="transitionEngine">The engine that decides state transitions.</param>
    /// <param name="policy">The health policy for this dependency.</param>
    /// <param name="clock">The system clock for timestamps.</param>
    public DependencyMonitor(
        DependencyId dependencyId,
        ISignalBuffer buffer,
        IPolicyEvaluator evaluator,
        ITransitionEngine transitionEngine,
        HealthPolicy policy,
        ISystemClock clock)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(transitionEngine);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(clock);

        DependencyId = dependencyId;
        _buffer = buffer;
        _evaluator = evaluator;
        _transitionEngine = transitionEngine;
        _policy = policy;
        _clock = clock;
        _currentState = HealthState.Healthy;
        _lastTransitionTime = clock.UtcNow;
    }

    /// <inheritdoc />
    public DependencyId DependencyId { get; }

    /// <inheritdoc />
    public HealthState CurrentState => _currentState;

    /// <inheritdoc />
    public void RecordSignal(HealthSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        _buffer.Record(signal);

        if (signal.Outcome != SignalOutcome.Success)
        {
            Interlocked.Increment(ref _consecutiveFailures);
        }
        else
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }
    }

    /// <inheritdoc />
    public DependencySnapshot GetSnapshot()
    {
        lock (_syncRoot)
        {
            var now = _clock.UtcNow;
            var signals = _buffer.GetSignals(_policy.SlidingWindow);
            var assessment = _evaluator.Evaluate(signals, _policy, _currentState, now);

            _latestAssessment = assessment;

            var decision = _transitionEngine.Evaluate(
                _currentState, assessment, _policy, _lastTransitionTime);

            if (decision.ShouldTransition && decision.TargetState.HasValue)
            {
                _currentState = decision.TargetState.Value;
                _lastTransitionTime = now;
            }

            return new DependencySnapshot(
                DependencyId: DependencyId,
                CurrentState: _currentState,
                LatestAssessment: assessment,
                StateChangedAt: _lastTransitionTime,
                ConsecutiveFailures: Volatile.Read(ref _consecutiveFailures));
        }
    }
}

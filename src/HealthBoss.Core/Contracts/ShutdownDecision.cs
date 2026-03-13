// <copyright file="ShutdownDecision.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core.Contracts;

/// <summary>
/// Output of the shutdown orchestrator's 3-gate safety chain evaluation.
/// <para>
/// The shutdown safety chain evaluates up to three gates in order. If any gate
/// fails, evaluation stops and the decision is <b>denied</b> with the blocking
/// gate identified. Only when all evaluated gates pass is shutdown <b>approved</b>.
/// </para>
/// <para>
/// <b>Gate values:</b>
/// <list type="table">
///   <listheader><term>Gate</term><description>Meaning</description></listheader>
///   <item>
///     <term><c>"MinSignals"</c></term>
///     <description>
///       Gate 1 denied shutdown — not enough health signals have been observed to
///       make a trustworthy decision. The system is still warming up.
///     </description>
///   </item>
///   <item>
///     <term><c>"Cooldown"</c></term>
///     <description>
///       Gate 2 denied shutdown — insufficient time has elapsed since the last
///       state transition. Prevents premature shutdown during transient flapping.
///     </description>
///   </item>
///   <item>
///     <term><c>"ConfirmDelegate"</c></term>
///     <description>
///       Gate 3 denied shutdown — the caller-supplied async confirmation delegate
///       returned <c>false</c>, timed out (5 s limit), threw an exception, or was
///       required but not provided. Also returned when <see cref="IShutdownOrchestrator.Evaluate"/>
///       is called synchronously but Gate 3 is required (use
///       <see cref="IShutdownOrchestrator.RequestShutdownAsync"/> instead).
///     </description>
///   </item>
///   <item>
///     <term><c>"All"</c></term>
///     <description>
///       All evaluated gates passed — shutdown is approved. This value only appears
///       when <see cref="Approved"/> is <c>true</c>.
///     </description>
///   </item>
/// </list>
/// </para>
/// </summary>
/// <param name="Approved">
/// <c>true</c> if all evaluated gates passed and the system is safe to shut down;
/// <c>false</c> if any gate blocked the shutdown request.
/// </param>
/// <param name="Gate">
/// The name of the first gate that blocked shutdown
/// (<c>"MinSignals"</c>, <c>"Cooldown"</c>, or <c>"ConfirmDelegate"</c>),
/// or <c>"All"</c> when every gate passed. Use this value for metrics tagging and
/// operational dashboards.
/// </param>
/// <param name="Reason">
/// Human-readable explanation of the decision, suitable for logging. Includes
/// relevant thresholds and elapsed values when a gate denies shutdown.
/// </param>
public sealed record ShutdownDecision(
    bool Approved,
    string Gate,
    string Reason);

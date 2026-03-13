// <copyright file="EventSinkDispatcherOptions.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core.Contracts;

/// <summary>
/// Configuration for the event sink dispatcher.
/// Controls rate limiting and timeout behavior for event sink dispatch.
/// </summary>
/// <param name="MaxEventsPerSecondPerSink">
/// Maximum events per second allowed per individual sink.
/// Events beyond this rate are dropped with a warning log.
/// Must be at least 1. Default is 100.
/// </param>
/// <param name="SinkTimeout">
/// Maximum time to wait for a single sink call before timing out.
/// Default is 5 seconds when <c>null</c>.
/// </param>
public sealed record EventSinkDispatcherOptions(
    int MaxEventsPerSecondPerSink = 100,
    TimeSpan? SinkTimeout = null)
{
    /// <summary>
    /// Gets the effective sink timeout, defaulting to 5 seconds when not explicitly configured.
    /// </summary>
    internal TimeSpan EffectiveSinkTimeout => SinkTimeout ?? TimeSpan.FromSeconds(5);
}

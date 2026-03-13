// <copyright file="FakeClock.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core;

namespace HealthBoss.Grpc.Tests;

/// <summary>
/// Deterministic clock for testing.
/// </summary>
internal sealed class FakeClock : ISystemClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
}

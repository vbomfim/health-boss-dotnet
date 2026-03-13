using HealthBoss.Core;
using HealthBoss.Core.Contracts;

namespace HealthBoss.AspNetCore.Tests.Fakes;

/// <summary>
/// Captures recorded signals for test assertions.
/// </summary>
internal sealed class FakeSignalBuffer : ISignalBuffer
{
    private readonly List<HealthSignal> _signals = [];

    public IReadOnlyList<HealthSignal> Signals => _signals;

    public int Count => _signals.Count;

    public void Record(HealthSignal signal) => _signals.Add(signal);

    public IReadOnlyList<HealthSignal> GetSignals(TimeSpan window) => _signals;

    public void Trim(DateTimeOffset cutoff) { }
}

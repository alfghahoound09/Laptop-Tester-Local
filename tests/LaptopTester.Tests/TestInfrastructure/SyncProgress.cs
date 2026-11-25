using System.Collections.Concurrent;

namespace LaptopTester.Tests.TestInfrastructure;

/// <summary>Synchronous IProgress&lt;T&gt; implementation that avoids SynchronizationContext async posting in unit tests.</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly ConcurrentQueue<T> _items = new();

    public void Report(T value) => _items.Enqueue(value);

    public IReadOnlyList<T> Snapshot() => _items.ToArray();
}

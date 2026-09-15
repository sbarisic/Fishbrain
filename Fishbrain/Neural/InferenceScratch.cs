using System.Collections.Concurrent;

namespace Fishbrain.Neural;

/// <summary>Exact-length, bounded scratch reuse. A reply owns all rentals until it returns.</summary>
internal sealed class InferenceScratch : IDisposable
{
    private const long MaximumRetainedBytes = 128L * 1024 * 1024;
    private static readonly AsyncLocal<InferenceScratch?> Current = new();
    private static readonly object PoolLock = new();
    private sealed class Bucket
    {
        internal readonly Stack<float[]> Arrays = new();
        internal long LastUse;
    }
    private static readonly Dictionary<int, Bucket> Pool = [];
    private static long _retainedBytes;
    private static long _clock;
    private readonly ConcurrentBag<float[]> _rentals = [];
    private readonly HashSet<float[]> _escaped = [];
    private readonly InferenceScratch? _parent;
    private bool _disposed;

    internal InferenceScratch()
    {
        _parent = Current.Value;
        Current.Value = this;
    }

    internal static InferenceScratch? Nested(bool training) => !training && Current.Value is not null ? new() : null;

    internal void Keep(float[] values)
    {
        if (values.Length < 1024 || !_escaped.Add(values)) return;
        _parent?._rentals.Add(values);
    }

    internal static float[] Allocate(int length)
    {
        if (Current.Value is not { } scope || length < 1024) return new float[length];
        float[]? values = null;
        lock (PoolLock)
            if (Pool.TryGetValue(length, out var bucket) && bucket.Arrays.TryPop(out values))
            {
                _retainedBytes -= (long)length * sizeof(float);
                bucket.LastUse = ++_clock;
                if (bucket.Arrays.Count == 0) Pool.Remove(length);
            }
        if (values is null) values = new float[length];
        else Array.Clear(values);
        scope._rentals.Add(values);
        return values;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Current.Value = _parent;
        lock (PoolLock)
            while (_rentals.TryTake(out var values))
            {
                if (_escaped.Contains(values)) continue;
                var bytes = (long)values.Length * sizeof(float);
                if (bytes > MaximumRetainedBytes) continue;
                while (_retainedBytes + bytes > MaximumRetainedBytes && Pool.Count > 0)
                {
                    var oldest = Pool.MinBy(x => x.Value.LastUse);
                    _retainedBytes -= (long)oldest.Key * sizeof(float) * oldest.Value.Arrays.Count;
                    Pool.Remove(oldest.Key);
                }
                if (!Pool.TryGetValue(values.Length, out var bucket)) Pool.Add(values.Length, bucket = new());
                bucket.Arrays.Push(values);
                bucket.LastUse = ++_clock;
                _retainedBytes += bytes;
            }
    }
}

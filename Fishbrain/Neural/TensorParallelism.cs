namespace Fishbrain.Neural;

/// <summary>A sample worker owns its CPU core instead of starting nested kernel workers.</summary>
internal sealed class TensorParallelism : IDisposable
{
    private static readonly AsyncLocal<bool> Serial = new();
    private readonly bool _previous = Serial.Value;
    internal static bool AllowWorkers => !Serial.Value;
    internal TensorParallelism() => Serial.Value = true;
    public void Dispose() => Serial.Value = _previous;
}

namespace Fishbrain;

/// <summary>A decoder exclusively owns its cache until disposal. At most one idle cache is retained.</summary>
internal sealed class DecoderWorkspace
{
    private const long MaximumIdleBytes = 32L * 1024 * 1024;
    private static readonly object Gate = new();
    private static DecoderWorkspace? _idle;
    private readonly (int Layers, int Context, int Width) _shape;
    internal float[][] Keys { get; }
    internal float[][] Values { get; }
    internal float[] Scores { get; }

    private DecoderWorkspace(CausalConfig config)
    {
        _shape = (config.Layers, config.Context, config.Width);
        Keys = Enumerable.Range(0, config.Layers).Select(_ => new float[config.Context * config.Width]).ToArray();
        Values = Enumerable.Range(0, config.Layers).Select(_ => new float[config.Context * config.Width]).ToArray();
        Scores = new float[config.Context];
    }

    internal static DecoderWorkspace Rent(CausalConfig config)
    {
        lock (Gate)
        {
            if (_idle is { } available && available._shape == (config.Layers, config.Context, config.Width))
            {
                _idle = null;
                return available;
            }
        }
        return new(config);
    }

    internal void Return()
    {
        if ((long)_shape.Layers * _shape.Context * _shape.Width * sizeof(float) * 2 > MaximumIdleBytes) return;
        lock (Gate) _idle = this;
    }
}

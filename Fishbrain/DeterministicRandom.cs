using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fishbrain;

internal sealed class DeterministicRandom
{
    private ulong _state;
    public DeterministicRandom(int seed) => State = unchecked((ulong)seed) + 0x9E3779B97F4A7C15UL;

    public ulong State
    {
        get => _state;
        set => _state = value == 0 ? 0x9E3779B97F4A7C15UL : value;
    }

    public ulong NextUInt64()
    {
        var value = _state;
        value ^= value >> 12;
        value ^= value << 25;
        value ^= value >> 27;
        _state = value;
        return value * 2685821657736338717UL;
    }

    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);
    public int NextInt(int maximum) => maximum > 0
        ? (int)(NextUInt64() % (uint)maximum)
        : throw new ArgumentOutOfRangeException(nameof(maximum));

    public double NextGaussian()
    {
        var first = Math.Max(NextDouble(), double.Epsilon);
        var second = NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(first)) * Math.Cos(2.0 * Math.PI * second);
    }
}


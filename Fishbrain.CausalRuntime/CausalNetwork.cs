using Fishbrain.Neural;
using System.Numerics;

namespace Fishbrain;

internal sealed record WeightShape(string Name, int Rows, int Columns);
internal sealed class CausalNetwork
{
    internal CausalConfig Config { get; }
    internal IReadOnlyList<WeightShape> Shapes { get; }
    private readonly Dictionary<string, float[]> _weights;
    private readonly Dictionary<string, Tensor> _decodingWeights;
    internal long ParameterCount => Shapes.Sum(s => (long)s.Rows * s.Columns);
    internal CausalNetwork(CausalConfig config, Dictionary<string, float[]> weights)
    {
        config.Validate(); Config = config; Shapes = Layout(config).ToArray();
        if (weights.Count != Shapes.Count || Shapes.Any(s => !weights.TryGetValue(s.Name, out var w) || w.Length != s.Rows * s.Columns || w.Any(x => !float.IsFinite(x))))
            throw new InvalidDataException("Causal weight layout mismatch or nonfinite weights.");
        _weights = weights;
        // Single-token decoding uses contiguous dot products. Keep the canonical
        // matrices for prompt batches and an immutable transpose for this path.
        _decodingWeights = Shapes.Where(s => s.Name.StartsWith("layers.", StringComparison.Ordinal) && !s.Name.Contains("norm", StringComparison.Ordinal))
            .ToDictionary(s => s.Name, s => new Tensor(s.Columns, s.Rows, TensorKernels.Transpose(weights[s.Name], s.Rows, s.Columns)));
    }
    internal Dictionary<string, Tensor> Parameters(bool gradients = false) => Shapes.ToDictionary(s => s.Name,
        s => new Tensor(s.Rows, s.Columns, _weights[s.Name], gradients));
    internal static IEnumerable<WeightShape> Layout(CausalConfig c)
    {
        yield return new("token", c.Vocabulary, c.Width); yield return new("position", c.Context, c.Width);
        for (var l = 0; l < c.Layers; l++)
        {
            yield return new($"layers.{l}.norm1", 1, c.Width); yield return new($"layers.{l}.norm2", 1, c.Width);
            foreach (var name in new[] { "q", "k", "v", "o" }) yield return new($"layers.{l}.{name}", c.Width, c.Width);
            yield return new($"layers.{l}.up", c.Width, c.FeedForward); yield return new($"layers.{l}.down", c.FeedForward, c.Width);
        }
        yield return new("norm", 1, c.Width);
    }
    internal Tensor Forward(TensorGraph g, IReadOnlyDictionary<string, Tensor> p, int[] tokens, bool lastOnly = false,
        Action<int, Tensor, Tensor>? cache = null)
    {
        if (tokens.Length is < 1 || tokens.Length > Config.Context) throw new ArgumentException("Invalid token count.");
        var x = g.Add(g.Gather(p["token"], tokens), g.Gather(p["position"], Enumerable.Range(0, tokens.Length).ToArray()));
        for (var l = 0; l < Config.Layers; l++)
        {
            using var scratch = InferenceScratch.Nested(g.Training);
            var prefix = $"layers.{l}.";
            var n = g.ScaleColumns(g.Normalize(x), p[prefix + "norm1"]);
            var q = g.MatMul(n, p[prefix + "q"]); var k = g.MatMul(n, p[prefix + "k"]); var v = g.MatMul(n, p[prefix + "v"]);
            cache?.Invoke(l, k, v);
            x = g.Add(x, g.MatMul(g.Attention(q, k, v, Config.Heads, true), p[prefix + "o"]));
            n = g.ScaleColumns(g.Normalize(x), p[prefix + "norm2"]);
            x = g.Add(x, g.MatMul(g.Gelu(g.MatMul(n, p[prefix + "up"])), p[prefix + "down"]));
            scratch?.Keep(x.Data);
        }
        if (lastOnly) x = g.Gather(x, [x.Rows - 1]);
        return g.MatMulRightTranspose(g.ScaleColumns(g.Normalize(x), p["norm"]), p["token"]);
    }

    internal sealed class Session(CausalNetwork model) : IDisposable
    {
        private readonly Dictionary<string, Tensor> _p = model.Parameters();
        private readonly DecoderWorkspace _workspace = DecoderWorkspace.Rent(model.Config);
        private float[][] _keys => _workspace.Keys;
        private float[][] _values => _workspace.Values;
        private int _count;
        private bool _disposed;
        internal float[] Prefill(int[] tokens)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (tokens.Length is < 1 || tokens.Length > model.Config.Context) throw new ArgumentException("Invalid token count.");
            // Process bounded blocks against the same causal cache. This avoids
            // allocating full context-by-context attention matrices for each reply.
            float[]? logits = null; var c = model.Config;
            for (var start = 0; start < tokens.Length; start += 32)
            {
                using var scratch = new InferenceScratch();
                var length = Math.Min(32, tokens.Length - start); var g = new TensorGraph(false);
                var x = g.Add(g.Gather(_p["token"], tokens[start..(start + length)]),
                    g.Gather(_p["position"], Enumerable.Range(start, length).ToArray()));
                for (var l = 0; l < c.Layers; l++)
                {
                    using var layerScratch = InferenceScratch.Nested(false);
                    var prefix = $"layers.{l}."; var n = g.ScaleColumns(g.Normalize(x), _p[prefix + "norm1"]);
                    var q = g.MatMul(n, _p[prefix + "q"]); var k = g.MatMul(n, _p[prefix + "k"]); var v = g.MatMul(n, _p[prefix + "v"]);
                    Array.Copy(k.Data, 0, _keys[l], start * c.Width, k.Data.Length);
                    Array.Copy(v.Data, 0, _values[l], start * c.Width, v.Data.Length);
                    x = g.Add(x, g.MatMul(Attend(q, l, start), _p[prefix + "o"]));
                    n = g.ScaleColumns(g.Normalize(x), _p[prefix + "norm2"]);
                    x = g.Add(x, g.MatMul(g.Gelu(g.MatMul(n, _p[prefix + "up"])), _p[prefix + "down"]));
                    layerScratch?.Keep(x.Data);
                }
                if (start + length == tokens.Length)
                    logits = (float[])DecodeKernels.Project(g.ScaleColumns(g.Normalize(g.Gather(x, [length - 1])), _p["norm"]), _p["token"]).Data.Clone();
            }
            _count = tokens.Length; return logits!;
        }
        internal float[] Next(int token)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_count == 0 || _count >= model.Config.Context) throw new InvalidOperationException("Decoder context exhausted.");
            using var scratch = new InferenceScratch();
            var c = model.Config; var g = new TensorGraph(false);
            var x = g.Add(g.Gather(_p["token"], [token]), g.Gather(_p["position"], [_count]));
            for (var l = 0; l < c.Layers; l++)
            {
                var prefix = $"layers.{l}."; var n = g.ScaleColumns(g.Normalize(x), _p[prefix + "norm1"]);
                var q = DecodeKernels.Project(n, model._decodingWeights[prefix + "q"]); var k = DecodeKernels.Project(n, model._decodingWeights[prefix + "k"]); var v = DecodeKernels.Project(n, model._decodingWeights[prefix + "v"]);
                Array.Copy(k.Data, 0, _keys[l], _count * c.Width, c.Width); Array.Copy(v.Data, 0, _values[l], _count * c.Width, c.Width);
                x = g.Add(x, DecodeKernels.Project(Attend(q, l, _count), model._decodingWeights[prefix + "o"]));
                n = g.ScaleColumns(g.Normalize(x), _p[prefix + "norm2"]);
                x = g.Add(x, DecodeKernels.Project(g.Gelu(DecodeKernels.Project(n, model._decodingWeights[prefix + "up"])), model._decodingWeights[prefix + "down"]));
            }
            _count++;
            return (float[])DecodeKernels.Project(g.ScaleColumns(g.Normalize(x), _p["norm"]), _p["token"]).Data.Clone();
        }
        private Tensor Attend(Tensor query, int layer, int start)
        {
            var c = model.Config; var output = InferenceScratch.Allocate(query.Rows * c.Width);
            var headWidth = c.Width / c.Heads; var scores = _workspace.Scores;
            for (var row = 0; row < query.Rows; row++)
            for (var h = 0; h < c.Heads; h++)
            {
                var count = start + row + 1; var offset = h * headWidth; var target = row * c.Width + offset;
                var maximum = float.NegativeInfinity;
                for (var t = 0; t < count; t++)
                {
                    scores[t] = Dot(query.Data, target, _keys[layer], t * c.Width + offset, headWidth) / MathF.Sqrt(headWidth);
                    maximum = Math.Max(maximum, scores[t]);
                }
                var sum = TensorKernels.Exponentiate(scores, 0, count, maximum);
                for (var t = 0; t < count; t++)
                {
                    var scale = scores[t] / sum; var factor = new Vector<float>(scale); var valueOffset = t * c.Width + offset; var i = 0;
                    for (; i <= headWidth - Vector<float>.Count; i += Vector<float>.Count)
                        (new Vector<float>(output, target + i) + new Vector<float>(_values[layer], valueOffset + i) * factor).CopyTo(output, target + i);
                    for (; i < headWidth; i++) output[target + i] += scale * _values[layer][valueOffset + i];
                }
            }
            return new(query.Rows, c.Width, output);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _workspace.Return();
        }
        private static float Dot(float[] a, int ai, float[] b, int bi, int length)
        {
            var sum = Vector<float>.Zero; var i = 0;
            for (; i <= length - Vector<float>.Count; i += Vector<float>.Count) sum += new Vector<float>(a, ai + i) * new Vector<float>(b, bi + i);
            var value = Vector.Sum(sum); for (; i < length; i++) value += a[ai + i] * b[bi + i]; return value;
        }
    }
}

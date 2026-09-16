using System.Numerics;

namespace Fishbrain.Neural;

/// <summary>A per-call tensor. Inference never creates gradients or records a backward tape.</summary>
internal sealed class Tensor(int rows, int columns, float[] data, bool differentiable = false, float[]? gradientBuffer = null)
{
    public int Rows { get; } = rows;
    public int Columns { get; } = columns;
    public float[] Data { get; } = data.Length == checked(rows * columns) ? data
        : throw new ArgumentException("Tensor dimensions do not match storage.");
    public float[]? Gradient { get; } = differentiable
        ? gradientBuffer is null ? new float[data.Length] : gradientBuffer.Length == data.Length ? gradientBuffer
            : throw new ArgumentException("Gradient dimensions do not match storage.")
        : null;
}

internal sealed class TensorGraph(bool training, bool vectorized = true)
{
    private readonly List<Action> _backward = [];
    public bool Training { get; } = training;
    private Tensor Result(int rows, int columns) => new(rows, columns, InferenceScratch.Allocate(checked(rows * columns)),
        Training, Training ? InferenceScratch.Allocate(checked(rows * columns)) : null);
    private void Record(Action action) { if (Training) _backward.Add(action); }
    private static void Accumulate(Tensor tensor, int index, float value)
    {
        if (tensor.Gradient is { } gradient) gradient[index] += value;
    }

    public void Backward()
    {
        for (var i = _backward.Count - 1; i >= 0; i--) _backward[i]();
        _backward.Clear();
    }

    public Tensor Add(Tensor a, Tensor b)
    {
        if (a.Rows != b.Rows || a.Columns != b.Columns) throw new ArgumentException("Addition shape mismatch.");
        var y = Result(a.Rows, a.Columns);
        for (var i = 0; i < y.Data.Length; i++) y.Data[i] = a.Data[i] + b.Data[i];
        if (Training) Record(() => { for (var i = 0; i < y.Data.Length; i++) { Accumulate(a, i, y.Gradient![i]); Accumulate(b, i, y.Gradient![i]); } });
        return y;
    }

    public Tensor MatMul(Tensor a, Tensor b)
    {
        if (a.Columns != b.Rows) throw new ArgumentException("Multiplication shape mismatch.");
        var y = Result(a.Rows, b.Columns);
        if (vectorized) TensorKernels.Multiply(a.Data, b.Data, y.Data, a.Rows, a.Columns, b.Columns);
        else for (var r = 0; r < a.Rows; r++)
            for (var k = 0; k < a.Columns; k++)
                AddScaled(b.Data, k * b.Columns, y.Data, r * b.Columns, b.Columns, a.Data[r * a.Columns + k]);
        if (Training) Record(() =>
        {
            if (vectorized)
            {
                if (a.Gradient is { } ag)
                {
                    var contribution = InferenceScratch.Allocate(ag.Length);
                    TensorKernels.Multiply(y.Gradient!, TensorKernels.Transpose(b.Data, b.Rows, b.Columns), contribution, a.Rows, b.Columns, a.Columns);
                    AddScaled(contribution, 0, ag, 0, ag.Length, 1);
                }
                if (b.Gradient is { } bg)
                {
                    var contribution = InferenceScratch.Allocate(bg.Length);
                    TensorKernels.Multiply(TensorKernels.Transpose(a.Data, a.Rows, a.Columns), y.Gradient!, contribution, a.Columns, a.Rows, b.Columns);
                    AddScaled(contribution, 0, bg, 0, bg.Length, 1);
                }
                return;
            }
            for (var r = 0; r < a.Rows; r++)
                for (var k = 0; k < a.Columns; k++)
                {
                    Accumulate(a, r * a.Columns + k, Dot(y.Gradient!, r * b.Columns, b.Data, k * b.Columns, b.Columns));
                    if (b.Gradient is { } bg) AddScaled(y.Gradient!, r * b.Columns, bg, k * b.Columns, b.Columns, a.Data[r * a.Columns + k]);
                }
        });
        return y;
    }

    /// <summary>Projects against row-major embeddings without materializing a vocabulary-sized transpose.</summary>
    public Tensor MatMulRightTranspose(Tensor a, Tensor b)
    {
        if (a.Columns != b.Columns) throw new ArgumentException("Transposed multiplication shape mismatch.");
        var y = Result(a.Rows, b.Rows);
        void Forward(int row)
        {
            for (var r = 0; r < a.Rows; r++)
                y.Data[r * b.Rows + row] = Dot(a.Data, r * a.Columns, b.Data, row * b.Columns, a.Columns);
        }
        if (vectorized && TensorParallelism.AllowWorkers && (long)a.Rows * b.Rows * a.Columns >= 1_000_000)
            Parallel.For(0, b.Rows, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(6, Environment.ProcessorCount) }, Forward);
        else for (var row = 0; row < b.Rows; row++) Forward(row);
        if (Training) Record(() =>
        {
            if (a.Gradient is { } ag)
            {
                var contribution = InferenceScratch.Allocate(ag.Length);
                if (vectorized) TensorKernels.Multiply(y.Gradient!, b.Data, contribution, a.Rows, b.Rows, a.Columns);
                else for (var r = 0; r < a.Rows; r++) for (var k = 0; k < b.Rows; k++)
                    AddScaled(b.Data, k * b.Columns, contribution, r * a.Columns, a.Columns, y.Gradient![r * b.Rows + k]);
                AddScaled(contribution, 0, ag, 0, ag.Length, 1);
            }
            if (b.Gradient is { } bg)
            {
                void BackwardRow(int row)
                {
                    for (var r = 0; r < a.Rows; r++)
                        AddScaled(a.Data, r * a.Columns, bg, row * b.Columns, b.Columns, y.Gradient![r * b.Rows + row]);
                }
                if (vectorized && TensorParallelism.AllowWorkers && (long)a.Rows * b.Rows * a.Columns >= 1_000_000)
                    Parallel.For(0, b.Rows, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(6, Environment.ProcessorCount) }, BackwardRow);
                else for (var row = 0; row < b.Rows; row++) BackwardRow(row);
            }
        });
        return y;
    }

    public Tensor Transpose(Tensor x)
    {
        var y = Result(x.Columns, x.Rows);
        for (var r = 0; r < x.Rows; r++) for (var c = 0; c < x.Columns; c++) y.Data[c * x.Rows + r] = x.Data[r * x.Columns + c];
        if (Training) Record(() => { for (var r = 0; r < x.Rows; r++) for (var c = 0; c < x.Columns; c++) Accumulate(x, r * x.Columns + c, y.Gradient![c * x.Rows + r]); });
        return y;
    }

    public Tensor Gather(Tensor table, IReadOnlyList<int> rows)
    {
        var indices = rows.ToArray();
        if (indices.Any(i => i < 0 || i >= table.Rows)) throw new ArgumentException("Gather index is outside the table.");
        var y = Result(indices.Length, table.Columns);
        for (var r = 0; r < indices.Length; r++) Array.Copy(table.Data, indices[r] * table.Columns, y.Data, r * table.Columns, table.Columns);
        if (Training) Record(() => { if (table.Gradient is { } gradient) for (var r = 0; r < indices.Length; r++) AddScaled(y.Gradient!, r * table.Columns, gradient, indices[r] * table.Columns, table.Columns, 1); });
        return y;
    }

    public Tensor Concat(params Tensor[] inputs)
    {
        if (inputs.Length == 0 || inputs.Any(x => x.Columns != inputs[0].Columns)) throw new ArgumentException("Concatenation shape mismatch.");
        var y = Result(inputs.Sum(x => x.Rows), inputs[0].Columns);
        var offset = 0;
        foreach (var x in inputs) { Array.Copy(x.Data, 0, y.Data, offset, x.Data.Length); offset += x.Data.Length; }
        if (Training) Record(() => { var cursor = 0; foreach (var x in inputs) { if (x.Gradient is { } gradient) AddScaled(y.Gradient!, cursor, gradient, 0, gradient.Length, 1); cursor += x.Data.Length; } });
        return y;
    }

    public Tensor Relu(Tensor x)
    {
        var y = Result(x.Rows, x.Columns);
        for (var i = 0; i < x.Data.Length; i++) y.Data[i] = Math.Max(0, x.Data[i]);
        if (Training) Record(() => { for (var i = 0; i < x.Data.Length; i++) if (x.Data[i] > 0) Accumulate(x, i, y.Gradient![i]); });
        return y;
    }

    public Tensor Normalize(Tensor x)
    {
        var y = Result(x.Rows, x.Columns);
        var inverse = new float[x.Rows];
        for (var r = 0; r < x.Rows; r++)
        {
            var offset = r * x.Columns;
            inverse[r] = 1 / MathF.Sqrt(Dot(x.Data, offset, x.Data, offset, x.Columns) / x.Columns + 1e-5f);
            for (var c = 0; c < x.Columns; c++) y.Data[offset + c] = x.Data[offset + c] * inverse[r];
        }
        if (Training) Record(() =>
        {
            for (var r = 0; r < x.Rows; r++)
            {
                var offset = r * x.Columns;
                var projection = Dot(y.Gradient!, offset, y.Data, offset, x.Columns) / x.Columns;
                for (var c = 0; c < x.Columns; c++) Accumulate(x, offset + c, inverse[r] * (y.Gradient![offset + c] - y.Data[offset + c] * projection));
            }
        });
        return y;
    }

    public Tensor Mean(Tensor x)
    {
        if (x.Rows == 0) throw new ArgumentException("Cannot average an empty tensor.");
        var y = Result(1, x.Columns);
        for (var r = 0; r < x.Rows; r++) AddScaled(x.Data, r * x.Columns, y.Data, 0, x.Columns, 1f / x.Rows);
        if (Training) Record(() => { if (x.Gradient is { } gradient) for (var r = 0; r < x.Rows; r++) AddScaled(y.Gradient!, 0, gradient, r * x.Columns, x.Columns, 1f / x.Rows); });
        return y;
    }

    public Tensor Attention(Tensor query, Tensor key, Tensor value, int heads, bool causal)
    {
        if (query.Columns != key.Columns || key.Rows != value.Rows || key.Columns != value.Columns || query.Columns % heads != 0 || causal && query.Rows != key.Rows)
            throw new ArgumentException("Attention shape mismatch.");
        var width = query.Columns;
        var headWidth = width / heads;
        var scale = 1 / MathF.Sqrt(headWidth);
        var y = Result(query.Rows, width);
        var probabilities = InferenceScratch.Allocate(checked(heads * query.Rows * key.Rows));
        RunHeads(ForwardHead);
        void ForwardHead(int h)
        {
            if (vectorized && query.Rows >= 16 && key.Rows >= 16)
            {
                var qh = InferenceScratch.Allocate(query.Rows * headWidth);
                var kt = InferenceScratch.Allocate(headWidth * key.Rows);
                var vh = InferenceScratch.Allocate(key.Rows * headWidth);
                for (var r = 0; r < query.Rows; r++)
                    Array.Copy(query.Data, r * width + h * headWidth, qh, r * headWidth, headWidth);
                for (var r = 0; r < key.Rows; r++)
                    for (var c = 0; c < headWidth; c++)
                    {
                        kt[c * key.Rows + r] = key.Data[r * width + h * headWidth + c];
                        vh[r * headWidth + c] = value.Data[r * width + h * headWidth + c];
                    }
                var scores = InferenceScratch.Allocate(query.Rows * key.Rows);
                TensorKernels.Multiply(qh, kt, scores, query.Rows, headWidth, key.Rows, false);
                for (var q = 0; q < query.Rows; q++)
                {
                    var offset = q * key.Rows;
                    var count = causal ? q + 1 : key.Rows;
                    var maximum = float.NegativeInfinity;
                    for (var k = 0; k < count; k++) maximum = Math.Max(maximum, scores[offset + k] *= scale);
                    var sum = TensorKernels.Exponentiate(scores, offset, count, maximum);
                    for (var k = 0; k < count; k++) scores[offset + k] /= sum;
                    Array.Clear(scores, offset + count, key.Rows - count);
                }
                Array.Copy(scores, 0, probabilities, h * scores.Length, scores.Length);
                var result = InferenceScratch.Allocate(query.Rows * headWidth);
                TensorKernels.Multiply(scores, vh, result, query.Rows, key.Rows, headWidth, false);
                for (var r = 0; r < query.Rows; r++)
                    Array.Copy(result, r * headWidth, y.Data, r * width + h * headWidth, headWidth);
                return;
            }
            for (var q = 0; q < query.Rows; q++)
            {
                var count = causal ? q + 1 : key.Rows;
                var pOffset = (h * query.Rows + q) * key.Rows;
                var qOffset = q * width + h * headWidth;
                var maximum = float.NegativeInfinity;
                for (var k = 0; k < count; k++) maximum = Math.Max(maximum, probabilities[pOffset + k] = Dot(query.Data, qOffset, key.Data, k * width + h * headWidth, headWidth) * scale);
                var sum = 0f;
                if (vectorized) sum = TensorKernels.Exponentiate(probabilities, pOffset, count, maximum);
                else for (var k = 0; k < count; k++) sum += probabilities[pOffset + k] = MathF.Exp(probabilities[pOffset + k] - maximum);
                for (var k = 0; k < count; k++)
                {
                    var p = probabilities[pOffset + k] /= sum;
                    AddScaled(value.Data, k * width + h * headWidth, y.Data, qOffset, headWidth, p);
                }
            }
        }
        if (Training) Record(() =>
        {
            RunHeads(BackwardHead);
            void BackwardHead(int h)
            {
                var dp = new float[key.Rows];
                for (var q = 0; q < query.Rows; q++)
                {
                    var count = causal ? q + 1 : key.Rows;
                    var pOffset = (h * query.Rows + q) * key.Rows;
                    var qOffset = q * width + h * headWidth;
                    var mean = 0f;
                    for (var k = 0; k < count; k++)
                    {
                        dp[k] = Dot(y.Gradient!, qOffset, value.Data, k * width + h * headWidth, headWidth);
                        mean += dp[k] * probabilities[pOffset + k];
                        if (value.Gradient is { } vg) AddScaled(y.Gradient!, qOffset, vg, k * width + h * headWidth, headWidth, probabilities[pOffset + k]);
                    }
                    for (var k = 0; k < count; k++)
                    {
                        var scoreGradient = probabilities[pOffset + k] * (dp[k] - mean) * scale;
                        var kOffset = k * width + h * headWidth;
                        if (query.Gradient is { } qg) AddScaled(key.Data, kOffset, qg, qOffset, headWidth, scoreGradient);
                        if (key.Gradient is { } kg) AddScaled(query.Data, qOffset, kg, kOffset, headWidth, scoreGradient);
                    }
                }
            }
        });
        return y;
        void RunHeads(Action<int> run)
        {
            if (vectorized && TensorParallelism.AllowWorkers && query.Rows >= 32 && (long)heads * query.Rows * key.Rows >= 32768)
                Parallel.For(0, heads, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(heads, Environment.ProcessorCount) }, run);
            else for (var head = 0; head < heads; head++) run(head);
        }
    }

    public float CrossEntropy(Tensor logits, int row, int target, float weight = 1)
    {
        if (row < 0 || row >= logits.Rows || target < 0 || target >= logits.Columns || !float.IsFinite(weight) || weight < 0) throw new ArgumentException("Invalid categorical target.");
        var p = Probabilities(logits, row);
        var loss = -MathF.Log(Math.Max(1e-30f, p[target])) * weight;
        if (Training) for (var c = 0; c < logits.Columns; c++) Accumulate(logits, row * logits.Columns + c, (p[c] - (c == target ? 1 : 0)) * weight);
        return loss;
    }

    public float BinaryCrossEntropy(Tensor logits, int row, IReadOnlySet<int> positives, float weight = 1)
    {
        var loss = 0f;
        for (var c = 0; c < logits.Columns; c++)
        {
            var z = logits.Data[row * logits.Columns + c];
            var target = positives.Contains(c) ? 1f : 0f;
            loss += (Math.Max(z, 0) - z * target + MathF.Log(1 + MathF.Exp(-Math.Abs(z)))) * weight / logits.Columns;
            if (Training) Accumulate(logits, row * logits.Columns + c, (1 / (1 + MathF.Exp(-z)) - target) * weight / logits.Columns);
        }
        return loss;
    }

    public static float[] Probabilities(Tensor logits, int row = 0)
    {
        var p = logits.Data.AsSpan(row * logits.Columns, logits.Columns).ToArray();
        var max = p.Max();
        var sum = 0f;
        for (var i = 0; i < p.Length; i++) sum += p[i] = MathF.Exp(p[i] - max);
        for (var i = 0; i < p.Length; i++) p[i] /= sum;
        return p;
    }

    private float Dot(float[] a, int ai, float[] b, int bi, int count)
    {
        var i = 0;
        var sum = 0f;
        if (vectorized)
        {
            var accumulator = Vector<float>.Zero;
            for (; i <= count - Vector<float>.Count; i += Vector<float>.Count) accumulator += new Vector<float>(a, ai + i) * new Vector<float>(b, bi + i);
            sum = Vector.Sum(accumulator);
        }
        for (; i < count; i++) sum += a[ai + i] * b[bi + i];
        return sum;
    }

    private void AddScaled(float[] source, int sourceOffset, float[] destination, int destinationOffset, int count, float scale)
    {
        var i = 0;
        if (vectorized)
        {
            var factor = new Vector<float>(scale);
            for (; i <= count - Vector<float>.Count; i += Vector<float>.Count)
                (new Vector<float>(destination, destinationOffset + i) + new Vector<float>(source, sourceOffset + i) * factor).CopyTo(destination, destinationOffset + i);
        }
        for (; i < count; i++) destination[destinationOffset + i] += source[sourceOffset + i] * scale;
    }
}

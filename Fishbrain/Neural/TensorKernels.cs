using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Fishbrain.Neural;

/// <summary>Row-major kernels. Each worker owns disjoint output rows; reductions keep a fixed order.</summary>
internal static class TensorKernels
{
    internal static float Exponentiate(float[] values, int offset, int count, float maximum)
    {
        var index = 0;
        var sum = 0f;
        if (Vector256.IsHardwareAccelerated)
        {
            ref var start = ref MemoryMarshal.GetArrayDataReference(values);
            var accumulator = Vector256<float>.Zero;
            var shift = Vector256.Create(maximum);
            for (; index <= count - 8; index += 8)
            {
                var exp = Vector256.Exp(Vector256.LoadUnsafe(ref start, (nuint)(offset + index)) - shift);
                exp.StoreUnsafe(ref start, (nuint)(offset + index));
                accumulator += exp;
            }
            sum = Vector256.Sum(accumulator);
        }
        for (; index < count; index++) sum += values[offset + index] = MathF.Exp(values[offset + index] - maximum);
        return sum;
    }

    internal static void Multiply(float[] a, float[] b, float[] output, int rows, int inner, int columns, bool allowParallel = true)
    {
        if (a.Length != checked(rows * inner) || b.Length != checked(inner * columns) || output.Length != checked(rows * columns))
            throw new ArgumentException("Invalid matrix storage.");
        var blocks = (rows + 3) / 4;
        if (allowParallel && (long)rows * inner * columns >= 8_000_000 && rows >= 32)
        {
            var workers = Math.Min(6, Math.Min(Environment.ProcessorCount, blocks));
            Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, worker =>
                Rows(a, b, output, inner, columns, worker * blocks / workers * 4, Math.Min(rows, (worker + 1) * blocks / workers * 4)));
        }
        else Rows(a, b, output, inner, columns, 0, rows);
    }

    private static void Rows(float[] a, float[] b, float[] y, int inner, int columns, int first, int last)
    {
        ref var br = ref MemoryMarshal.GetArrayDataReference(b);
        ref var yr = ref MemoryMarshal.GetArrayDataReference(y);
        var row = first;
        if (Fma.IsSupported && Avx.IsSupported)
        {
            for (; row + 3 < last; row += 4)
            {
                var col = 0;
                for (; col + 15 < columns; col += 16)
                {
                    var s00 = Vector256<float>.Zero; var s01 = Vector256<float>.Zero;
                    var s10 = Vector256<float>.Zero; var s11 = Vector256<float>.Zero;
                    var s20 = Vector256<float>.Zero; var s21 = Vector256<float>.Zero;
                    var s30 = Vector256<float>.Zero; var s31 = Vector256<float>.Zero;
                    for (var k = 0; k < inner; k++)
                    {
                        var b0 = Vector256.LoadUnsafe(ref br, (nuint)(k * columns + col));
                        var b1 = Vector256.LoadUnsafe(ref br, (nuint)(k * columns + col + 8));
                        var av = Vector256.Create(a[row * inner + k]);
                        s00 = Fma.MultiplyAdd(av, b0, s00); s01 = Fma.MultiplyAdd(av, b1, s01);
                        av = Vector256.Create(a[(row + 1) * inner + k]);
                        s10 = Fma.MultiplyAdd(av, b0, s10); s11 = Fma.MultiplyAdd(av, b1, s11);
                        av = Vector256.Create(a[(row + 2) * inner + k]);
                        s20 = Fma.MultiplyAdd(av, b0, s20); s21 = Fma.MultiplyAdd(av, b1, s21);
                        av = Vector256.Create(a[(row + 3) * inner + k]);
                        s30 = Fma.MultiplyAdd(av, b0, s30); s31 = Fma.MultiplyAdd(av, b1, s31);
                    }
                    s00.StoreUnsafe(ref yr, (nuint)(row * columns + col)); s01.StoreUnsafe(ref yr, (nuint)(row * columns + col + 8));
                    s10.StoreUnsafe(ref yr, (nuint)((row + 1) * columns + col)); s11.StoreUnsafe(ref yr, (nuint)((row + 1) * columns + col + 8));
                    s20.StoreUnsafe(ref yr, (nuint)((row + 2) * columns + col)); s21.StoreUnsafe(ref yr, (nuint)((row + 2) * columns + col + 8));
                    s30.StoreUnsafe(ref yr, (nuint)((row + 3) * columns + col)); s31.StoreUnsafe(ref yr, (nuint)((row + 3) * columns + col + 8));
                }
                if (col + 7 < columns)
                {
                    var s0 = Vector256<float>.Zero; var s1 = Vector256<float>.Zero;
                    var s2 = Vector256<float>.Zero; var s3 = Vector256<float>.Zero;
                    for (var k = 0; k < inner; k++)
                    {
                        var values = Vector256.LoadUnsafe(ref br, (nuint)(k * columns + col));
                        s0 = Fma.MultiplyAdd(Vector256.Create(a[row * inner + k]), values, s0);
                        s1 = Fma.MultiplyAdd(Vector256.Create(a[(row + 1) * inner + k]), values, s1);
                        s2 = Fma.MultiplyAdd(Vector256.Create(a[(row + 2) * inner + k]), values, s2);
                        s3 = Fma.MultiplyAdd(Vector256.Create(a[(row + 3) * inner + k]), values, s3);
                    }
                    s0.StoreUnsafe(ref yr, (nuint)(row * columns + col));
                    s1.StoreUnsafe(ref yr, (nuint)((row + 1) * columns + col));
                    s2.StoreUnsafe(ref yr, (nuint)((row + 2) * columns + col));
                    s3.StoreUnsafe(ref yr, (nuint)((row + 3) * columns + col));
                    col += 8;
                }
                for (var r = row; r < row + 4; r++)
                    for (var c = col; c < columns; c++)
                    { float sum = 0; for (var k = 0; k < inner; k++) sum += a[r * inner + k] * b[k * columns + c]; y[r * columns + c] = sum; }
            }
        }
        for (; row < last; row++)
        {
            // GEMV and non-x86 fallback retain streaming SIMD without hardware-specific requirements.
            for (var k = 0; k < inner; k++)
            {
                var c = 0;
                var factor = new System.Numerics.Vector<float>(a[row * inner + k]);
                for (; c <= columns - System.Numerics.Vector<float>.Count; c += System.Numerics.Vector<float>.Count)
                    (new System.Numerics.Vector<float>(y, row * columns + c) + new System.Numerics.Vector<float>(b, k * columns + c) * factor).CopyTo(y, row * columns + c);
                for (; c < columns; c++) y[row * columns + c] += a[row * inner + k] * b[k * columns + c];
            }
        }
    }

    internal static float[] Transpose(float[] x, int rows, int columns)
    {
        var y = new float[x.Length];
        const int block = 32;
        for (var rr = 0; rr < rows; rr += block) for (var cc = 0; cc < columns; cc += block)
            for (var r = rr; r < Math.Min(rows, rr + block); r++) for (var c = cc; c < Math.Min(columns, cc + block); c++) y[c * rows + r] = x[r * columns + c];
        return y;
    }
}

using Fishbrain.Neural;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Fishbrain;

internal static class DecodeKernels
{
    // Rows of the immutable transposed matrix are independent output channels.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static Tensor Project(Tensor input, Tensor matrix)
    {
        if (input.Rows != 1 || input.Columns != matrix.Columns) throw new ArgumentException("Invalid cached projection shape.");
        var output = InferenceScratch.Allocate(matrix.Rows);
        for (var row = 0; row < matrix.Rows; row++) output[row] = Dot(input.Data, matrix.Data, row * matrix.Columns, matrix.Columns);
        return new(1, matrix.Rows, output);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Dot(float[] input, float[] matrix, int offset, int count)
    {
        var i = 0; float total = 0;
        if (Fma.IsSupported)
        {
            ref var a = ref MemoryMarshal.GetArrayDataReference(input); ref var b = ref MemoryMarshal.GetArrayDataReference(matrix);
            var s0 = Vector256<float>.Zero; var s1 = s0; var s2 = s0; var s3 = s0;
            for (; i <= count - 32; i += 32)
            {
                s0 = Fma.MultiplyAdd(Vector256.LoadUnsafe(ref a, (nuint)i), Vector256.LoadUnsafe(ref b, (nuint)(offset + i)), s0);
                s1 = Fma.MultiplyAdd(Vector256.LoadUnsafe(ref a, (nuint)(i + 8)), Vector256.LoadUnsafe(ref b, (nuint)(offset + i + 8)), s1);
                s2 = Fma.MultiplyAdd(Vector256.LoadUnsafe(ref a, (nuint)(i + 16)), Vector256.LoadUnsafe(ref b, (nuint)(offset + i + 16)), s2);
                s3 = Fma.MultiplyAdd(Vector256.LoadUnsafe(ref a, (nuint)(i + 24)), Vector256.LoadUnsafe(ref b, (nuint)(offset + i + 24)), s3);
            }
            s0 = (s0 + s1) + (s2 + s3);
            for (; i <= count - 8; i += 8) s0 = Fma.MultiplyAdd(Vector256.LoadUnsafe(ref a, (nuint)i), Vector256.LoadUnsafe(ref b, (nuint)(offset + i)), s0);
            total = Vector256.Sum(s0);
        }
        else
        {
            var sum = Vector<float>.Zero;
            for (; i <= count - Vector<float>.Count; i += Vector<float>.Count) sum += new Vector<float>(input, i) * new Vector<float>(matrix, offset + i);
            total = Vector.Sum(sum);
        }
        for (; i < count; i++) total += input[i] * matrix[offset + i];
        return total;
    }
}

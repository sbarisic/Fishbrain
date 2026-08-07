using System.Numerics;

namespace Fishbrain;

/// <summary>
/// Packed forward/backward implementation for the single-layer Fishbrain model.
/// It keeps training data in contiguous arrays so the numerical hot loops can use SIMD.
/// The scalar Value graph remains the inference implementation and forward-value reference.
/// </summary>
internal sealed class PackedTrainer
{
    private const double RmsEpsilon = 1e-5;

    private readonly BrainConfig _config;
    private readonly Layout _layout;
    private readonly double[] _weights;
    private readonly double[] _gradients;

    private PackedTrainer(BrainConfig config, double[] weights, double[] gradients)
    {
        _config = config;
        _layout = new Layout(config);
        _weights = weights;
        _gradients = gradients;
        if (weights.Length != _layout.ParameterCount || gradients.Length != weights.Length)
            throw new ArgumentException("Packed parameter storage does not match the model configuration.");
    }

    public static double Calculate(
        BrainConfig config,
        double[] weights,
        double[] gradients,
        TrainingSample sample)
    {
        Array.Clear(gradients);
        return new PackedTrainer(config, weights, gradients).Calculate(sample);
    }

    private double Calculate(TrainingSample sample)
    {
        if (sample.Task == TrainingTask.Perception)
            return CalculatePerception(sample);

        if (sample.FirstTargetIndex < 1 || sample.FirstTargetIndex >= sample.Tokens.Length)
            throw new ArgumentException("A training sample has no valid targets.", nameof(sample));

        var inputCount = sample.Tokens.Length - 1;
        var tokens = PrepareTokens(sample.Tokens.AsSpan(0, inputCount), sample.PositionOffset);
        var firstPosition = sample.FirstTargetIndex - 1;
        var targetCount = inputCount - firstPosition;
        var targets = new HiddenCache[targetCount];
        var loss = 0.0;

        for (var index = 0; index < targetCount; index++)
        {
            var position = firstPosition + index;
            var hidden = ForwardHidden(tokens, position);
            targets[index] = hidden;
            var target = Tokenizer.OutputId(sample.Tokens[sample.FirstTargetIndex + index]);
            loss += CrossEntropy(
                _layout.OutputHead, Tokenizer.OutputSize, _config.EmbeddingSize,
                hidden.Final, target, 1.0 / targetCount, hidden.DFinal);
        }

        Backward(tokens, targets);
        return loss / targetCount;
    }

    private double CalculatePerception(TrainingSample sample)
    {
        var target = sample.PerceptionTarget
            ?? throw new ArgumentException("A perception sample requires a target.", nameof(sample));
        var tokens = PrepareTokens(sample.Tokens, sample.PositionOffset);
        var hidden = ForwardHidden(tokens, tokens.Length - 1);
        var headCount = 0;
        if (sample.TargetFields.HasFlag(PerceptionFields.Intent)) headCount++;
        if (sample.TargetFields.HasFlag(PerceptionFields.Affect)) headCount++;
        if (sample.TargetFields.HasFlag(PerceptionFields.Expected)) headCount++;
        if (headCount == 0)
            throw new ArgumentException("A perception sample has no supervised fields.", nameof(sample));

        var scale = 1.0 / headCount;
        var loss = 0.0;
        if (sample.TargetFields.HasFlag(PerceptionFields.Intent))
        {
            loss += CrossEntropy(
                _layout.IntentHead, Enum.GetValues<DialogueIntent>().Length, _config.EmbeddingSize,
                hidden.Final, (int)target.Intent, scale, hidden.DFinal);
        }
        if (sample.TargetFields.HasFlag(PerceptionFields.Affect))
        {
            loss += CrossEntropy(
                _layout.AffectHead, Enum.GetValues<UserAffect>().Length, _config.EmbeddingSize,
                hidden.Final, (int)target.Affect, scale, hidden.DFinal);
        }
        if (sample.TargetFields.HasFlag(PerceptionFields.Expected))
        {
            loss += CrossEntropy(
                _layout.ExpectedHead, 2, _config.EmbeddingSize,
                hidden.Final, target.ResponseExpected ? 1 : 0, scale, hidden.DFinal);
        }

        Backward(tokens, [hidden]);
        return loss / headCount;
    }

    private TokenCache[] PrepareTokens(ReadOnlySpan<int> tokenIds, int positionOffset)
    {
        var size = _config.EmbeddingSize;
        var tokens = new TokenCache[tokenIds.Length];
        for (var position = 0; position < tokenIds.Length; position++)
        {
            var token = new TokenCache(tokenIds[position], (positionOffset + position) % _config.PositionPeriod, size);
            var tokenOffset = _layout.TokenEmbedding + token.Id * size;
            var positionMatrixOffset = _layout.PositionEmbedding + token.Position * size;
            Add(_weights, tokenOffset, _weights, positionMatrixOffset, token.X);
            token.NormInv = RmsNorm(token.X, token.Normalized);
            MatVec(_layout.Key, size, size, token.Normalized, token.Key);
            MatVec(_layout.Value, size, size, token.Normalized, token.Value);
            tokens[position] = token;
        }
        return tokens;
    }

    private HiddenCache ForwardHidden(TokenCache[] tokens, int position)
    {
        var size = _config.EmbeddingSize;
        var mlpSize = _config.MlpSize;
        var token = tokens[position];
        var attentionStart = Math.Max(0, position + 1 - _config.AttentionWindow);
        var attentionCount = position + 1 - attentionStart;
        var hidden = new HiddenCache(token, attentionStart, attentionCount, size, mlpSize, _config.HeadCount);

        MatVec(_layout.Query, size, size, token.Normalized, hidden.Query);
        var headSize = size / _config.HeadCount;
        var inverseScale = 1.0 / Math.Sqrt(headSize);
        for (var head = 0; head < _config.HeadCount; head++)
        {
            var headOffset = head * headSize;
            var weightOffset = head * attentionCount;
            var maximum = double.NegativeInfinity;
            for (var index = 0; index < attentionCount; index++)
            {
                var context = tokens[attentionStart + index];
                var score = Dot(hidden.Query, headOffset, context.Key, headOffset, headSize) * inverseScale;
                hidden.AttentionWeights[weightOffset + index] = score;
                maximum = Math.Max(maximum, score);
            }

            var sum = 0.0;
            for (var index = 0; index < attentionCount; index++)
            {
                var exponential = Math.Exp(hidden.AttentionWeights[weightOffset + index] - maximum);
                hidden.AttentionWeights[weightOffset + index] = exponential;
                sum += exponential;
            }
            for (var index = 0; index < attentionCount; index++)
                hidden.AttentionWeights[weightOffset + index] /= sum;

            for (var index = 0; index < attentionCount; index++)
            {
                var context = tokens[attentionStart + index];
                AddScaled(context.Value, headOffset,
                    hidden.AttentionWeights[weightOffset + index], hidden.Attention, headOffset, headSize);
            }
        }

        MatVec(_layout.AttentionOutput, size, size, hidden.Attention, hidden.Residual1);
        AddInPlace(hidden.Residual1, token.X);
        hidden.Norm2Inv = RmsNorm(hidden.Residual1, hidden.Normalized2);
        MatVec(_layout.MlpIn, mlpSize, size, hidden.Normalized2, hidden.MlpPre);
        for (var index = 0; index < mlpSize; index++)
            hidden.MlpActive[index] = Math.Max(0.0, hidden.MlpPre[index]);
        MatVec(_layout.MlpOut, size, mlpSize, hidden.MlpActive, hidden.Residual2);
        AddInPlace(hidden.Residual2, hidden.Residual1);
        hidden.FinalInv = RmsNorm(hidden.Residual2, hidden.Final);
        return hidden;
    }

    private void Backward(TokenCache[] tokens, IReadOnlyList<HiddenCache> targets)
    {
        foreach (var hidden in targets)
            BackwardHidden(tokens, hidden);

        var size = _config.EmbeddingSize;
        foreach (var token in tokens)
        {
            MatBackward(_layout.Key, size, size, token.Normalized, token.DKey, token.DNormalized);
            MatBackward(_layout.Value, size, size, token.Normalized, token.DValue, token.DNormalized);
            RmsNormBackward(token.X, token.DNormalized, token.NormInv, token.DX);
            AddTo(_gradients, _layout.TokenEmbedding + token.Id * size, token.DX);
            AddTo(_gradients, _layout.PositionEmbedding + token.Position * size, token.DX);
        }
    }

    private void BackwardHidden(TokenCache[] tokens, HiddenCache hidden)
    {
        var size = _config.EmbeddingSize;
        var mlpSize = _config.MlpSize;
        var dResidual2 = new double[size];
        RmsNormBackward(hidden.Residual2, hidden.DFinal, hidden.FinalInv, dResidual2);

        var dMlpActive = new double[mlpSize];
        MatBackward(_layout.MlpOut, size, mlpSize, hidden.MlpActive, dResidual2, dMlpActive);
        var dResidual1 = (double[])dResidual2.Clone();
        for (var index = 0; index < mlpSize; index++)
            if (hidden.MlpPre[index] <= 0.0) dMlpActive[index] = 0.0;

        var dNormalized2 = new double[size];
        MatBackward(_layout.MlpIn, mlpSize, size, hidden.Normalized2, dMlpActive, dNormalized2);
        RmsNormBackward(hidden.Residual1, dNormalized2, hidden.Norm2Inv, dResidual1);

        var dAttention = new double[size];
        MatBackward(_layout.AttentionOutput, size, size, hidden.Attention, dResidual1, dAttention);
        AddInPlace(hidden.Token.DX, dResidual1);

        var headSize = size / _config.HeadCount;
        var inverseScale = 1.0 / Math.Sqrt(headSize);
        var dQuery = new double[size];
        for (var head = 0; head < _config.HeadCount; head++)
        {
            var headOffset = head * headSize;
            var weightOffset = head * hidden.AttentionCount;
            var dWeights = new double[hidden.AttentionCount];
            for (var index = 0; index < hidden.AttentionCount; index++)
            {
                var context = tokens[hidden.AttentionStart + index];
                dWeights[index] = Dot(dAttention, headOffset, context.Value, headOffset, headSize);
                AddScaled(dAttention, headOffset,
                    hidden.AttentionWeights[weightOffset + index], context.DValue, headOffset, headSize);
            }

            var weightedGradient = 0.0;
            for (var index = 0; index < hidden.AttentionCount; index++)
                weightedGradient += hidden.AttentionWeights[weightOffset + index] * dWeights[index];

            for (var index = 0; index < hidden.AttentionCount; index++)
            {
                var context = tokens[hidden.AttentionStart + index];
                var dScore = hidden.AttentionWeights[weightOffset + index] *
                    (dWeights[index] - weightedGradient) * inverseScale;
                AddScaled(context.Key, headOffset, dScore, dQuery, headOffset, headSize);
                AddScaled(hidden.Query, headOffset, dScore, context.DKey, headOffset, headSize);
            }
        }

        MatBackward(_layout.Query, size, size, hidden.Token.Normalized, dQuery, hidden.Token.DNormalized);
    }

    private double CrossEntropy(
        int matrixOffset,
        int rows,
        int columns,
        double[] input,
        int target,
        double gradientScale,
        double[] dInput)
    {
        var logits = new double[rows];
        MatVec(matrixOffset, rows, columns, input, logits);
        var maximum = logits.Max();
        var sum = 0.0;
        for (var row = 0; row < rows; row++)
        {
            logits[row] = Math.Exp(logits[row] - maximum);
            sum += logits[row];
        }

        var loss = Math.Log(sum) + maximum - DotRow(matrixOffset, target, columns, input);
        for (var row = 0; row < rows; row++)
        {
            var gradient = (logits[row] / sum - (row == target ? 1.0 : 0.0)) * gradientScale;
            OuterRowAdd(_gradients, matrixOffset + row * columns, input, gradient);
            AddScaled(_weights, matrixOffset + row * columns, gradient, dInput, 0, columns);
        }
        return loss;
    }

    private void MatVec(int matrixOffset, int rows, int columns, double[] input, double[] output)
    {
        for (var row = 0; row < rows; row++)
            output[row] = Dot(_weights, matrixOffset + row * columns, input, 0, columns);
    }

    private void MatBackward(
        int matrixOffset,
        int rows,
        int columns,
        double[] input,
        double[] dOutput,
        double[] dInput)
    {
        for (var row = 0; row < rows; row++)
        {
            var gradient = dOutput[row];
            if (gradient == 0.0) continue;
            var rowOffset = matrixOffset + row * columns;
            OuterRowAdd(_gradients, rowOffset, input, gradient);
            AddScaled(_weights, rowOffset, gradient, dInput, 0, columns);
        }
    }

    private double DotRow(int matrixOffset, int row, int columns, double[] input) =>
        Dot(_weights, matrixOffset + row * columns, input, 0, columns);

    private static double RmsNorm(double[] input, double[] output)
    {
        var inverse = 1.0 / Math.Sqrt(Dot(input, 0, input, 0, input.Length) / input.Length + RmsEpsilon);
        Multiply(input, inverse, output);
        return inverse;
    }

    private static void RmsNormBackward(
        double[] input,
        double[] dOutput,
        double inverse,
        double[] dInput)
    {
        var correction = Dot(dOutput, 0, input, 0, input.Length) *
            inverse * inverse * inverse / input.Length;
        var width = Vector<double>.Count;
        var inverseVector = new Vector<double>(inverse);
        var correctionVector = new Vector<double>(correction);
        var index = 0;
        for (; index <= input.Length - width; index += width)
        {
            var result = new Vector<double>(dInput, index) +
                new Vector<double>(dOutput, index) * inverseVector -
                new Vector<double>(input, index) * correctionVector;
            result.CopyTo(dInput, index);
        }
        for (; index < input.Length; index++)
            dInput[index] += dOutput[index] * inverse - input[index] * correction;
    }

    private static double Dot(double[] left, int leftOffset, double[] right, int rightOffset, int count)
    {
        var width = Vector<double>.Count;
        var accumulator = Vector<double>.Zero;
        var index = 0;
        for (; index <= count - width; index += width)
            accumulator += new Vector<double>(left, leftOffset + index) *
                new Vector<double>(right, rightOffset + index);
        var result = Vector.Sum(accumulator);
        for (; index < count; index++) result += left[leftOffset + index] * right[rightOffset + index];
        return result;
    }

    private static void Add(double[] left, int leftOffset, double[] right, int rightOffset, double[] output)
    {
        var width = Vector<double>.Count;
        var index = 0;
        for (; index <= output.Length - width; index += width)
            (new Vector<double>(left, leftOffset + index) + new Vector<double>(right, rightOffset + index))
                .CopyTo(output, index);
        for (; index < output.Length; index++) output[index] = left[leftOffset + index] + right[rightOffset + index];
    }

    private static void AddInPlace(double[] destination, double[] source)
    {
        var width = Vector<double>.Count;
        var index = 0;
        for (; index <= destination.Length - width; index += width)
            (new Vector<double>(destination, index) + new Vector<double>(source, index)).CopyTo(destination, index);
        for (; index < destination.Length; index++) destination[index] += source[index];
    }

    private static void AddTo(double[] destination, int destinationOffset, double[] source)
    {
        var width = Vector<double>.Count;
        var index = 0;
        for (; index <= source.Length - width; index += width)
            (new Vector<double>(destination, destinationOffset + index) + new Vector<double>(source, index))
                .CopyTo(destination, destinationOffset + index);
        for (; index < source.Length; index++) destination[destinationOffset + index] += source[index];
    }

    private static void AddScaled(
        double[] source,
        int sourceOffset,
        double scale,
        double[] destination,
        int destinationOffset,
        int count)
    {
        var width = Vector<double>.Count;
        var scaleVector = new Vector<double>(scale);
        var index = 0;
        for (; index <= count - width; index += width)
        {
            var result = new Vector<double>(destination, destinationOffset + index) +
                new Vector<double>(source, sourceOffset + index) * scaleVector;
            result.CopyTo(destination, destinationOffset + index);
        }
        for (; index < count; index++) destination[destinationOffset + index] += source[sourceOffset + index] * scale;
    }

    private static void OuterRowAdd(double[] destination, int offset, double[] input, double scale)
    {
        AddScaled(input, 0, scale, destination, offset, input.Length);
    }

    private static void Multiply(double[] input, double scale, double[] output)
    {
        var width = Vector<double>.Count;
        var scaleVector = new Vector<double>(scale);
        var index = 0;
        for (; index <= input.Length - width; index += width)
            (new Vector<double>(input, index) * scaleVector).CopyTo(output, index);
        for (; index < input.Length; index++) output[index] = input[index] * scale;
    }

    private sealed class TokenCache(int id, int position, int size)
    {
        public int Id { get; } = id;
        public int Position { get; } = position;
        public double[] X { get; } = new double[size];
        public double[] Normalized { get; } = new double[size];
        public double NormInv { get; set; }
        public double[] Key { get; } = new double[size];
        public double[] Value { get; } = new double[size];
        public double[] DX { get; } = new double[size];
        public double[] DNormalized { get; } = new double[size];
        public double[] DKey { get; } = new double[size];
        public double[] DValue { get; } = new double[size];
    }

    private sealed class HiddenCache(
        TokenCache token,
        int attentionStart,
        int attentionCount,
        int size,
        int mlpSize,
        int headCount)
    {
        public TokenCache Token { get; } = token;
        public int AttentionStart { get; } = attentionStart;
        public int AttentionCount { get; } = attentionCount;
        public double[] Query { get; } = new double[size];
        public double[] AttentionWeights { get; } = new double[attentionCount * headCount];
        public double[] Attention { get; } = new double[size];
        public double[] Residual1 { get; } = new double[size];
        public double[] Normalized2 { get; } = new double[size];
        public double Norm2Inv { get; set; }
        public double[] MlpPre { get; } = new double[mlpSize];
        public double[] MlpActive { get; } = new double[mlpSize];
        public double[] Residual2 { get; } = new double[size];
        public double[] Final { get; } = new double[size];
        public double FinalInv { get; set; }
        public double[] DFinal { get; } = new double[size];
    }

    internal sealed class Layout
    {
        public Layout(BrainConfig config)
        {
            var size = config.EmbeddingSize;
            TokenEmbedding = 0;
            OutputHead = TokenEmbedding + Tokenizer.VocabularySize * size;
            PositionEmbedding = OutputHead + Tokenizer.OutputSize * size;
            Query = PositionEmbedding + config.PositionPeriod * size;
            Key = Query + size * size;
            Value = Key + size * size;
            AttentionOutput = Value + size * size;
            MlpIn = AttentionOutput + size * size;
            MlpOut = MlpIn + config.MlpSize * size;
            IntentHead = MlpOut + size * config.MlpSize;
            AffectHead = IntentHead + Enum.GetValues<DialogueIntent>().Length * size;
            ExpectedHead = AffectHead + Enum.GetValues<UserAffect>().Length * size;
            ParameterCount = ExpectedHead + 2 * size;
        }

        public int TokenEmbedding { get; }
        public int OutputHead { get; }
        public int PositionEmbedding { get; }
        public int Query { get; }
        public int Key { get; }
        public int Value { get; }
        public int AttentionOutput { get; }
        public int MlpIn { get; }
        public int MlpOut { get; }
        public int IntentHead { get; }
        public int AffectHead { get; }
        public int ExpectedHead { get; }
        public int ParameterCount { get; }
    }
}

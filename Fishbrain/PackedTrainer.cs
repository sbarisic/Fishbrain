using System.Numerics;

namespace Fishbrain;

/// <summary>Contiguous, SIMD-friendly forward/backward implementation for configurable Transformer layers.</summary>
internal sealed class PackedTrainer
{
    private const double RmsEpsilon = 1e-5;
    private readonly BrainConfig _config;
    private readonly DialogueTokenizer _tokenizer;
    private readonly Layout _layout;
    private readonly double[] _weights;
    private readonly double[] _gradients;

    private PackedTrainer(BrainConfig config, DialogueTokenizer tokenizer, double[] weights, double[] gradients)
    {
        _config = config;
        _tokenizer = tokenizer;
        _layout = new Layout(config, tokenizer.VocabularySize, tokenizer.OutputSize);
        _weights = weights;
        _gradients = gradients;
        if (weights.Length != _layout.ParameterCount || (gradients.Length != 0 && gradients.Length != weights.Length))
            throw new ArgumentException("Packed parameter storage does not match the model configuration.");
    }

    public static double Calculate(
        BrainConfig config,
        DialogueTokenizer tokenizer,
        double[] weights,
        double[] gradients,
        TrainingSample sample)
    {
        Array.Clear(gradients);
        return new PackedTrainer(config, tokenizer, weights, gradients).Calculate(sample);
    }

    public static double[] ContextVector(
        BrainConfig config,
        DialogueTokenizer tokenizer,
        double[] weights,
        IReadOnlyList<int> tokens)
    {
        if (tokens.Count is < 1 || tokens.Count > config.ContextLength)
            throw new ArgumentOutOfRangeException(nameof(tokens));
        var gradients = Array.Empty<double>();
        var trainer = new PackedTrainer(config, tokenizer, weights, gradients);
        var sequence = trainer.Forward(tokens.ToArray(), 0);
        return (double[])sequence.Layers[^1][^1].Final.Clone();
    }

    private double Calculate(TrainingSample sample)
    {
        if (sample.Task == TrainingTask.Perception) return CalculatePerception(sample);
        if (sample.FirstTargetIndex < 1 || sample.FirstTargetIndex >= sample.Tokens.Length)
            throw new ArgumentException("A training sample has no valid targets.", nameof(sample));

        var inputCount = sample.Tokens.Length - 1;
        var sequence = Forward(sample.Tokens.AsSpan(0, inputCount).ToArray(), sample.PositionOffset);
        var firstPosition = sample.FirstTargetIndex - 1;
        var targetCount = inputCount - firstPosition;
        var loss = 0.0;
        for (var index = 0; index < targetCount; index++)
        {
            var position = firstPosition + index;
            var final = sequence.Layers[^1][position];
            var target = _tokenizer.OutputId(sample.Tokens[sample.FirstTargetIndex + index]);
            loss += CrossEntropy(_layout.OutputHead, _tokenizer.OutputSize, _config.EmbeddingSize,
                final.Final, target, 1.0 / targetCount, final.DFinal);
        }
        Backward(sequence);
        return loss / targetCount;
    }

    private double CalculatePerception(TrainingSample sample)
    {
        var target = sample.PerceptionTarget
            ?? throw new ArgumentException("A perception sample requires a target.", nameof(sample));
        var sequence = Forward(sample.Tokens, sample.PositionOffset);
        var final = sequence.Layers[^1][^1];
        var headCount = 0;
        if (sample.TargetFields.HasFlag(PerceptionFields.Intent)) headCount++;
        if (sample.TargetFields.HasFlag(PerceptionFields.Affect)) headCount++;
        if (sample.TargetFields.HasFlag(PerceptionFields.Expected)) headCount++;
        if (headCount == 0) throw new ArgumentException("A perception sample has no supervised fields.", nameof(sample));

        var scale = 1.0 / headCount;
        var loss = 0.0;
        if (sample.TargetFields.HasFlag(PerceptionFields.Intent))
            loss += CrossEntropy(_layout.IntentHead, Enum.GetValues<DialogueIntent>().Length,
                _config.EmbeddingSize, final.Final, (int)target.Intent, scale, final.DFinal);
        if (sample.TargetFields.HasFlag(PerceptionFields.Affect))
            loss += CrossEntropy(_layout.AffectHead, Enum.GetValues<UserAffect>().Length,
                _config.EmbeddingSize, final.Final, (int)target.Affect, scale, final.DFinal);
        if (sample.TargetFields.HasFlag(PerceptionFields.Expected))
            loss += CrossEntropy(_layout.ExpectedHead, 2, _config.EmbeddingSize,
                final.Final, target.ResponseExpected ? 1 : 0, scale, final.DFinal);
        Backward(sequence);
        return loss / headCount;
    }

    private SequenceCache Forward(IReadOnlyList<int> tokenIds, int positionOffset)
    {
        if (tokenIds.Count is < 1 || tokenIds.Count > _config.ContextLength)
            throw new ArgumentOutOfRangeException(nameof(tokenIds));
        var size = _config.EmbeddingSize;
        var bases = new BaseToken[tokenIds.Count];
        for (var position = 0; position < tokenIds.Count; position++)
        {
            var token = new BaseToken(tokenIds[position], (positionOffset + position) % _config.PositionPeriod, size);
            Add(_weights, _layout.TokenEmbedding + token.Id * size,
                _weights, _layout.PositionEmbedding + token.Position * size, token.X);
            bases[position] = token;
        }

        var layers = new LayerToken[_config.LayerCount][];
        for (var layer = 0; layer < _config.LayerCount; layer++)
        {
            var tokens = new LayerToken[tokenIds.Count];
            for (var position = 0; position < tokenIds.Count; position++)
            {
                var input = layer == 0 ? bases[position].X : layers[layer - 1][position].Final;
                var token = new LayerToken(input, size, _config.MlpSize, _config.HeadCount,
                    Math.Min(position + 1, _config.AttentionWindow));
                token.NormInv = RmsNorm(input, token.Normalized);
                MatVec(_layout.Key[layer], size, size, token.Normalized, token.Key);
                MatVec(_layout.Value[layer], size, size, token.Normalized, token.Value);
                tokens[position] = token;
            }
            for (var position = 0; position < tokenIds.Count; position++) ForwardOutput(layer, tokens, position);
            layers[layer] = tokens;
        }
        return new SequenceCache(bases, layers);
    }

    private void ForwardOutput(int layer, LayerToken[] tokens, int position)
    {
        var size = _config.EmbeddingSize;
        var token = tokens[position];
        token.AttentionStart = Math.Max(0, position + 1 - _config.AttentionWindow);
        token.AttentionCount = position + 1 - token.AttentionStart;
        MatVec(_layout.Query[layer], size, size, token.Normalized, token.Query);
        var headSize = size / _config.HeadCount;
        var inverseScale = 1.0 / Math.Sqrt(headSize);
        for (var head = 0; head < _config.HeadCount; head++)
        {
            var headOffset = head * headSize;
            var weightOffset = head * token.AttentionCount;
            var maximum = double.NegativeInfinity;
            for (var index = 0; index < token.AttentionCount; index++)
            {
                var context = tokens[token.AttentionStart + index];
                var score = Dot(token.Query, headOffset, context.Key, headOffset, headSize) * inverseScale;
                token.AttentionWeights[weightOffset + index] = score;
                maximum = Math.Max(maximum, score);
            }
            var sum = 0.0;
            for (var index = 0; index < token.AttentionCount; index++)
            {
                var exponential = Math.Exp(token.AttentionWeights[weightOffset + index] - maximum);
                token.AttentionWeights[weightOffset + index] = exponential;
                sum += exponential;
            }
            for (var index = 0; index < token.AttentionCount; index++)
                token.AttentionWeights[weightOffset + index] /= sum;
            for (var index = 0; index < token.AttentionCount; index++)
            {
                var context = tokens[token.AttentionStart + index];
                AddScaled(context.Value, headOffset, token.AttentionWeights[weightOffset + index],
                    token.Attention, headOffset, headSize);
            }
        }

        MatVec(_layout.AttentionOutput[layer], size, size, token.Attention, token.Residual1);
        AddInPlace(token.Residual1, token.Input);
        token.Norm2Inv = RmsNorm(token.Residual1, token.Normalized2);
        MatVec(_layout.MlpIn[layer], _config.MlpSize, size, token.Normalized2, token.MlpPre);
        for (var index = 0; index < token.MlpPre.Length; index++)
            token.MlpActive[index] = Math.Max(0.0, token.MlpPre[index]);
        MatVec(_layout.MlpOut[layer], size, _config.MlpSize, token.MlpActive, token.Residual2);
        AddInPlace(token.Residual2, token.Residual1);
        token.FinalInv = RmsNorm(token.Residual2, token.Final);
    }

    private void Backward(SequenceCache sequence)
    {
        var size = _config.EmbeddingSize;
        for (var layer = _config.LayerCount - 1; layer >= 0; layer--)
        {
            var tokens = sequence.Layers[layer];
            for (var position = tokens.Length - 1; position >= 0; position--)
                BackwardOutput(layer, tokens, tokens[position]);

            for (var position = 0; position < tokens.Length; position++)
            {
                var token = tokens[position];
                MatBackward(_layout.Key[layer], size, size, token.Normalized, token.DKey, token.DNormalized);
                MatBackward(_layout.Value[layer], size, size, token.Normalized, token.DValue, token.DNormalized);
                RmsNormBackward(token.Input, token.DNormalized, token.NormInv, token.DInput);
                if (layer > 0) AddInPlace(sequence.Layers[layer - 1][position].DFinal, token.DInput);
                else
                {
                    var basis = sequence.Bases[position];
                    AddInPlace(basis.DX, token.DInput);
                    AddTo(_gradients, _layout.TokenEmbedding + basis.Id * size, basis.DX);
                    AddTo(_gradients, _layout.PositionEmbedding + basis.Position * size, basis.DX);
                }
            }
        }
    }

    private void BackwardOutput(int layer, LayerToken[] tokens, LayerToken token)
    {
        var size = _config.EmbeddingSize;
        var dResidual2 = new double[size];
        RmsNormBackward(token.Residual2, token.DFinal, token.FinalInv, dResidual2);
        var dMlpActive = new double[_config.MlpSize];
        MatBackward(_layout.MlpOut[layer], size, _config.MlpSize, token.MlpActive, dResidual2, dMlpActive);
        var dResidual1 = (double[])dResidual2.Clone();
        for (var index = 0; index < dMlpActive.Length; index++)
            if (token.MlpPre[index] <= 0.0) dMlpActive[index] = 0.0;
        var dNormalized2 = new double[size];
        MatBackward(_layout.MlpIn[layer], _config.MlpSize, size, token.Normalized2, dMlpActive, dNormalized2);
        RmsNormBackward(token.Residual1, dNormalized2, token.Norm2Inv, dResidual1);
        var dAttention = new double[size];
        MatBackward(_layout.AttentionOutput[layer], size, size, token.Attention, dResidual1, dAttention);
        AddInPlace(token.DInput, dResidual1);

        var headSize = size / _config.HeadCount;
        var inverseScale = 1.0 / Math.Sqrt(headSize);
        var dQuery = new double[size];
        for (var head = 0; head < _config.HeadCount; head++)
        {
            var headOffset = head * headSize;
            var weightOffset = head * token.AttentionCount;
            var dWeights = new double[token.AttentionCount];
            for (var index = 0; index < token.AttentionCount; index++)
            {
                var context = tokens[token.AttentionStart + index];
                dWeights[index] = Dot(dAttention, headOffset, context.Value, headOffset, headSize);
                AddScaled(dAttention, headOffset, token.AttentionWeights[weightOffset + index],
                    context.DValue, headOffset, headSize);
            }
            var weightedGradient = 0.0;
            for (var index = 0; index < token.AttentionCount; index++)
                weightedGradient += token.AttentionWeights[weightOffset + index] * dWeights[index];
            for (var index = 0; index < token.AttentionCount; index++)
            {
                var context = tokens[token.AttentionStart + index];
                var dScore = token.AttentionWeights[weightOffset + index] *
                    (dWeights[index] - weightedGradient) * inverseScale;
                AddScaled(context.Key, headOffset, dScore, dQuery, headOffset, headSize);
                AddScaled(token.Query, headOffset, dScore, context.DKey, headOffset, headSize);
            }
        }
        MatBackward(_layout.Query[layer], size, size, token.Normalized, dQuery, token.DNormalized);
    }

    private double CrossEntropy(
        int matrixOffset, int rows, int columns, double[] input, int target,
        double gradientScale, double[] dInput)
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

    private void MatVec(int offset, int rows, int columns, double[] input, double[] output)
    {
        for (var row = 0; row < rows; row++) output[row] = Dot(_weights, offset + row * columns, input, 0, columns);
    }

    private void MatBackward(int offset, int rows, int columns, double[] input, double[] dOutput, double[] dInput)
    {
        for (var row = 0; row < rows; row++)
        {
            var gradient = dOutput[row];
            if (gradient == 0.0) continue;
            var rowOffset = offset + row * columns;
            OuterRowAdd(_gradients, rowOffset, input, gradient);
            AddScaled(_weights, rowOffset, gradient, dInput, 0, columns);
        }
    }

    private double DotRow(int offset, int row, int columns, double[] input) =>
        Dot(_weights, offset + row * columns, input, 0, columns);

    private static double RmsNorm(double[] input, double[] output)
    {
        var inverse = 1.0 / Math.Sqrt(Dot(input, 0, input, 0, input.Length) / input.Length + RmsEpsilon);
        Multiply(input, inverse, output);
        return inverse;
    }

    private static void RmsNormBackward(double[] input, double[] dOutput, double inverse, double[] dInput)
    {
        var correction = Dot(dOutput, 0, input, 0, input.Length) * inverse * inverse * inverse / input.Length;
        var width = Vector<double>.Count;
        var inverseVector = new Vector<double>(inverse);
        var correctionVector = new Vector<double>(correction);
        var index = 0;
        for (; index <= input.Length - width; index += width)
        {
            var result = new Vector<double>(dInput, index) + new Vector<double>(dOutput, index) * inverseVector -
                new Vector<double>(input, index) * correctionVector;
            result.CopyTo(dInput, index);
        }
        for (; index < input.Length; index++) dInput[index] += dOutput[index] * inverse - input[index] * correction;
    }

    private static double Dot(double[] left, int leftOffset, double[] right, int rightOffset, int count)
    {
        var width = Vector<double>.Count;
        var accumulator = Vector<double>.Zero;
        var index = 0;
        for (; index <= count - width; index += width)
            accumulator += new Vector<double>(left, leftOffset + index) * new Vector<double>(right, rightOffset + index);
        var result = Vector.Sum(accumulator);
        for (; index < count; index++) result += left[leftOffset + index] * right[rightOffset + index];
        return result;
    }

    private static void Add(double[] left, int leftOffset, double[] right, int rightOffset, double[] output)
    {
        var width = Vector<double>.Count;
        var index = 0;
        for (; index <= output.Length - width; index += width)
            (new Vector<double>(left, leftOffset + index) + new Vector<double>(right, rightOffset + index)).CopyTo(output, index);
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

    private static void AddTo(double[] destination, int offset, double[] source)
    {
        var width = Vector<double>.Count;
        var index = 0;
        for (; index <= source.Length - width; index += width)
            (new Vector<double>(destination, offset + index) + new Vector<double>(source, index)).CopyTo(destination, offset + index);
        for (; index < source.Length; index++) destination[offset + index] += source[index];
    }

    private static void AddScaled(double[] source, int sourceOffset, double scale, double[] destination, int destinationOffset, int count)
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

    private static void OuterRowAdd(double[] destination, int offset, double[] input, double scale) =>
        AddScaled(input, 0, scale, destination, offset, input.Length);

    private static void Multiply(double[] input, double scale, double[] output)
    {
        var width = Vector<double>.Count;
        var scaleVector = new Vector<double>(scale);
        var index = 0;
        for (; index <= input.Length - width; index += width)
            (new Vector<double>(input, index) * scaleVector).CopyTo(output, index);
        for (; index < input.Length; index++) output[index] = input[index] * scale;
    }

    private sealed class BaseToken(int id, int position, int size)
    {
        public int Id { get; } = id;
        public int Position { get; } = position;
        public double[] X { get; } = new double[size];
        public double[] DX { get; } = new double[size];
    }

    private sealed class LayerToken(double[] input, int size, int mlpSize, int headCount, int maximumAttention)
    {
        public double[] Input { get; } = input;
        public double[] Normalized { get; } = new double[size];
        public double NormInv { get; set; }
        public double[] Key { get; } = new double[size];
        public double[] Value { get; } = new double[size];
        public double[] Query { get; } = new double[size];
        public int AttentionStart { get; set; }
        public int AttentionCount { get; set; }
        public double[] AttentionWeights { get; } = new double[Math.Max(1, maximumAttention) * headCount];
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
        public double[] DInput { get; } = new double[size];
        public double[] DNormalized { get; } = new double[size];
        public double[] DKey { get; } = new double[size];
        public double[] DValue { get; } = new double[size];
    }

    private sealed record SequenceCache(BaseToken[] Bases, LayerToken[][] Layers);

    internal sealed class Layout
    {
        public Layout(BrainConfig config, int vocabularySize, int outputSize)
        {
            if (vocabularySize <= 0 || outputSize <= 0) throw new ArgumentOutOfRangeException(nameof(vocabularySize));
            var size = config.EmbeddingSize;
            TokenEmbedding = 0;
            OutputHead = TokenEmbedding + vocabularySize * size;
            PositionEmbedding = OutputHead + outputSize * size;
            var offset = PositionEmbedding + config.PositionPeriod * size;
            Query = new int[config.LayerCount];
            Key = new int[config.LayerCount];
            Value = new int[config.LayerCount];
            AttentionOutput = new int[config.LayerCount];
            MlpIn = new int[config.LayerCount];
            MlpOut = new int[config.LayerCount];
            for (var layer = 0; layer < config.LayerCount; layer++)
            {
                Query[layer] = offset; offset += size * size;
                Key[layer] = offset; offset += size * size;
                Value[layer] = offset; offset += size * size;
                AttentionOutput[layer] = offset; offset += size * size;
                MlpIn[layer] = offset; offset += config.MlpSize * size;
                MlpOut[layer] = offset; offset += size * config.MlpSize;
            }
            IntentHead = offset; offset += Enum.GetValues<DialogueIntent>().Length * size;
            AffectHead = offset; offset += Enum.GetValues<UserAffect>().Length * size;
            ExpectedHead = offset; offset += 2 * size;
            ParameterCount = offset;
        }

        public int TokenEmbedding { get; }
        public int OutputHead { get; }
        public int PositionEmbedding { get; }
        public int[] Query { get; }
        public int[] Key { get; }
        public int[] Value { get; }
        public int[] AttentionOutput { get; }
        public int[] MlpIn { get; }
        public int[] MlpOut { get; }
        public int IntentHead { get; }
        public int AffectHead { get; }
        public int ExpectedHead { get; }
        public int ParameterCount { get; }
    }
}

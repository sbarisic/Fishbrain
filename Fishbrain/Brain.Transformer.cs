using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fishbrain;

public sealed partial class LegacyBrain
{
    private double CalculateLoss(TrainingSample sample, bool optimizedForward = true)
    {
        var window = sample.Tokens;
        if (window.Length < 2 || window.Length > Config.ContextLength + 1)
            throw new ArgumentException($"A training sample must contain 2-{Config.ContextLength + 1} tokens.");
        if (optimizedForward)
            return PackedTrainer.Calculate(Config, _tokenizer, _weights, _packedGradients, sample);

        SyncScalarWeights();
        if (sample.Task == TrainingTask.Perception)
            return CalculatePerceptionLossReference(sample);
        if (sample.FirstTargetIndex < 1 || sample.FirstTargetIndex >= window.Length)
            throw new ArgumentException("A training sample has no valid targets.");
        foreach (var parameter in _parameters) parameter.Grad = 0.0;

        var inputs = new int[window.Length - 1];
        for (var i = 0; i < inputs.Length; i++)
        {
            inputs[i] = window[i];
        }
        var firstLogitPosition = sample.FirstTargetIndex - 1;
        var logits = optimizedForward
            ? ForwardTargets(inputs, sample.PositionOffset, firstLogitPosition)
            : Forward(inputs, sample.PositionOffset)[firstLogitPosition..];
        var total = new Value(0.0);
        for (var index = 0; index < logits.Length; index++)
        {
            var target = _tokenizer.OutputId(window[sample.FirstTargetIndex + index]);
            total += Value.CrossEntropy(logits[index], target);
        }

        var loss = total / logits.Length;
        loss.Backward();
        CopyScalarGradients();
        return loss.Data;
    }

    private double CalculatePerceptionLossReference(TrainingSample sample)
    {
        var target = sample.PerceptionTarget
            ?? throw new ArgumentException("A perception sample requires a target.", nameof(sample));
        foreach (var parameter in _parameters) parameter.Grad = 0.0;

        var representation = ForwardLastHidden(sample.Tokens, sample.PositionOffset);
        var total = new Value(0.0);
        var count = 0;
        if (sample.TargetFields.HasFlag(PerceptionFields.Intent))
        {
            total += Value.CrossEntropy(Linear(representation, _intentHead), (int)target.Intent);
            count++;
        }
        if (sample.TargetFields.HasFlag(PerceptionFields.Affect))
        {
            total += Value.CrossEntropy(Linear(representation, _affectHead), (int)target.Affect);
            count++;
        }
        if (sample.TargetFields.HasFlag(PerceptionFields.Expected))
        {
            total += Value.CrossEntropy(Linear(representation, _expectedHead), target.ResponseExpected ? 1 : 0);
            count++;
        }
        if (count == 0) throw new ArgumentException("A perception sample has no supervised fields.", nameof(sample));
        var loss = total / count;
        loss.Backward();
        CopyScalarGradients();
        return loss.Data;
    }

    private void CopyScalarGradients()
    {
        for (var index = 0; index < _parameters.Count; index++)
            _packedGradients[index] = _parameters[index].Grad;
    }

    private void ApplyGradients(int targetSteps)
    {
        var updateStep = _step + 1;
        var languageEnd = Math.Max(1, targetSteps / 20);
        var phaseStart = _step < languageEnd ? 0 : languageEnd;
        var phaseEnd = _step < languageEnd ? languageEnd : targetSteps;
        var localStep = _step - phaseStart;
        var phaseLength = Math.Max(1, phaseEnd - phaseStart);
        var warmup = Math.Min(1.0, updateStep / 500.0);
        var decay = Math.Max(0.0, 1.0 - (double)localStep / phaseLength);
        var learningRate = Config.LearningRate * warmup * decay;
        var gradientNorm = Math.Sqrt(SumSquares(_packedGradients));
        var gradientScale = gradientNorm > 1.0 ? 1.0 / gradientNorm : 1.0;

        var beta1Correction = 1.0 - Math.Pow(Config.Beta1, updateStep);
        var beta2Correction = 1.0 - Math.Pow(Config.Beta2, updateStep);
        var width = Vector<double>.Count;
        var beta1 = new Vector<double>(Config.Beta1);
        var beta2 = new Vector<double>(Config.Beta2);
        var oneMinusBeta1 = new Vector<double>(1.0 - Config.Beta1);
        var oneMinusBeta2 = new Vector<double>(1.0 - Config.Beta2);
        var scale = new Vector<double>(gradientScale);
        var inverseBeta1Correction = new Vector<double>(1.0 / beta1Correction);
        var inverseBeta2Correction = new Vector<double>(1.0 / beta2Correction);
        var rate = new Vector<double>(learningRate);
        var epsilon = new Vector<double>(Config.AdamEpsilon);
        var index = 0;
        for (; index <= _weights.Length - width; index += width)
        {
            var gradient = new Vector<double>(_packedGradients, index) * scale;
            var moment = beta1 * new Vector<double>(_adamM, index) + oneMinusBeta1 * gradient;
            var variance = beta2 * new Vector<double>(_adamV, index) + oneMinusBeta2 * gradient * gradient;
            var updated = new Vector<double>(_weights, index) - rate *
                (moment * inverseBeta1Correction) /
                (Vector.SquareRoot(variance * inverseBeta2Correction) + epsilon);
            moment.CopyTo(_adamM, index);
            variance.CopyTo(_adamV, index);
            updated.CopyTo(_weights, index);
        }
        for (; index < _weights.Length; index++)
        {
            var gradient = _packedGradients[index] * gradientScale;
            _adamM[index] = Config.Beta1 * _adamM[index] + (1.0 - Config.Beta1) * gradient;
            _adamV[index] = Config.Beta2 * _adamV[index] + (1.0 - Config.Beta2) * gradient * gradient;
            var mHat = _adamM[index] / beta1Correction;
            var vHat = _adamV[index] / beta2Correction;
            _weights[index] -= learningRate * mHat / (Math.Sqrt(vHat) + Config.AdamEpsilon);
        }

        _scalarWeightsCurrent = false;
        _step = updateStep;
    }

    private static double SumSquares(double[] values)
    {
        var width = Vector<double>.Count;
        var accumulator = Vector<double>.Zero;
        var index = 0;
        for (; index <= values.Length - width; index += width)
        {
            var vector = new Vector<double>(values, index);
            accumulator += vector * vector;
        }
        var total = Vector.Sum(accumulator);
        for (; index < values.Length; index++)
        {
            total += values[index] * values[index];
        }
        return total;
    }

    private Value[][] Forward(IReadOnlyList<int> tokens, int positionOffset)
    {
        if (tokens.Count is < 1 || tokens.Count > Config.ContextLength)
            throw new ArgumentOutOfRangeException(nameof(tokens));
        if (positionOffset < 0) throw new ArgumentOutOfRangeException(nameof(positionOffset));
        return ForwardHiddenSequence(tokens, positionOffset)
            .Select(hidden => Linear(hidden, _outputHead)).ToArray();
    }

    private Value[] ForwardLastHidden(IReadOnlyList<int> tokens, int positionOffset)
    {
        if (tokens.Count is < 1 || tokens.Count > Config.ContextLength)
            throw new ArgumentOutOfRangeException(nameof(tokens));
        if (positionOffset < 0) throw new ArgumentOutOfRangeException(nameof(positionOffset));

        return ForwardHiddenSequence(tokens, positionOffset)[^1];
    }

    private Value[][] ForwardTargets(IReadOnlyList<int> tokens, int positionOffset, int firstLogitPosition)
    {
        if (tokens.Count is < 1 || tokens.Count > Config.ContextLength)
            throw new ArgumentOutOfRangeException(nameof(tokens));
        if (positionOffset < 0) throw new ArgumentOutOfRangeException(nameof(positionOffset));
        if ((uint)firstLogitPosition >= (uint)tokens.Count)
            throw new ArgumentOutOfRangeException(nameof(firstLogitPosition));

        var hidden = ForwardHiddenSequence(tokens, positionOffset);
        var result = new Value[tokens.Count - firstLogitPosition][];
        for (var position = firstLogitPosition; position < tokens.Count; position++)
            result[position - firstLogitPosition] = Linear(hidden[position], _outputHead);
        return result;
    }

    private Value[][] ForwardHiddenSequence(IReadOnlyList<int> tokens, int positionOffset)
    {
        var hidden = new Value[tokens.Count][];
        for (var position = 0; position < tokens.Count; position++)
        {
            hidden[position] = new Value[Config.EmbeddingSize];
            for (var column = 0; column < Config.EmbeddingSize; column++)
                hidden[position][column] = _tokenEmbedding[tokens[position]][column] +
                    _positionEmbedding[(positionOffset + position) % Config.PositionPeriod][column];
        }

        for (var layer = 0; layer < Config.LayerCount; layer++)
        {
            var normalized = hidden.Select(RmsNorm).ToArray();
            var keys = normalized.Select(value => Linear(value, _keyLayers[layer])).ToArray();
            var values = normalized.Select(value => Linear(value, _valueLayers[layer])).ToArray();
            var next = new Value[tokens.Count][];
            for (var position = 0; position < tokens.Count; position++)
            {
                var query = Linear(normalized[position], _queryLayers[layer]);
                var attention = new Value[Config.EmbeddingSize];
                var attentionStart = Math.Max(0, position + 1 - Config.AttentionWindow);
                var headSize = Config.EmbeddingSize / Config.HeadCount;
                for (var head = 0; head < Config.HeadCount; head++)
                {
                    var offset = head * headSize;
                    var scores = new Value[position + 1 - attentionStart];
                    for (var context = attentionStart; context <= position; context++)
                        scores[context - attentionStart] = Value.Dot(query, offset, keys[context], offset, headSize) /
                            Math.Sqrt(headSize);
                    var weights = Softmax(scores);
                    for (var column = 0; column < headSize; column++)
                    {
                        var valuesForColumn = new Value[scores.Length];
                        for (var context = attentionStart; context <= position; context++)
                            valuesForColumn[context - attentionStart] = values[context][offset + column];
                        attention[offset + column] = Value.Dot(weights, valuesForColumn);
                    }
                }

                var residual1 = Linear(attention, _attentionOutputLayers[layer]);
                for (var column = 0; column < residual1.Length; column++)
                {
                    residual1[column] += hidden[position][column];
                }
                var mlp = Linear(RmsNorm(residual1), _mlpInLayers[layer]);
                for (var column = 0; column < mlp.Length; column++)
                {
                    mlp[column] = mlp[column].Relu();
                }
                var residual2 = Linear(mlp, _mlpOutLayers[layer]);
                for (var column = 0; column < residual2.Length; column++)
                {
                    residual2[column] += residual1[column];
                }
                next[position] = RmsNorm(residual2);
            }
            hidden = next;
        }
        return hidden;
    }

    private double[] NextLogits(IReadOnlyList<int> context)
    {
        var retainedStart = Math.Max(0, context.Count - Config.ContextLength);
        var localStart = Math.Max(retainedStart, context.Count - Config.AttentionWindow);
        var count = context.Count - localStart;
        var tail = new int[count];
        for (var i = 0; i < count; i++)
        {
            tail[i] = context[localStart + i];
        }

        using var _ = Value.NoGrad();
        var logits = ForwardTargets(tail, localStart - retainedStart, tail.Length - 1);
        return logits[0].Select(x => x.Data).ToArray();
    }

    private string GenerateText(List<int> context, double temperature, DeterministicRandom random)
    {
        var output = new List<int>();
        var session = new InferenceSession(this, context);
        for (var i = 0; i < Config.MaximumOutputLength; i++)
        {
            var allowed = AllowedTextOutputs(output);
            var outputToken = Sample(session.Logits, allowed, temperature, random);
            if (outputToken == _tokenizer.OutputId(Tokenizer.Eos)) break;
            output.Add(outputToken);
            var inputToken = _tokenizer.InputIdFromOutput(outputToken);
            context.Add(inputToken);
            session.Append(inputToken);
        }

        var text = _tokenizer.DetokenizeOutput(output).Trim();
        return text.Length == 0 ? SafeFallback : text;
    }

    private string SelectSafeResponse(
        string generated,
        string input,
        DialogueIntent intent,
        ResponseAction action,
        ResponseTone tone)
    {
        var key = DialogueKeys.Catalog(intent, tone);
        if (!_responseCatalog.TryGetValue(key, out var candidates) || candidates.Length == 0)
        {
            candidates = _responseCatalog
                .Where(item => item.Key.StartsWith(intent + "|", StringComparison.Ordinal))
                .SelectMany(item => item.Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        if (candidates.Length == 0)
        {
            return action switch
            {
                ResponseAction.Clarify => "PLEASE EXPLAIN.",
                ResponseAction.Refuse => "I WILL NOT DO THAT.",
                ResponseAction.Respond => "I HEAR YOU.",
                _ => generated
            };
        }

        var currentTurn = ExtractCurrentPlayerTurn(input);
        var inputWords = Tokenizer.Lex(currentTurn)
            .Where(token => token.Kind == LexicalTokenKind.Word)
            .Select(token => token.Text)
            .ToHashSet(StringComparer.Ordinal);
        var bestOverlap = candidates.Max(candidate => Tokenizer.Lex(candidate)
            .Count(token => token.Kind == LexicalTokenKind.Word && inputWords.Contains(token.Text)));
        candidates = candidates.Where(candidate => Tokenizer.Lex(candidate)
                .Count(token => token.Kind == LexicalTokenKind.Word && inputWords.Contains(token.Text)) == bestOverlap)
            .ToArray();
        if (candidates.Contains(generated, StringComparer.Ordinal)) return generated;
        uint hash = 2166136261;
        foreach (var character in currentTurn)
        {
            hash ^= character;
            hash *= 16777619;
        }
        hash ^= (uint)tone;
        return candidates[(int)(hash % (uint)candidates.Length)];
    }

    private static bool CatalogEquals(
        IReadOnlyDictionary<string, string[]> left,
        IReadOnlyDictionary<string, string[]> right) =>
        left.Count == right.Count && left.All(item =>
            right.TryGetValue(item.Key, out var values) && item.Value.SequenceEqual(values));

    private int[] AllowedTextOutputs(IReadOnlyList<int> generated)
    {
        var allowed = _tokenizer.GeneratedTextOutputs.ToHashSet();
        var eos = _tokenizer.OutputId(Tokenizer.Eos);
        if (generated.Count == 0)
        {
            allowed.Remove(eos);
            foreach (var punctuation in new[]
                     {
                         Tokenizer.Period, Tokenizer.Comma, Tokenizer.Question,
                         Tokenizer.Exclamation, Tokenizer.Colon
                     })
                allowed.Remove(_tokenizer.OutputId(punctuation));
        }
        if (generated.Count >= 2 && generated[^1] == generated[^2])
            allowed.Remove(generated[^1]);
        if (generated.Count > 0)
        {
            var lastInput = _tokenizer.InputIdFromOutput(generated[^1]);
            if (lastInput is Tokenizer.Period or Tokenizer.Comma or Tokenizer.Question or
                Tokenizer.Exclamation or Tokenizer.Colon)
            {
                foreach (var punctuation in new[]
                         {
                             Tokenizer.Period, Tokenizer.Comma, Tokenizer.Question,
                             Tokenizer.Exclamation, Tokenizer.Colon
                         })
                    allowed.Remove(_tokenizer.OutputId(punctuation));
                if (lastInput is Tokenizer.Comma or Tokenizer.Colon) allowed.Remove(eos);
            }
        }
        if (generated.Count >= 2)
        {
            for (var index = 0; index + 2 < generated.Count; index++)
            {
                if (generated[index] == generated[^2] && generated[index + 1] == generated[^1])
                    allowed.Remove(generated[index + 2]);
            }
        }
        return allowed.Count == 0 ? [eos] : allowed.Order().ToArray();
    }

    private sealed class InferenceSession
    {
        private readonly LegacyBrain _brain;
        private readonly List<int> _context;

        public InferenceSession(LegacyBrain brain, IReadOnlyList<int> context)
        {
            _brain = brain;
            var retainedStart = Math.Max(0, context.Count - brain.Config.ContextLength);
            _context = context.Skip(retainedStart).ToList();
            if (_context.Count == 0)
                throw new ArgumentException("Inference context cannot be empty.", nameof(context));
            Logits = Calculate();
        }

        public double[] Logits { get; private set; }

        public void Append(int token)
        {
            _context.Add(token);
            if (_context.Count > _brain.Config.ContextLength) _context.RemoveAt(0);
            Logits = Calculate();
        }

        private double[] Calculate()
        {
            using var _ = Value.NoGrad();
            return _brain.ForwardTargets(_context, 0, _context.Count - 1)[0].Select(value => value.Data).ToArray();
        }
    }

    private int Greedy(IReadOnlyList<double> logits, IReadOnlyCollection<int> allowed)
    {
        if (allowed.Count == 0) throw new InvalidOperationException("No tokens are allowed in this decoding state.");
        return allowed.OrderBy(x => x).MaxBy(x => logits[x]);
    }

    private static int ArgMax(IReadOnlyList<Value> logits)
    {
        if (logits.Count == 0) throw new ArgumentException("Logits cannot be empty.", nameof(logits));
        var best = 0;
        for (var index = 1; index < logits.Count; index++)
            if (logits[index].Data > logits[best].Data) best = index;
        return best;
    }

    private static int Sample(
        IReadOnlyList<double> logits, IReadOnlyCollection<int> allowed, double temperature,
        DeterministicRandom random)
    {
        var tokens = allowed.Distinct().OrderBy(x => x).ToArray();
        var maximum = tokens.Max(x => logits[x] / temperature);
        var weights = tokens.Select(x => Math.Exp(logits[x] / temperature - maximum)).ToArray();
        var choice = random.NextDouble() * weights.Sum();
        for (var i = 0; i < tokens.Length; i++)
        {
            choice -= weights[i];
            if (choice <= 0) return tokens[i];
        }

        return tokens[^1];
    }

    private DeterministicRandom ReplyRandom(string input, NpcState state, int? seedOverride = null)
    {
        uint hash = 2166136261;
        foreach (var character in input)
        {
            hash ^= character;
            hash *= 16777619;
        }
        hash ^= state.Rapport;
        hash = hash * 16777619 ^ (uint)state.Mood;
        hash = hash * 16777619 ^ (uint)state.LastIntent;
        hash = hash * 16777619 ^ (uint)state.LastAffect;
        hash = hash * 16777619 ^ (uint)(seedOverride ?? Config.Seed);
        return new DeterministicRandom(unchecked((int)hash));
    }

    private Value[][] CreateMatrix(int rows, int columns)
    {
        var matrix = new Value[rows][];
        for (var row = 0; row < rows; row++)
        {
            matrix[row] = new Value[columns];
            for (var column = 0; column < columns; column++)
                matrix[row][column] = new Value(_random.NextGaussian() * 0.08);
        }
        return matrix;
    }

    private void AddParameters(IEnumerable<Value[]> matrix)
    {
        foreach (var row in matrix) _parameters.AddRange(row);
    }

    private static Value[] Linear(IReadOnlyList<Value> input, IReadOnlyList<Value[]> weights)
    {
        var result = new Value[weights.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Value.Dot(weights[i], input);
        }
        return result;
    }

    private static Value[] Softmax(IReadOnlyList<Value> logits)
    {
        var maximum = logits.Max(x => x.Data);
        var exponents = logits.Select(x => (x - maximum).Exp()).ToArray();
        var total = new Value(0.0);
        foreach (var exponent in exponents) total += exponent;
        return exponents.Select(x => x / total).ToArray();
    }

    private static Value[] RmsNorm(IReadOnlyList<Value> input)
    {
        var squares = new Value(0.0);
        foreach (var value in input) squares += value * value;
        var scale = (squares / input.Count + 1e-5).Pow(-0.5);
        return input.Select(x => x * scale).ToArray();
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string ComputeCheckpointIntegrity(Checkpoint checkpoint) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(checkpoint, JsonOptions())))).ToLowerInvariant();

    private sealed class Checkpoint
    {
        public BrainConfig Config { get; set; } = new();
        public string[] Words { get; set; } = [];
        public string[] OutputWords { get; set; } = [];
        public string[]? TrainedTools { get; set; }
        public Dictionary<string, string>? TrainedExamples { get; set; }
        public Dictionary<string, string[]>? ResponseCatalog { get; set; }
        public double[] Weights { get; set; } = [];
        public double[] AdamM { get; set; } = [];
        public double[] AdamV { get; set; } = [];
        public int CompletedSteps { get; set; }
        public ulong RandomState { get; set; }
        public string CurriculumPhase { get; set; } = "UNSTARTED";
        public int SamplerPosition { get; set; }
        public double BestPerceptionScore { get; set; } = -1.0;
        public int BestPerceptionStep { get; set; }
        public double BestRealizationLoss { get; set; } = double.MaxValue;
        public int BestRealizationStep { get; set; }
        public double[] StructuredWeights { get; set; } = [];
        public int StructuredUpdates { get; set; }
        public Dictionary<string, double>? StructuredLabelThresholds { get; set; }
        public string[]? FrozenStructuredHeads { get; set; }
        public Dictionary<string, ModelSchemas.ConfidenceThreshold>? ConfidenceCalibration { get; set; }
        public Dictionary<string, string[]>? LabelSchemas { get; set; }
        public ToolSchema[]? ToolSchemas { get; set; }
        public ResponseCandidate[]? CandidateCatalog { get; set; }
        public string? CorpusHash { get; set; }
        public string IntegrityChecksum { get; set; } = "";
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Fishbrain;

public sealed class BrainConfig
{
    public int EmbeddingSize { get; set; } = 32;
    public int HeadCount { get; set; } = 4;
    public int MlpSize { get; set; } = 64;
    public int ContextLength { get; set; } = 256;
    public int AttentionWindow { get; set; } = 32;
    public int PositionPeriod { get; set; } = 64;
    public int MaximumOutputLength { get; set; } = 256;
    public double LearningRate { get; set; } = 0.01;
    public double Beta1 { get; set; } = 0.85;
    public double Beta2 { get; set; } = 0.99;
    public double AdamEpsilon { get; set; } = 1e-8;
    public int Seed { get; set; } = 42;
    public int PlannedSteps { get; set; } = 10_000;

    internal void Validate()
    {
        if (EmbeddingSize <= 0 || HeadCount <= 0 || EmbeddingSize % HeadCount != 0)
            throw new InvalidDataException("EmbeddingSize must be positive and divisible by HeadCount.");
        if (MlpSize <= 0 || ContextLength <= 0 || MaximumOutputLength <= 0)
            throw new InvalidDataException("Model dimensions must be positive.");
        if (AttentionWindow <= 0 || AttentionWindow > ContextLength)
            throw new InvalidDataException("AttentionWindow must be within the context length.");
        if (PositionPeriod <= 0 || PositionPeriod > ContextLength || ContextLength % PositionPeriod != 0)
            throw new InvalidDataException("PositionPeriod must be a positive divisor of ContextLength.");
        if (LearningRate <= 0 || Beta1 is <= 0 or >= 1 || Beta2 is <= 0 or >= 1 || AdamEpsilon <= 0)
            throw new InvalidDataException("Optimizer settings are invalid.");
        if (PlannedSteps <= 0) throw new InvalidDataException("PlannedSteps must be positive.");
    }
}

/// <summary>A deliberately tiny, character-level GPT for uppercase video-game dialogue.</summary>
public sealed class Brain
{
    private const int CheckpointVersion = 2;
    private const string SafeFallback = "I DO NOT KNOW";
    private static readonly int[] TextTokens = [.. Enumerable.Range(0, Tokenizer.VisibleCount), Tokenizer.Eos];

    private readonly DeterministicRandom _random;
    private readonly HashSet<string> _trainedTools;
    private readonly HashSet<string> _trainedResponses;
    private readonly Dictionary<string, string> _trainedExamples;
    private readonly List<Value> _parameters = [];
    private readonly Value[][] _tokenEmbedding;
    private readonly Value[][] _positionEmbedding;
    private readonly Value[][] _query;
    private readonly Value[][] _key;
    private readonly Value[][] _value;
    private readonly Value[][] _attentionOutput;
    private readonly Value[][] _mlpIn;
    private readonly Value[][] _mlpOut;
    private double[] _adamM;
    private double[] _adamV;
    private int _step;

    private Brain(
        BrainConfig config,
        DeterministicRandom random,
        IEnumerable<string> trainedTools,
        IEnumerable<string> trainedResponses,
        IEnumerable<KeyValuePair<string, string>> trainedExamples)
    {
        config.Validate();
        Config = config;
        _random = random;
        _trainedTools = new HashSet<string>(trainedTools, StringComparer.Ordinal);
        _trainedResponses = new HashSet<string>(trainedResponses, StringComparer.Ordinal);
        _trainedExamples = new Dictionary<string, string>(trainedExamples, StringComparer.Ordinal);

        _tokenEmbedding = CreateMatrix(Tokenizer.VocabularySize, config.EmbeddingSize);
        _positionEmbedding = CreateMatrix(config.PositionPeriod, config.EmbeddingSize);
        _query = CreateMatrix(config.EmbeddingSize, config.EmbeddingSize);
        _key = CreateMatrix(config.EmbeddingSize, config.EmbeddingSize);
        _value = CreateMatrix(config.EmbeddingSize, config.EmbeddingSize);
        _attentionOutput = CreateMatrix(config.EmbeddingSize, config.EmbeddingSize);
        _mlpIn = CreateMatrix(config.MlpSize, config.EmbeddingSize);
        _mlpOut = CreateMatrix(config.EmbeddingSize, config.MlpSize);

        AddParameters(_tokenEmbedding);
        AddParameters(_positionEmbedding);
        AddParameters(_query);
        AddParameters(_key);
        AddParameters(_value);
        AddParameters(_attentionOutput);
        AddParameters(_mlpIn);
        AddParameters(_mlpOut);

        _adamM = new double[_parameters.Count];
        _adamV = new double[_parameters.Count];
        Tools = new ToolRegistry(_trainedTools);
    }

    public BrainConfig Config { get; }
    public ToolRegistry Tools { get; }
    public int CompletedSteps => _step;
    public IReadOnlyCollection<string> TrainedTools => _trainedTools;

    public static Brain Load(string path)
    {
        var checkpoint = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(path), JsonOptions())
            ?? throw new InvalidDataException("Checkpoint is empty.");
        if (checkpoint.Version != CheckpointVersion)
            throw new InvalidDataException($"Unsupported checkpoint version {checkpoint.Version}.");
        checkpoint.Config.Validate();

        var brain = new Brain(
            checkpoint.Config,
            new DeterministicRandom(checkpoint.Config.Seed),
            checkpoint.TrainedTools ?? [],
            checkpoint.TrainedResponses ?? [],
            checkpoint.TrainedExamples ?? new Dictionary<string, string>());

        if (checkpoint.Weights.Length != brain._parameters.Count ||
            checkpoint.AdamM.Length != brain._parameters.Count ||
            checkpoint.AdamV.Length != brain._parameters.Count)
        {
            throw new InvalidDataException("Checkpoint parameter counts do not match its configuration.");
        }

        for (var i = 0; i < brain._parameters.Count; i++) brain._parameters[i].Data = checkpoint.Weights[i];
        brain._adamM = checkpoint.AdamM;
        brain._adamV = checkpoint.AdamV;
        brain._step = checkpoint.CompletedSteps;
        brain._random.State = checkpoint.RandomState;
        return brain;
    }

    public string Reply(string recentDialogue, double temperature = 0.2)
    {
        if (temperature <= 0) throw new ArgumentOutOfRangeException(nameof(temperature));
        var input = Tokenizer.Normalize(recentDialogue);
        if (input.Length == 0) throw new ArgumentException("Dialogue cannot be empty.", nameof(recentDialogue));
        if (_trainedExamples.TryGetValue(input, out var trainedResponse)) return trainedResponse;

        var basePrompt = new List<int> { Tokenizer.Bos };
        basePrompt.AddRange(Tokenizer.Encode(input));
        basePrompt.Add(Tokenizer.Sep);

        var mode = Greedy(NextLogits(basePrompt), [Tokenizer.Text, Tokenizer.Call]);
        if (mode == Tokenizer.Text)
        {
            basePrompt.Add(Tokenizer.Text);
            return GenerateText(basePrompt, temperature);
        }

        var callContext = new List<int>(basePrompt) { Tokenizer.Call };
        if (!TryGenerateToolCall(callContext, out var toolName, out var arguments, out var callBody) ||
            !Tools.TryInvoke(toolName, arguments, out var result))
        {
            return SafeFallback;
        }

        var resultPrompt = new List<int>(basePrompt) { Tokenizer.Call };
        resultPrompt.AddRange(callBody);
        resultPrompt.Add(Tokenizer.Result);
        resultPrompt.AddRange(Tokenizer.Encode(result));
        resultPrompt.Add(Tokenizer.Sep);
        resultPrompt.Add(Tokenizer.Text);
        return GenerateText(resultPrompt, temperature);
    }

    internal static void TrainNew(string dataPath, string checkpointPath, int plannedSteps)
    {
        if (File.Exists(checkpointPath))
            throw new IOException($"Checkpoint '{checkpointPath}' already exists. Use resume or choose another path.");

        var data = TrainingData.Load(dataPath);
        var config = new BrainConfig { PlannedSteps = plannedSteps };
        var brain = new Brain(
            config,
            new DeterministicRandom(config.Seed),
            data.ToolNames,
            data.Responses,
            data.Examples);
        brain.Train(data.Samples, checkpointPath, plannedSteps);
    }

    internal static void Resume(string dataPath, string checkpointPath, int? targetSteps)
    {
        var brain = Load(checkpointPath);
        var data = TrainingData.Load(dataPath);
        if (!brain._trainedTools.SetEquals(data.ToolNames))
            throw new InvalidDataException("Training data tools differ from the checkpoint's trained tool set.");
        if (brain._trainedResponses.Count == 0)
            brain._trainedResponses.UnionWith(data.Responses);
        else if (!brain._trainedResponses.SetEquals(data.Responses))
            throw new InvalidDataException("Training data responses differ from the checkpoint's trained response set.");
        if (brain._trainedExamples.Count == 0)
        {
            foreach (var example in data.Examples) brain._trainedExamples.Add(example.Key, example.Value);
        }
        else if (brain._trainedExamples.Count != data.Examples.Count ||
                 brain._trainedExamples.Any(example =>
                     !data.Examples.TryGetValue(example.Key, out var response) || response != example.Value))
        {
            throw new InvalidDataException("Training examples differ from the checkpoint's trained example set.");
        }

        var target = targetSteps ?? brain.Config.PlannedSteps;
        if (target <= brain._step)
            throw new ArgumentOutOfRangeException(nameof(targetSteps), "Target steps must exceed completed steps.");
        brain.Config.PlannedSteps = target;
        brain.Train(data.Samples, checkpointPath, target);
    }

    internal void Save(string path)
    {
        var checkpoint = new Checkpoint
        {
            Version = CheckpointVersion,
            Config = Config,
            TrainedTools = _trainedTools.Order(StringComparer.Ordinal).ToArray(),
            TrainedResponses = _trainedResponses.Order(StringComparer.Ordinal).ToArray(),
            TrainedExamples = _trainedExamples
                .OrderBy(example => example.Key, StringComparer.Ordinal)
                .ToDictionary(example => example.Key, example => example.Value, StringComparer.Ordinal),
            Weights = _parameters.Select(p => p.Data).ToArray(),
            AdamM = _adamM,
            AdamV = _adamV,
            CompletedSteps = _step,
            RandomState = _random.State
        };

        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(checkpoint, JsonOptions()));
        File.Move(temporaryPath, path, true);
    }

    internal static Brain CreateForTesting(BrainConfig config, params string[] trainedTools) =>
        new(config, new DeterministicRandom(config.Seed), trainedTools, [], []);

    internal static Brain CreateForTestingWithExamples(
        BrainConfig config,
        IReadOnlyDictionary<string, string> examples) =>
        new(config, new DeterministicRandom(config.Seed), [], examples.Values, examples);

    internal double[] DebugNextLogits(IReadOnlyList<int> tokens) => NextLogits(tokens);
    internal double[] DebugWeights() => _parameters.Select(p => p.Data).ToArray();

    internal double[] DebugLogitsAt(IReadOnlyList<int> tokens, int position)
    {
        using var _ = Value.NoGrad();
        return Forward(tokens, 0)[position].Select(x => x.Data).ToArray();
    }

    internal double DebugTrainWindow(IReadOnlyList<int> window, int targetSteps)
    {
        var loss = CalculateLoss(new TrainingSample([.. window], 0, 1));
        ApplyGradients(targetSteps);
        return loss;
    }

    private void Train(IReadOnlyList<TrainingSample> samples, string checkpointPath, int targetSteps)
    {
        if (samples.Count == 0) throw new InvalidDataException("Training data produced no training samples.");
        Config.PlannedSteps = targetSteps;
        var epoch = -1;
        int[] order = [];

        while (_step < targetSteps)
        {
            var currentEpoch = _step / samples.Count;
            if (currentEpoch != epoch)
            {
                epoch = currentEpoch;
                order = EpochOrder(samples.Count, currentEpoch);
            }

            var sample = samples[order[_step % samples.Count]];
            var loss = CalculateLoss(sample);
            ApplyGradients(targetSteps);

            if (_step == 1 || _step % 10 == 0)
                Console.WriteLine($"STEP {_step,6} OF {targetSteps,6} LOSS {loss:F4}");
            if (_step % 100 == 0) Save(checkpointPath);
        }

        Save(checkpointPath);
    }

    private int[] EpochOrder(int count, int epoch)
    {
        var order = Enumerable.Range(0, count).ToArray();
        var random = new DeterministicRandom(unchecked(Config.Seed + epoch * 7919));
        for (var i = order.Length - 1; i > 0; i--)
        {
            var other = random.NextInt(i + 1);
            (order[i], order[other]) = (order[other], order[i]);
        }
        return order;
    }

    private double CalculateLoss(TrainingSample sample)
    {
        var window = sample.Tokens;
        if (window.Length is < 2 or > 257) throw new ArgumentException("A training sample must contain 2-257 tokens.");
        if (sample.FirstTargetIndex < 1 || sample.FirstTargetIndex >= window.Length)
            throw new ArgumentException("A training sample has no valid targets.");
        foreach (var parameter in _parameters) parameter.Grad = 0.0;

        var inputs = new int[window.Length - 1];
        for (var i = 0; i < inputs.Length; i++) inputs[i] = window[i];
        var logits = Forward(inputs, sample.PositionOffset);
        var total = new Value(0.0);
        var targetCount = 0;

        for (var i = sample.FirstTargetIndex - 1; i < logits.Length; i++)
        {
            var target = window[i + 1];
            var maximum = logits[i].Max(x => x.Data);
            var sum = new Value(0.0);
            foreach (var logit in logits[i]) sum += (logit - maximum).Exp();
            total += sum.Log() + maximum - logits[i][target];
            targetCount++;
        }

        var loss = total / targetCount;
        loss.Backward();
        return loss.Data;
    }

    private void ApplyGradients(int targetSteps)
    {
        var updateStep = _step + 1;
        var learningRate = Config.LearningRate * Math.Max(0.0, 1.0 - (double)_step / targetSteps);

        for (var i = 0; i < _parameters.Count; i++)
        {
            var gradient = _parameters[i].Grad;
            _adamM[i] = Config.Beta1 * _adamM[i] + (1.0 - Config.Beta1) * gradient;
            _adamV[i] = Config.Beta2 * _adamV[i] + (1.0 - Config.Beta2) * gradient * gradient;
            var mHat = _adamM[i] / (1.0 - Math.Pow(Config.Beta1, updateStep));
            var vHat = _adamV[i] / (1.0 - Math.Pow(Config.Beta2, updateStep));
            _parameters[i].Data -= learningRate * mHat / (Math.Sqrt(vHat) + Config.AdamEpsilon);
        }

        _step = updateStep;
    }

    private Value[][] Forward(IReadOnlyList<int> tokens, int positionOffset)
    {
        if (tokens.Count is < 1 || tokens.Count > Config.ContextLength)
            throw new ArgumentOutOfRangeException(nameof(tokens));
        if (positionOffset < 0) throw new ArgumentOutOfRangeException(nameof(positionOffset));

        var keys = new List<Value[]>(tokens.Count);
        var values = new List<Value[]>(tokens.Count);
        var result = new Value[tokens.Count][];
        for (var position = 0; position < tokens.Count; position++)
            result[position] = ForwardToken(tokens[position], positionOffset + position, keys, values);
        return result;
    }

    private Value[] ForwardToken(int token, int position, List<Value[]> keys, List<Value[]> values)
    {
        var x = new Value[Config.EmbeddingSize];
        for (var i = 0; i < x.Length; i++)
            x[i] = _tokenEmbedding[token][i] + _positionEmbedding[position % Config.PositionPeriod][i];

        var residual = x;
        var normalized = RmsNorm(x);
        var query = Linear(normalized, _query);
        var key = Linear(normalized, _key);
        var value = Linear(normalized, _value);
        keys.Add(key);
        values.Add(value);

        var headSize = Config.EmbeddingSize / Config.HeadCount;
        var attention = new Value[Config.EmbeddingSize];
        var attentionStart = Math.Max(0, keys.Count - Config.AttentionWindow);
        for (var head = 0; head < Config.HeadCount; head++)
        {
            var offset = head * headSize;
            var scores = new Value[keys.Count - attentionStart];
            for (var t = attentionStart; t < keys.Count; t++)
                scores[t - attentionStart] = Value.Dot(query, offset, keys[t], offset, headSize) / Math.Sqrt(headSize);
            var weights = Softmax(scores);

            for (var j = 0; j < headSize; j++)
            {
                var column = new Value[values.Count - attentionStart];
                for (var t = attentionStart; t < values.Count; t++)
                    column[t - attentionStart] = values[t][offset + j];
                attention[offset + j] = Value.Dot(weights, column);
            }
        }

        x = Linear(attention, _attentionOutput);
        for (var i = 0; i < x.Length; i++) x[i] += residual[i];

        residual = x;
        x = RmsNorm(x);
        x = Linear(x, _mlpIn);
        for (var i = 0; i < x.Length; i++) x[i] = x[i].Relu();
        x = Linear(x, _mlpOut);
        for (var i = 0; i < x.Length; i++) x[i] += residual[i];
        x = RmsNorm(x);

        // The token embedding matrix is deliberately reused as the language-model head.
        return Linear(x, _tokenEmbedding);
    }

    private double[] NextLogits(IReadOnlyList<int> context)
    {
        var retainedStart = Math.Max(0, context.Count - Config.ContextLength);
        var localStart = Math.Max(retainedStart, context.Count - Config.AttentionWindow);
        var count = context.Count - localStart;
        var tail = new int[count];
        for (var i = 0; i < count; i++) tail[i] = context[localStart + i];

        using var _ = Value.NoGrad();
        var logits = Forward(tail, localStart - retainedStart);
        return logits[^1].Select(x => x.Data).ToArray();
    }

    private string GenerateText(List<int> context, double temperature)
    {
        if (_trainedResponses.Count > 0)
            return GenerateTrainedResponse(context, temperature);

        var output = new StringBuilder();
        for (var i = 0; i < Config.MaximumOutputLength; i++)
        {
            var token = Sample(NextLogits(context), TextTokens, temperature);
            context.Add(token);
            if (token == Tokenizer.Eos) break;
            output.Append(Tokenizer.DecodeVisible(token));
        }

        var text = output.ToString().Trim();
        return text.Length == 0 ? SafeFallback : text;
    }

    private string GenerateTrainedResponse(List<int> context, double temperature)
    {
        var candidates = _trainedResponses.Order(StringComparer.Ordinal).ToList();
        var output = new StringBuilder();

        for (var i = 0; i < Config.MaximumOutputLength; i++)
        {
            var allowed = new HashSet<int>();
            foreach (var candidate in candidates)
            {
                if (candidate.Length == output.Length)
                    allowed.Add(Tokenizer.Eos);
                else
                    allowed.Add(Tokenizer.EncodeCharacter(candidate[output.Length]));
            }

            if (allowed.Count == 0) return SafeFallback;
            var token = Sample(NextLogits(context), [.. allowed.Order()], temperature);
            context.Add(token);
            if (token == Tokenizer.Eos) return output.ToString();

            output.Append(Tokenizer.DecodeVisible(token));
            var prefix = output.ToString();
            candidates.RemoveAll(candidate => !candidate.StartsWith(prefix, StringComparison.Ordinal));
            if (candidates.Count == 0) return SafeFallback;
        }

        return SafeFallback;
    }

    private bool TryGenerateToolCall(
        List<int> context,
        out string toolName,
        out string[] arguments,
        out int[] callBody)
    {
        toolName = string.Empty;
        arguments = [];
        callBody = [];

        var candidates = Tools.RegisteredNames.Order(StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0) return false;

        var body = new List<int>();
        var prefix = new StringBuilder();
        ToolRegistry.ToolMethod? selected = null;

        while (prefix.Length < 32)
        {
            var matching = candidates.Where(x => x.StartsWith(prefix.ToString(), StringComparison.Ordinal)).ToArray();
            if (matching.Length == 0) return false;

            var allowed = matching
                .Where(x => x.Length > prefix.Length)
                .Select(x => Tokenizer.EncodeCharacter(x[prefix.Length]))
                .Distinct()
                .ToList();

            var exact = matching.FirstOrDefault(x => x.Length == prefix.Length);
            if (exact is not null && Tools.TryGet(exact, out var exactTool))
                allowed.Add(exactTool.ParameterTypes.Length == 0 ? Tokenizer.Eos : Tokenizer.Space);

            var next = Greedy(NextLogits(context), allowed);
            context.Add(next);
            if (next == Tokenizer.Eos || next == Tokenizer.Space)
            {
                if (exact is null || !Tools.TryGet(exact, out selected)) return false;
                toolName = exact;
                break;
            }

            body.Add(next);
            prefix.Append(Tokenizer.DecodeVisible(next));
        }

        if (selected is null) return false;
        if (selected.ParameterTypes.Length == 0)
        {
            arguments = [];
            callBody = body.ToArray();
            return true;
        }

        body.Add(Tokenizer.Space);
        var rawArguments = new List<string>();
        for (var parameterIndex = 0; parameterIndex < selected.ParameterTypes.Length; parameterIndex++)
        {
            var value = new StringBuilder();
            var maximumLength = selected.ParameterTypes[parameterIndex] == typeof(int) ? 10 : 32;
            while (value.Length < maximumLength)
            {
                var allowed = selected.ParameterTypes[parameterIndex] == typeof(int)
                    ? Enumerable.Range(Tokenizer.DigitStart, 10).ToList()
                    : Enumerable.Range(0, 36).ToList();

                if (value.Length > 0)
                    allowed.Add(parameterIndex == selected.ParameterTypes.Length - 1 ? Tokenizer.Eos : Tokenizer.Space);

                var next = Greedy(NextLogits(context), allowed);
                context.Add(next);
                if (next == Tokenizer.Eos || next == Tokenizer.Space) break;
                body.Add(next);
                value.Append(Tokenizer.DecodeVisible(next));
            }

            if (value.Length == 0) return false;
            rawArguments.Add(value.ToString());

            var expectedDelimiter = parameterIndex == selected.ParameterTypes.Length - 1 ? Tokenizer.Eos : Tokenizer.Space;
            if (context[^1] != expectedDelimiter)
            {
                context.Add(expectedDelimiter);
            }
            if (expectedDelimiter == Tokenizer.Space) body.Add(Tokenizer.Space);
        }

        arguments = rawArguments.ToArray();
        callBody = body.ToArray();
        return true;
    }

    private int Greedy(IReadOnlyList<double> logits, IReadOnlyCollection<int> allowed)
    {
        if (allowed.Count == 0) throw new InvalidOperationException("No tokens are allowed in this decoding state.");
        return allowed.OrderBy(x => x).MaxBy(x => logits[x]);
    }

    private int Sample(IReadOnlyList<double> logits, IReadOnlyCollection<int> allowed, double temperature)
    {
        var tokens = allowed.Distinct().OrderBy(x => x).ToArray();
        var maximum = tokens.Max(x => logits[x] / temperature);
        var weights = tokens.Select(x => Math.Exp(logits[x] / temperature - maximum)).ToArray();
        var choice = _random.NextDouble() * weights.Sum();
        for (var i = 0; i < tokens.Length; i++)
        {
            choice -= weights[i];
            if (choice <= 0) return tokens[i];
        }

        return tokens[^1];
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
        for (var i = 0; i < result.Length; i++) result[i] = Value.Dot(weights[i], input);
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
        WriteIndented = true
    };

    private sealed class Checkpoint
    {
        public int Version { get; set; }
        public BrainConfig Config { get; set; } = new();
        public string[]? TrainedTools { get; set; }
        public string[]? TrainedResponses { get; set; }
        public Dictionary<string, string>? TrainedExamples { get; set; }
        public double[] Weights { get; set; } = [];
        public double[] AdamM { get; set; } = [];
        public double[] AdamV { get; set; } = [];
        public int CompletedSteps { get; set; }
        public ulong RandomState { get; set; }
    }
}

internal static class Tokenizer
{
    public const int LetterStart = 0;
    public const int DigitStart = 26;
    public const int Space = 36;
    public const int VisibleCount = 37;
    public const int Bos = 37;
    public const int Sep = 38;
    public const int Eos = 39;
    public const int Text = 40;
    public const int Call = 41;
    public const int Result = 42;
    public const int VocabularySize = 43;

    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var output = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var original in text)
        {
            if (char.IsWhiteSpace(original))
            {
                pendingSpace = output.Length > 0;
                continue;
            }

            var character = char.ToUpperInvariant(original);
            if (!IsIdentifierCharacter(character))
                throw new ArgumentException($"Unsupported character '{original}'. Only A-Z, 0-9, and whitespace are allowed.");
            if (pendingSpace) output.Append(' ');
            output.Append(character);
            pendingSpace = false;
        }

        return output.ToString();
    }

    public static int[] Encode(string normalized)
    {
        var result = new int[normalized.Length];
        for (var i = 0; i < normalized.Length; i++) result[i] = EncodeCharacter(normalized[i]);
        return result;
    }

    public static int EncodeCharacter(char character) => character switch
    {
        >= 'A' and <= 'Z' => character - 'A',
        >= '0' and <= '9' => DigitStart + character - '0',
        ' ' => Space,
        _ => throw new ArgumentException($"Unsupported visible character '{character}'.")
    };

    public static char DecodeVisible(int token) => token switch
    {
        >= 0 and < 26 => (char)('A' + token),
        >= DigitStart and < Space => (char)('0' + token - DigitStart),
        Space => ' ',
        _ => throw new ArgumentOutOfRangeException(nameof(token), "Internal tokens cannot be decoded as visible text.")
    };

    public static bool IsIdentifierCharacter(char character) =>
        character is >= 'A' and <= 'Z' or >= '0' and <= '9';
}

internal sealed record TrainingSample(int[] Tokens, int PositionOffset, int FirstTargetIndex);

internal sealed class TrainingData
{
    internal const int ConditioningLength = 32;
    internal const int TargetChunkLength = 32;
    internal const int MaximumSampleLength = ConditioningLength + TargetChunkLength;

    private TrainingData(
        List<TrainingSample> samples,
        HashSet<string> toolNames,
        HashSet<string> responses,
        Dictionary<string, string> examples)
    {
        Samples = samples;
        ToolNames = toolNames;
        Responses = responses;
        Examples = examples;
    }

    public IReadOnlyList<TrainingSample> Samples { get; }
    public IReadOnlySet<string> ToolNames { get; }
    public IReadOnlySet<string> Responses { get; }
    public IReadOnlyDictionary<string, string> Examples { get; }

    public static TrainingData Load(string path)
    {
        var samples = new List<TrainingSample>();
        var tools = new HashSet<string>(StringComparer.Ordinal);
        var responses = new HashSet<string>(StringComparer.Ordinal);
        var examples = new Dictionary<string, string>(StringComparer.Ordinal);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var lineNumber = 0;

        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var row = JsonSerializer.Deserialize<TrainingRow>(line, options)
                    ?? throw new InvalidDataException("Empty object.");
                AddRow(row, samples, tools, responses, examples);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
            {
                throw new InvalidDataException($"Invalid training data on line {lineNumber}: {exception.Message}", exception);
            }
        }

        if (samples.Count == 0) throw new InvalidDataException("Training data contains no examples.");
        return new TrainingData(samples, tools, responses, examples);
    }

    private static void AddRow(
        TrainingRow row,
        List<TrainingSample> samples,
        HashSet<string> tools,
        HashSet<string> responses,
        Dictionary<string, string> examples)
    {
        if (row.Input is null || row.Response is null)
            throw new InvalidDataException("Input and response are required.");

        var input = Tokenizer.Normalize(row.Input);
        var response = Tokenizer.Normalize(row.Response);
        if (input.Length == 0 || response.Length == 0)
            throw new InvalidDataException("Input and response cannot be empty.");
        if (response.Length > 256) throw new InvalidDataException("Response exceeds 256 characters.");
        responses.Add(response);

        var hasAnyToolField = row.Tool is not null || row.Arguments is not null || row.Result is not null;
        var hasAllToolFields = row.Tool is not null && row.Arguments is not null && row.Result is not null;
        if (hasAnyToolField != hasAllToolFields)
            throw new InvalidDataException("Tool, arguments, and result must be supplied together.");

        if (!hasAllToolFields)
        {
            if (examples.TryGetValue(input, out var existingResponse) && existingResponse != response)
                throw new InvalidDataException("The same input cannot have competing responses.");
            examples[input] = response;
            AddSamples(SerializeNormal(input, response), samples);
            return;
        }

        var tool = Tokenizer.Normalize(row.Tool!);
        if (tool.Length is < 1 or > 32 || tool.Any(c => !Tokenizer.IsIdentifierCharacter(c)))
            throw new InvalidDataException("Tool names must be 1-32 uppercase alphanumeric characters without spaces.");

        var arguments = row.Arguments!.Select(Tokenizer.Normalize).ToArray();
        if (arguments.Any(x => x.Length is < 1 or > 32 || x.Any(c => !Tokenizer.IsIdentifierCharacter(c))))
            throw new InvalidDataException("Tool arguments must be 1-32 character uppercase alphanumeric identifiers or integers.");

        var result = Tokenizer.Normalize(row.Result!);
        if (result.Length is < 1 or > 64) throw new InvalidDataException("Tool results must contain 1-64 characters.");
        tools.Add(tool);
        AddSamples(SerializeToolCall(input, tool, arguments), samples);
        AddSamples(SerializeToolResult(input, tool, arguments, result, response), samples);
    }

    private static SerializedStream SerializeNormal(string input, string response)
    {
        var tokens = Start(input);
        var targetStart = tokens.Count;
        tokens.Add(Tokenizer.Text);
        tokens.AddRange(Tokenizer.Encode(response));
        tokens.Add(Tokenizer.Eos);
        return new SerializedStream(tokens.ToArray(), targetStart);
    }

    private static SerializedStream SerializeToolCall(string input, string tool, IReadOnlyList<string> arguments)
    {
        var tokens = Start(input);
        var targetStart = tokens.Count;
        tokens.Add(Tokenizer.Call);
        AddCallBody(tokens, tool, arguments);
        tokens.Add(Tokenizer.Eos);
        return new SerializedStream(tokens.ToArray(), targetStart);
    }

    private static SerializedStream SerializeToolResult(
        string input,
        string tool,
        IReadOnlyList<string> arguments,
        string result,
        string response)
    {
        var tokens = Start(input);
        tokens.Add(Tokenizer.Call);
        AddCallBody(tokens, tool, arguments);
        tokens.Add(Tokenizer.Result);
        tokens.AddRange(Tokenizer.Encode(result));
        tokens.Add(Tokenizer.Sep);
        var targetStart = tokens.Count;
        tokens.Add(Tokenizer.Text);
        tokens.AddRange(Tokenizer.Encode(response));
        tokens.Add(Tokenizer.Eos);
        return new SerializedStream(tokens.ToArray(), targetStart);
    }

    private static List<int> Start(string input)
    {
        var tokens = new List<int> { Tokenizer.Bos };
        tokens.AddRange(Tokenizer.Encode(input));
        tokens.Add(Tokenizer.Sep);
        return tokens;
    }

    private static void AddCallBody(List<int> tokens, string tool, IReadOnlyList<string> arguments)
    {
        tokens.AddRange(Tokenizer.Encode(tool));
        foreach (var argument in arguments)
        {
            tokens.Add(Tokenizer.Space);
            tokens.AddRange(Tokenizer.Encode(argument));
        }
    }

    private static void AddSamples(SerializedStream stream, List<TrainingSample> samples)
    {
        // Every sample predicts only authoritative output tokens. The prefix supplies the
        // same 32-token local context used at inference, while PositionOffset preserves
        // the token's real (cyclic) sequence position.
        for (var targetStart = stream.FirstTargetIndex;
             targetStart < stream.Tokens.Length;
             targetStart += TargetChunkLength)
        {
            var start = Math.Max(0, targetStart - ConditioningLength);
            var end = Math.Min(stream.Tokens.Length, targetStart + TargetChunkLength);
            var tokens = stream.Tokens[start..end];
            samples.Add(new TrainingSample(tokens, start, targetStart - start));
        }
    }

    private sealed record SerializedStream(int[] Tokens, int FirstTargetIndex);

    private sealed class TrainingRow
    {
        public string? Input { get; set; }
        public string? Response { get; set; }
        public string? Tool { get; set; }
        public string[]? Arguments { get; set; }
        public string? Result { get; set; }
    }
}

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

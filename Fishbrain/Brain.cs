using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fishbrain;

public sealed class BrainConfig
{
    public int EmbeddingSize { get; set; } = 64;
    public int HeadCount { get; set; } = 4;
    public int MlpSize { get; set; } = 128;
    public int ContextLength { get; set; } = 128;
    public int AttentionWindow { get; set; } = 128;
    public int PositionPeriod { get; set; } = 128;
    public int MaximumOutputLength { get; set; } = 64;
    public double LearningRate { get; set; } = 0.005;
    public double Beta1 { get; set; } = 0.85;
    public double Beta2 { get; set; } = 0.99;
    public double AdamEpsilon { get; set; } = 1e-8;
    public int Seed { get; set; } = 42;
    public int PlannedSteps { get; set; } = 40_000;

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

/// <summary>A deliberately tiny word-level GPT for uppercase video-game dialogue.</summary>
public sealed partial class Brain
{
    private const int CheckpointVersion = 10;
    private const int TeachingCheckpointInterval = 1_000;
    private const string SafeFallback = "I DO NOT KNOW.";

    private readonly DeterministicRandom _random;
    private readonly WordVocabulary _vocabulary;
    private readonly DialogueTokenizer _tokenizer;
    private readonly HashSet<string> _trainedTools;
    private readonly Dictionary<string, string> _trainedExamples;
    private readonly Dictionary<string, string[]> _responseCatalog;
    private readonly CompositionalHeadModel _structuredHeads;
    private readonly List<Value> _parameters = [];
    private readonly Value[][] _tokenEmbedding;
    private readonly Value[][] _outputHead;
    private readonly Value[][] _positionEmbedding;
    private readonly Value[][] _query;
    private readonly Value[][] _key;
    private readonly Value[][] _value;
    private readonly Value[][] _attentionOutput;
    private readonly Value[][] _mlpIn;
    private readonly Value[][] _mlpOut;
    private readonly Value[][] _intentHead;
    private readonly Value[][] _affectHead;
    private readonly Value[][] _expectedHead;
    private readonly double[] _weights;
    private readonly double[] _packedGradients;
    private double[] _adamM;
    private double[] _adamV;
    private bool _scalarWeightsCurrent = true;
    private int _step;
    private string _curriculumPhase = "UNSTARTED";
    private int _samplerPosition;
    private double _bestPerceptionScore = -1.0;
    private int _bestPerceptionStep;
    private double _bestRealizationLoss = double.MaxValue;
    private int _bestRealizationStep;
    private Dictionary<string, V10Schemas.ConfidenceThreshold> _confidenceCalibration;
    private string _corpusHash = "UNKNOWN";

    private Brain(
        BrainConfig config,
        WordVocabulary vocabulary,
        DeterministicRandom random,
        IEnumerable<string> trainedTools,
        IEnumerable<KeyValuePair<string, string>> trainedExamples,
        IEnumerable<KeyValuePair<string, string[]>> responseCatalog)
    {
        config.Validate();
        Config = config;
        _vocabulary = vocabulary;
        _tokenizer = new DialogueTokenizer(vocabulary);
        _random = random;
        _trainedTools = new HashSet<string>(trainedTools, StringComparer.Ordinal);
        _trainedExamples = new Dictionary<string, string>(trainedExamples, StringComparer.Ordinal);
        _responseCatalog = responseCatalog.ToDictionary(
            item => item.Key,
            item => item.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
        _structuredHeads = new CompositionalHeadModel(
            ["BUY", "LIST_WARES", "LOOKUP_LOCATION", "LOOKUP_PRICE", "SELL"],
            V10Candidates.Select(candidate => candidate.Id), config.Seed);
        _confidenceCalibration = V10Schemas.DefaultCalibration;

        _tokenEmbedding = CreateMatrix(_tokenizer.VocabularySize, config.EmbeddingSize);
        _outputHead = CreateMatrix(_tokenizer.OutputSize, config.EmbeddingSize);
        _positionEmbedding = CreateMatrix(config.PositionPeriod, config.EmbeddingSize);
        _query = CreateMatrix(config.EmbeddingSize, config.EmbeddingSize);
        _key = CreateMatrix(config.EmbeddingSize, config.EmbeddingSize);
        _value = CreateMatrix(config.EmbeddingSize, config.EmbeddingSize);
        _attentionOutput = CreateMatrix(config.EmbeddingSize, config.EmbeddingSize);
        _mlpIn = CreateMatrix(config.MlpSize, config.EmbeddingSize);
        _mlpOut = CreateMatrix(config.EmbeddingSize, config.MlpSize);
        _intentHead = CreateMatrix(Enum.GetValues<DialogueIntent>().Length, config.EmbeddingSize);
        _affectHead = CreateMatrix(Enum.GetValues<UserAffect>().Length, config.EmbeddingSize);
        _expectedHead = CreateMatrix(2, config.EmbeddingSize);

        AddParameters(_tokenEmbedding);
        AddParameters(_outputHead);
        AddParameters(_positionEmbedding);
        AddParameters(_query);
        AddParameters(_key);
        AddParameters(_value);
        AddParameters(_attentionOutput);
        AddParameters(_mlpIn);
        AddParameters(_mlpOut);
        AddParameters(_intentHead);
        AddParameters(_affectHead);
        AddParameters(_expectedHead);

        _weights = _parameters.Select(parameter => parameter.Data).ToArray();
        _packedGradients = new double[_parameters.Count];
        _adamM = new double[_parameters.Count];
        _adamV = new double[_parameters.Count];
        Tools = new ToolRegistry(_trainedTools);
    }

    public BrainConfig Config { get; }
    public ToolRegistry Tools { get; }
    public int CompletedSteps => _step;
    public IReadOnlyCollection<string> TrainedTools => _trainedTools;
    internal DialogueTokenizer DialogueTokenizer => _tokenizer;

    public static Brain Load(string path)
    {
        if (IsInferenceCheckpoint(path)) return LoadInferenceCheckpoint(path);
        var checkpoint = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(path), JsonOptions())
            ?? throw new InvalidDataException("Checkpoint is empty.");
        if (checkpoint.Version is >= 2 and <= 9)
            throw new InvalidDataException("Fishbrain v10 uses OOV character tokens and compositional schemas; retain the older checkpoint as an archive.");
        if (checkpoint.Version != CheckpointVersion)
            throw new InvalidDataException($"Unsupported checkpoint version {checkpoint.Version}.");
        var expectedIntegrity = checkpoint.IntegrityChecksum;
        checkpoint.IntegrityChecksum = "";
        if (expectedIntegrity.Length != 64 ||
            !ComputeCheckpointIntegrity(checkpoint).Equals(expectedIntegrity, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Training checkpoint integrity checksum failed.");
        checkpoint.IntegrityChecksum = expectedIntegrity;
        checkpoint.Config.Validate();
        if (checkpoint.Words.Length == 0 || checkpoint.OutputWords.Length == 0)
            throw new InvalidDataException("The v10 checkpoint does not contain a word vocabulary.");
        var vocabulary = new WordVocabulary(checkpoint.Words, checkpoint.OutputWords);

        var brain = new Brain(
            checkpoint.Config,
            vocabulary,
            new DeterministicRandom(checkpoint.Config.Seed),
            checkpoint.TrainedTools ?? [],
            checkpoint.TrainedExamples ?? new Dictionary<string, string>(),
            checkpoint.ResponseCatalog ?? new Dictionary<string, string[]>());

        if (checkpoint.Weights.Length != brain._parameters.Count ||
            checkpoint.AdamM.Length != brain._parameters.Count ||
            checkpoint.AdamV.Length != brain._parameters.Count)
        {
            throw new InvalidDataException("Checkpoint parameter counts do not match its configuration.");
        }

        Array.Copy(checkpoint.Weights, brain._weights, checkpoint.Weights.Length);
        brain._scalarWeightsCurrent = false;
        brain.SyncScalarWeights();
        brain._adamM = checkpoint.AdamM;
        brain._adamV = checkpoint.AdamV;
        brain._step = checkpoint.CompletedSteps;
        brain._random.State = checkpoint.RandomState;
        brain._curriculumPhase = checkpoint.CurriculumPhase;
        brain._samplerPosition = checkpoint.SamplerPosition;
        brain._bestPerceptionScore = checkpoint.BestPerceptionScore;
        brain._bestPerceptionStep = checkpoint.BestPerceptionStep;
        brain._bestRealizationLoss = checkpoint.BestRealizationLoss;
        brain._bestRealizationStep = checkpoint.BestRealizationStep;
        V10Schemas.Validate(V10Schemas.Labels,
            checkpoint.ConfidenceCalibration ?? V10Schemas.DefaultCalibration);
        brain._confidenceCalibration = checkpoint.ConfidenceCalibration ?? V10Schemas.DefaultCalibration;
        if (checkpoint.LabelSchemas is { Count: > 0 })
            V10Schemas.Validate(checkpoint.LabelSchemas, brain._confidenceCalibration);
        if (checkpoint.CandidateCatalog is { Length: > 0 } &&
            !checkpoint.CandidateCatalog.Select(candidate => candidate.Id)
                .SequenceEqual(V10Candidates.OrderBy(candidate => candidate.Id).Select(candidate => candidate.Id)))
            throw new InvalidDataException("Training checkpoint response candidate schema does not match this runtime.");
        if (checkpoint.ToolSchemas is { Length: > 0 } &&
            !checkpoint.ToolSchemas.Select(schema => schema.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(DemoGameTools.CreateMerchant().Schemas.Select(schema => schema.Name).Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Training checkpoint tool schemas do not match this runtime.");
        brain._corpusHash = checkpoint.CorpusHash ?? "UNKNOWN";
        brain._structuredHeads.Restore(checkpoint.StructuredWeights, checkpoint.StructuredUpdates);
        return brain;
    }

    internal LegacyReplyResult Reply(string recentDialogue, NpcState state, double temperature = 0.2) =>
        ReplyCore(recentDialogue, state, temperature, useExactMemory: true, seedOverride: null);

    internal LegacyReplyResult DebugReplyWithoutMemory(string recentDialogue, NpcState state, double temperature = 0.2) =>
        ReplyCore(recentDialogue, state, temperature, useExactMemory: false, seedOverride: null);

    private LegacyReplyResult GeneratedReply(
        string recentDialogue, string currentTurn, NpcState state, int seed, double temperature = 0.2) =>
        ReplyCore(recentDialogue, state, temperature, useExactMemory: false, seed, currentTurn);

    private LegacyReplyResult ReplyCore(
        string recentDialogue, NpcState state, double temperature, bool useExactMemory, int? seedOverride,
        string? structuredCurrentTurn = null)
    {
        SyncScalarWeights();
        if (temperature <= 0) throw new ArgumentOutOfRangeException(nameof(temperature));
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();
        var input = Tokenizer.Normalize(recentDialogue);
        if (input.Length == 0) throw new ArgumentException("Dialogue cannot be empty.", nameof(recentDialogue));

        var currentTurn = structuredCurrentTurn is null
            ? ExtractCurrentPlayerTurn(input)
            : Tokenizer.Normalize(structuredCurrentTurn);
        var perception = Cognition.Constrain(
            structuredCurrentTurn is null ? PredictPerception(input) : PredictCurrentTurn(currentTurn), currentTurn);
        var expectedAction = Cognition.ActionFor(perception);
        var decision = new TurnDecision(expectedAction);

        var decisionPrompt = StartPrompt(input);
        AppendState(decisionPrompt, state);
        decisionPrompt.Add(Tokenizer.Decide);
        decisionPrompt.Add(Tokenizer.Intent(perception.Intent));
        decisionPrompt.Add(Tokenizer.Affect(perception.Affect));
        decisionPrompt.Add(perception.ResponseExpected ? Tokenizer.ExpectedTrue : Tokenizer.ExpectedFalse);
        decisionPrompt.Add(Tokenizer.Action(expectedAction));

        var toolSucceeded = false;
        var toolResult = string.Empty;
        int[] callBody = [];
        if (decision.Action == ResponseAction.CallTool)
        {
            var callContext = new List<int>(decisionPrompt) { Tokenizer.Call };
            if (!TryGenerateToolCall(callContext, out var toolName, out var arguments, out callBody) ||
                !Tools.TryInvoke(toolName, arguments, out toolResult))
            {
                var failed = Cognition.Apply(state, perception, decision);
                return new LegacyReplyResult(SafeFallback, failed.State, perception, decision, failed.Tone);
            }
            toolSucceeded = true;
        }

        var transition = Cognition.Apply(state, perception, decision, toolSucceeded);
        if (decision.Action == ResponseAction.NoResponse)
            return new LegacyReplyResult(string.Empty, transition.State, perception, decision, transition.Tone);

        var memoryKey = DialogueKeys.Example(input, state, perception, decision, transition.Tone);
        if (useExactMemory && decision.Action != ResponseAction.CallTool &&
            _trainedExamples.TryGetValue(memoryKey, out var trainedResponse))
        {
            return new LegacyReplyResult(trainedResponse, transition.State, perception, decision, transition.Tone);
        }

        var responsePrompt = StartPrompt(input);
        AppendState(responsePrompt, transition.State);
        responsePrompt.Add(Tokenizer.Decide);
        responsePrompt.Add(Tokenizer.Intent(perception.Intent));
        responsePrompt.Add(Tokenizer.Affect(perception.Affect));
        responsePrompt.Add(perception.ResponseExpected ? Tokenizer.ExpectedTrue : Tokenizer.ExpectedFalse);
        responsePrompt.Add(Tokenizer.Action(decision.Action));
        responsePrompt.Add(Tokenizer.Tone(transition.Tone));
        if (toolSucceeded)
        {
            responsePrompt.Add(Tokenizer.Call);
            responsePrompt.AddRange(callBody);
            responsePrompt.Add(Tokenizer.Result);
            responsePrompt.AddRange(_tokenizer.Encode(toolResult));
        }
        responsePrompt.Add(Tokenizer.Text);

        var text = GenerateText(responsePrompt, temperature, ReplyRandom(input, state, seedOverride));
        if (decision.Action != ResponseAction.CallTool)
            text = SelectSafeResponse(text, input, perception.Intent, decision.Action, transition.Tone);
        return new LegacyReplyResult(text, transition.State, perception, decision, transition.Tone);
    }

    internal static void TrainNew(string dataPath, string checkpointPath, int plannedSteps)
    {
        if (File.Exists(checkpointPath))
            throw new IOException($"Checkpoint '{checkpointPath}' already exists. Use resume or choose another path.");

        var vocabulary = WordVocabulary.Build(dataPath);
        var tokenizer = new DialogueTokenizer(vocabulary);
        var data = TrainingData.Load(dataPath, tokenizer);
        var config = new BrainConfig { PlannedSteps = plannedSteps };
        var brain = new Brain(
            config,
            vocabulary,
            new DeterministicRandom(config.Seed),
            data.ToolNames,
            data.Examples,
            data.ResponseCatalog);
        brain._corpusHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(dataPath))).ToLowerInvariant();
        brain.Train(data.Samples, checkpointPath, plannedSteps);
    }

    internal static void Teach(
        string corpusDirectory,
        string checkpointPath,
        int? requestedPlannedSteps,
        int? requestedUntilStep,
        string projectPath)
    {
        var fullCorpusDirectory = Path.GetFullPath(corpusDirectory);
        var fullCheckpointPath = Path.GetFullPath(checkpointPath);
        var trainPath = Path.Combine(fullCorpusDirectory, "train.jsonl");
        Brain brain;
        int plannedSteps;
        if (File.Exists(fullCheckpointPath))
        {
            brain = Load(fullCheckpointPath);
            plannedSteps = brain.Config.PlannedSteps;
            if (requestedPlannedSteps is not null && requestedPlannedSteps != plannedSteps)
                throw new InvalidOperationException(
                    $"The checkpoint uses a {plannedSteps}-step curriculum; --planned cannot change it to {requestedPlannedSteps}.");
        }
        else
        {
            plannedSteps = requestedPlannedSteps ?? 40_000;
            var config = new BrainConfig { PlannedSteps = plannedSteps };
            var vocabulary = WordVocabulary.Build(trainPath);
            var tokenizer = new DialogueTokenizer(vocabulary);
            var initialData = TrainingData.Load(trainPath, tokenizer);
            brain = new Brain(config, vocabulary, new DeterministicRandom(config.Seed), initialData.ToolNames,
                initialData.Examples, initialData.ResponseCatalog);
        }
        var data = TrainingData.Load(trainPath, brain._tokenizer);
        var validation = TrainingData.Load(Path.Combine(fullCorpusDirectory, "validation.jsonl"), brain._tokenizer);
        if (data.LanguageSamples.Count == 0 || data.PerceptionSamples.Count == 0)
            throw new InvalidDataException("Teaching requires both language and perception samples.");
        if (!brain._trainedTools.SetEquals(data.ToolNames)) throw new InvalidDataException("Teaching tools differ from the checkpoint.");
        if (brain._trainedExamples.Count != data.Examples.Count ||
            brain._trainedExamples.Any(example => !data.Examples.TryGetValue(example.Key, out var value) || value != example.Value))
            throw new InvalidDataException("Teaching examples differ from the checkpoint.");
        if (!CatalogEquals(brain._responseCatalog, data.ResponseCatalog))
            throw new InvalidDataException("Teaching response catalog differs from the checkpoint.");
        var untilStep = requestedUntilStep ?? plannedSteps;
        if (untilStep <= brain._step || untilStep > plannedSteps)
            throw new ArgumentOutOfRangeException(nameof(requestedUntilStep),
                $"--until must be greater than completed step {brain._step} and no greater than planned step {plannedSteps}.");
        brain.Config.PlannedSteps = plannedSteps;
        var recovery = new TeachingRecovery(
            Path.GetFullPath(projectPath), fullCorpusDirectory, fullCheckpointPath, plannedSteps, untilStep);
        var corpusHash = ComputeCorpusHash(fullCorpusDirectory);
        if (brain._corpusHash != "UNKNOWN" && brain._corpusHash != corpusHash)
            throw new InvalidDataException("Teaching corpus hash differs from the checkpoint.");
        brain._corpusHash = corpusHash;
        brain.TrainCurriculum(data, validation, fullCheckpointPath, plannedSteps, untilStep, recovery, corpusHash);
    }

    internal static void Resume(string dataPath, string checkpointPath, int? targetSteps)
    {
        var brain = Load(checkpointPath);
        var data = TrainingData.Load(dataPath, brain._tokenizer);
        if (!brain._trainedTools.SetEquals(data.ToolNames))
            throw new InvalidDataException("Training data tools differ from the checkpoint's trained tool set.");
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
        if (!CatalogEquals(brain._responseCatalog, data.ResponseCatalog))
            throw new InvalidDataException("Training response catalog differs from the checkpoint.");

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
            Words = _vocabulary.Words,
            OutputWords = _vocabulary.OutputWords,
            TrainedTools = _trainedTools.Order(StringComparer.Ordinal).ToArray(),
            TrainedExamples = _trainedExamples
                .OrderBy(example => example.Key, StringComparer.Ordinal)
                .ToDictionary(example => example.Key, example => example.Value, StringComparer.Ordinal),
            ResponseCatalog = _responseCatalog
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            Weights = (double[])_weights.Clone(),
            AdamM = _adamM,
            AdamV = _adamV,
            CompletedSteps = _step,
            RandomState = _random.State,
            CurriculumPhase = _curriculumPhase,
            SamplerPosition = _samplerPosition,
            BestPerceptionScore = _bestPerceptionScore,
            BestPerceptionStep = _bestPerceptionStep,
            BestRealizationLoss = _bestRealizationLoss,
            BestRealizationStep = _bestRealizationStep,
            StructuredWeights = _structuredHeads.Snapshot(),
            StructuredUpdates = _structuredHeads.Updates,
            ConfidenceCalibration = _confidenceCalibration,
            LabelSchemas = V10Schemas.Labels,
            ToolSchemas = DemoGameTools.CreateMerchant().Schemas.OrderBy(schema => schema.Name).ToArray(),
            CandidateCatalog = V10Candidates.OrderBy(candidate => candidate.Id).ToArray(),
            CorpusHash = _corpusHash
        };
        checkpoint.IntegrityChecksum = ComputeCheckpointIntegrity(checkpoint);

        var temporaryPath = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(checkpoint, JsonOptions()));
        File.Move(temporaryPath, path, true);
    }

    private void SyncScalarWeights()
    {
        if (_scalarWeightsCurrent) return;
        for (var index = 0; index < _parameters.Count; index++)
            _parameters[index].Data = _weights[index];
        _scalarWeightsCurrent = true;
    }

    internal static Brain CreateForTesting(BrainConfig config, params string[] trainedTools) =>
        new(config, WordVocabulary.Testing(), new DeterministicRandom(config.Seed), trainedTools, [], []);

    internal static Brain CreateForTesting(BrainConfig config, WordVocabulary vocabulary, params string[] trainedTools) =>
        new(config, vocabulary, new DeterministicRandom(config.Seed), trainedTools, [], []);

    internal static Brain CreateForTestingWithExamples(
        BrainConfig config,
        IReadOnlyDictionary<string, string> examples) =>
        new(config, WordVocabulary.Testing(), new DeterministicRandom(config.Seed), [], examples, []);

    internal double[] DebugNextLogits(IReadOnlyList<int> tokens)
    {
        SyncScalarWeights();
        return NextLogits(tokens);
    }
    internal double[][] DebugSequenceLogits(IReadOnlyList<int> tokens)
    {
        SyncScalarWeights();
        using var _ = Value.NoGrad();
        return Forward(tokens, 0).Select(row => row.Select(value => value.Data).ToArray()).ToArray();
    }
    internal double[] DebugWeights() => (double[])_weights.Clone();
    internal string DebugCorpusHash => _corpusHash;

    internal TurnPerception DebugPredictPerception(string dialogue, NpcState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();
        var input = Tokenizer.Normalize(dialogue);
        if (input.Length == 0) throw new ArgumentException("Dialogue cannot be empty.", nameof(dialogue));
        return Cognition.Constrain(PredictPerception(input), ExtractCurrentPlayerTurn(input));
    }

    internal TurnPerception DebugPredictRawPerception(string dialogue, NpcState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();
        var input = Tokenizer.Normalize(dialogue);
        if (input.Length == 0) throw new ArgumentException("Dialogue cannot be empty.", nameof(dialogue));
        return PredictPerception(input);
    }

    internal TurnPerception DebugPredictRawCurrentTurn(string turn, NpcState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();
        var input = Tokenizer.Normalize(turn);
        if (input.Length == 0) throw new ArgumentException("Turn cannot be empty.", nameof(turn));
        return PredictCurrentTurn(input);
    }

    internal StructuredPerception DebugPredictStructuredRaw(string input) =>
        _structuredHeads.Predict(DialogueText.Normalize(input), []);

    internal StructuredMetrics DebugEvaluateStructured(IReadOnlyList<V10TrainingExample> examples) =>
        _structuredHeads.Evaluate(examples);

    internal static string ExtractCurrentPlayerTurn(string normalizedDialogue)
    {
        if (string.IsNullOrWhiteSpace(normalizedDialogue))
            throw new ArgumentException("Dialogue cannot be empty.", nameof(normalizedDialogue));

        var npcMarker = FindLastRoleMarker(normalizedDialogue, "NPC");
        if (npcMarker >= 0)
        {
            var playerMarker = FindLastRoleMarker(normalizedDialogue, "PLAYER");
            if (playerMarker <= npcMarker)
                throw new ArgumentException("Role-marked history must end with a PLAYER utterance.", nameof(normalizedDialogue));
            return TextAfterRoleMarker(normalizedDialogue, playerMarker, "PLAYER");
        }

        return normalizedDialogue.StartsWith("PLAYER ", StringComparison.Ordinal) || normalizedDialogue == "PLAYER"
            ? TextAfterRoleMarker(normalizedDialogue, 0, "PLAYER")
            : normalizedDialogue;
    }

    private static int FindRoleMarker(string dialogue, string role, int startIndex)
    {
        var marker = " " + role;
        for (var index = dialogue.IndexOf(marker, startIndex, StringComparison.Ordinal);
             index >= 0;
             index = dialogue.IndexOf(marker, index + marker.Length, StringComparison.Ordinal))
        {
            var before = index > 0 ? dialogue[index - 1] : '\0';
            var after = index + marker.Length;
            if ((before is '.' or '?' or '!') && (after == dialogue.Length || dialogue[after] == ' '))
                return index + 1;
        }
        return -1;
    }

    private static int FindLastRoleMarker(string dialogue, string role)
    {
        var last = -1;
        var search = 0;
        while (search < dialogue.Length)
        {
            var found = FindRoleMarker(dialogue, role, search);
            if (found < 0) return last;
            last = found;
            search = found + role.Length;
        }
        return last;
    }

    private static string TextAfterRoleMarker(string dialogue, int markerIndex, string role)
    {
        var turn = dialogue[(markerIndex + role.Length)..].Trim();
        if (turn.Length == 0)
            throw new ArgumentException($"The final {role} marker must be followed by an utterance.", nameof(dialogue));
        return turn;
    }

    private TurnPerception PredictPerception(string normalizedDialogue)
    {
        return PredictCurrentTurn(ExtractCurrentPlayerTurn(normalizedDialogue));
    }

    private TurnPerception PredictCurrentTurn(string normalizedTurn)
    {
        SyncScalarWeights();
        var encoded = _tokenizer.Encode(normalizedTurn);
        if (encoded.Length > Config.ContextLength - 2) encoded = encoded[^(Config.ContextLength - 2)..];
        var tokens = new List<int>(encoded.Length + 2) { Tokenizer.Bos };
        tokens.AddRange(encoded);
        tokens.Add(Tokenizer.Sep);
        using var _ = Value.NoGrad();
        var representation = ForwardLastHidden(tokens, 0);
        var intent = (DialogueIntent)ArgMax(Linear(representation, _intentHead));
        var affect = (UserAffect)ArgMax(Linear(representation, _affectHead));
        var expected = ArgMax(Linear(representation, _expectedHead)) == 1;
        return new TurnPerception(intent, affect, expected);
    }

    internal double DebugAverageLoss(IEnumerable<TrainingSample> samples)
    {
        var total = 0.0;
        var count = 0;
        foreach (var sample in samples)
        {
            total += CalculateLoss(sample);
            count++;
        }
        foreach (var parameter in _parameters) parameter.Grad = 0.0;
        return count == 0 ? double.NaN : total / count;
    }

    internal double[] DebugLogitsAt(IReadOnlyList<int> tokens, int position)
    {
        SyncScalarWeights();
        using var _ = Value.NoGrad();
        return Forward(tokens, 0)[position].Select(x => x.Data).ToArray();
    }

    internal double DebugTrainWindow(IReadOnlyList<int> window, int targetSteps)
    {
        var loss = CalculateLoss(new TrainingSample([.. window], 0, 1));
        ApplyGradients(targetSteps);
        return loss;
    }

    internal double DebugTrainSample(IReadOnlyList<int> tokens, int firstTargetIndex, int targetSteps)
    {
        var loss = CalculateLoss(new TrainingSample([.. tokens], 0, firstTargetIndex));
        ApplyGradients(targetSteps);
        return loss;
    }

    internal double DebugTrainSampleReference(IReadOnlyList<int> tokens, int firstTargetIndex, int targetSteps)
    {
        var loss = CalculateLoss(new TrainingSample([.. tokens], 0, firstTargetIndex), optimizedForward: false);
        ApplyGradients(targetSteps);
        return loss;
    }

    internal (double Loss, double[] Gradients) DebugLossAndGradients(
        IReadOnlyList<int> tokens, int firstTargetIndex, bool optimizedForward)
    {
        var loss = CalculateLoss(
            new TrainingSample([.. tokens], 0, firstTargetIndex), optimizedForward);
        return (loss, (double[])_packedGradients.Clone());
    }

    internal double DebugFiniteDifferenceGradient(IReadOnlyList<int> tokens, int firstTargetIndex, int parameterIndex)
        => DebugFiniteDifferenceGradient(new TrainingSample([.. tokens], 0, firstTargetIndex), parameterIndex);

    internal (double Loss, double[] Gradients) DebugLossAndGradients(TrainingSample sample)
    {
        var loss = PackedTrainer.Calculate(Config, _tokenizer, _weights, _packedGradients, sample);
        return (loss, (double[])_packedGradients.Clone());
    }

    internal double DebugFiniteDifferenceGradient(TrainingSample sample, int parameterIndex)
    {
        const double epsilon = 1e-6;
        var original = _weights[parameterIndex];
        try
        {
            _weights[parameterIndex] = original + epsilon;
            var plus = PackedTrainer.Calculate(Config, _tokenizer, _weights, _packedGradients, sample);
            _weights[parameterIndex] = original - epsilon;
            var minus = PackedTrainer.Calculate(Config, _tokenizer, _weights, _packedGradients, sample);
            return (plus - minus) / (2 * epsilon);
        }
        finally
        {
            _weights[parameterIndex] = original;
        }
    }

    internal double[][] DebugTargetLogits(
        IReadOnlyList<int> tokens, int firstLogitPosition, bool optimizedForward)
    {
        SyncScalarWeights();
        using var _ = Value.NoGrad();
        var logits = optimizedForward
            ? ForwardTargets(tokens, 0, firstLogitPosition)
            : Forward(tokens, 0)[firstLogitPosition..];
        return logits.Select(row => row.Select(value => value.Data).ToArray()).ToArray();
    }

    private List<int> StartPrompt(string input)
    {
        var tokens = new List<int> { Tokenizer.Bos };
        tokens.AddRange(_tokenizer.Encode(input));
        tokens.Add(Tokenizer.Sep);
        return tokens;
    }

    internal static void AppendState(List<int> tokens, NpcState state)
    {
        tokens.Add(Tokenizer.State);
        tokens.Add(Tokenizer.RapportStart + state.Rapport);
        tokens.Add(Tokenizer.Mood(state.Mood));
        tokens.Add(Tokenizer.Intent(state.LastIntent));
        tokens.Add(Tokenizer.Affect(state.LastAffect));
        tokens.Add(Tokenizer.Topic(state.ActiveTopic));
        tokens.Add(Tokenizer.Goal(state.ActiveGoal));
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

    private void TrainCurriculum(
        TrainingData data,
        TrainingData validation,
        string checkpointPath,
        int plannedSteps,
        int untilStep,
        TeachingRecovery? recovery,
        string? corpusHash = null)
    {
        if (data.StructuredSamples.Count > 0)
        {
            TrainV10Curriculum(data, validation, checkpointPath, plannedSteps, untilStep, recovery,
                corpusHash ?? "TEST");
            return;
        }
        Config.PlannedSteps = plannedSteps;
        var language = data.LanguageSamples.ToArray();
        var perceptionBuckets = new[]
        {
            PerceptionBuckets(data.PerceptionSamples.Where(sample => sample.TargetFields.HasFlag(PerceptionFields.Intent)),
                sample => ((int)sample.PerceptionTarget!.Intent).ToString(CultureInfo.InvariantCulture)),
            PerceptionBuckets(data.PerceptionSamples.Where(sample => sample.TargetFields.HasFlag(PerceptionFields.Affect)),
                sample => ((int)sample.PerceptionTarget!.Affect).ToString(CultureInfo.InvariantCulture)),
            PerceptionBuckets(data.PerceptionSamples.Where(sample => sample.TargetFields.HasFlag(PerceptionFields.Expected)),
                sample => sample.PerceptionTarget!.ResponseExpected ? "1" : "0")
        };
        var tools = data.ToolSamples.ToArray();
        var lastSavedStep = -1;
        while (_step < untilStep)
        {
            var languageEnd = Math.Max(1, plannedSteps / 20);
            TrainingSample sample;
            string phase;
            if (_step < languageEnd)
            {
                phase = "WARMUP";
                sample = language[DeterministicIndex(_step, language.Length, 101)];
            }
            else if ((_step - languageEnd) % 2 == 0)
            {
                phase = "INTERLEAVED";
                sample = BalancedPerception(perceptionBuckets, (_step - languageEnd) / 2);
            }
            else if (tools.Length > 0 && (_step - languageEnd) % 20 == 19)
            {
                phase = "INTERLEAVED";
                sample = tools[DeterministicIndex(_step, tools.Length, 307)];
            }
            else
            {
                phase = "INTERLEAVED";
                sample = language[DeterministicIndex(_step, language.Length, 503)];
            }

            var loss = CalculateLoss(sample);
            _curriculumPhase = phase;
            _samplerPosition = _step;
            ApplyGradients(plannedSteps);
            if (_step == 1 || _step % 10 == 0)
                Console.WriteLine($"STEP {_step,6} OF {plannedSteps,6} PHASE {phase,-10} LOSS {loss:F4}");
            if (_step % 1000 == 0)
            {
                var metrics = EvaluateValidation(validation);
                var bestPerception = metrics.IntentMacroF1 > _bestPerceptionScore;
                var bestRealization = metrics.RealizationLoss < _bestRealizationLoss;
                if (bestPerception)
                {
                    _bestPerceptionScore = metrics.IntentMacroF1;
                    _bestPerceptionStep = _step;
                }
                if (bestRealization)
                {
                    _bestRealizationLoss = metrics.RealizationLoss;
                    _bestRealizationStep = _step;
                }
                Console.WriteLine(
                    $"VALIDATION STEP {_step,6} INTENT_MACRO_F1 {metrics.IntentMacroF1:F4} " +
                    $"AFFECT_MACRO_F1 {metrics.AffectMacroF1:F4} EXPECTED_F1 {metrics.ExpectedF1:F4} " +
                    $"DIRECT_INTENT_MACRO_F1 {metrics.DirectIntentMacroF1:F4} " +
                    $"HISTORY_INTENT_MACRO_F1 {metrics.HistoryIntentMacroF1:F4} " +
                    $"REALIZATION_LOSS {metrics.RealizationLoss:F4}");
                Console.WriteLine(
                    $"BEST PERCEPTION {_bestPerceptionScore:F4} AT {_bestPerceptionStep} " +
                    $"REALIZATION {_bestRealizationLoss:F4} AT {_bestRealizationStep}");
                if (bestPerception) Save(CheckpointRolePath(checkpointPath, "best-perception"));
                if (bestRealization) Save(CheckpointRolePath(checkpointPath, "best-realization"));
            }
            if (_step % TeachingCheckpointInterval == 0)
            {
                SaveTeachingCheckpoint(checkpointPath, recovery);
                lastSavedStep = _step;
            }
        }
        if (lastSavedStep != _step) SaveTeachingCheckpoint(checkpointPath, recovery);
        if (recovery is not null) PrintMilestoneCommands(recovery);
    }

    private void SaveTeachingCheckpoint(string checkpointPath, TeachingRecovery? recovery)
    {
        Save(checkpointPath);
        if (recovery is null) return;
        Console.WriteLine($"CHECKPOINT SAVED STEP {_step}: {recovery.CheckpointPath}");
        if (_step < recovery.UntilStep)
        {
            Console.WriteLine("RESUME IF INTERRUPTED:");
            Console.WriteLine(recovery.TeachCommand(recovery.UntilStep));
        }
        Console.Out.Flush();
    }

    private static void PrintMilestoneCommands(TeachingRecovery recovery)
    {
        Console.WriteLine("EVALUATE THIS MILESTONE:");
        Console.WriteLine(recovery.EvaluateCommand());
        if (recovery.UntilStep < recovery.PlannedSteps)
        {
            Console.WriteLine("CONTINUE TO THE FULL CURRICULUM:");
            Console.WriteLine(recovery.TeachCommand(recovery.PlannedSteps));
        }
        Console.Out.Flush();
    }

    internal void DebugTrainCurriculum(
        TrainingData data, string checkpointPath, int plannedSteps, int untilStep) =>
        TrainCurriculum(data, data, checkpointPath, plannedSteps, untilStep, recovery: null);

    private void TrainV10Curriculum(
        TrainingData data,
        TrainingData validation,
        string checkpointPath,
        int plannedSteps,
        int untilStep,
        TeachingRecovery? recovery,
        string corpusHash)
    {
        Config.PlannedSteps = plannedSteps;
        var language = data.LanguageSamples.ToArray();
        if (language.Length == 0) throw new InvalidDataException("V10 teaching requires language samples.");
        var familiesBySource = data.StructuredSamples
            .GroupBy(example => $"{example.Source}|{string.Join(',', example.SpeechActs)}|" +
                                $"{string.Join(',', example.Domains)}|{string.Join(',', example.Goals)}|" +
                                $"{example.Affect}|{example.Policy}|{example.ToolSchema}|{example.ResponseCandidateId}",
                StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(source => source.GroupBy(example => example.SemanticFamilyId, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.OrderBy(example => example.Input, StringComparer.Ordinal).ToArray())
                .ToArray())
            .ToArray();
        var families = Enumerable.Range(0, familiesBySource.Max(source => source.Length))
            .SelectMany(index => familiesBySource.Where(source => index < source.Length).Select(source => source[index]))
            .ToArray();
        if (families.Length == 0) throw new InvalidDataException("V10 teaching requires structured samples.");
        var slotFamiliesBySource = data.StructuredSamples
            .Where(example => example.SupervisedHeads.Contains("slots"))
            .GroupBy(example => example.Source, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(source => source.GroupBy(example => example.SemanticFamilyId, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.OrderBy(example => example.Input, StringComparer.Ordinal).ToArray())
                .ToArray())
            .ToArray();
        if (slotFamiliesBySource.Length == 0)
            throw new InvalidDataException("V10 teaching requires slot-supervised samples.");
        var slotFamilies = Enumerable.Range(0, slotFamiliesBySource.Max(source => source.Length))
            .SelectMany(index => slotFamiliesBySource.Where(source => index < source.Length).Select(source => source[index]))
            .ToArray();

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var intervalStart = timer.Elapsed;
        var intervalStep = _step;
        var lastSavedStep = -1;
        while (_step < untilStep)
        {
            double loss;
            string phase;
            if (_step % 10 != 9)
            {
                phase = "STRUCTURED";
                var structuredStep = _structuredHeads.Updates;
                var family = families[structuredStep % families.Length];
                var example = family[DeterministicIndex(structuredStep / families.Length,
                    family.Length, 1009)];
                var progress = (double)_step / Math.Max(1, plannedSteps - 1);
                var learningRate = 0.14 * (1.0 - 0.75 * progress);
                loss = _structuredHeads.Train(example, learningRate);
                var slotFamily = slotFamilies[structuredStep % slotFamilies.Length];
                var slotExample = slotFamily[DeterministicIndex(structuredStep / slotFamilies.Length,
                    slotFamily.Length, 1877)];
                loss = (loss + _structuredHeads.TrainSlotsOnly(slotExample, learningRate)) * 0.5;
                _step++;
            }
            else
            {
                phase = "GENERATION";
                var generationStep = _step / 10;
                var sample = language[DeterministicIndex(generationStep, language.Length, 2027)];
                loss = CalculateLoss(sample);
                ApplyGradients(plannedSteps);
            }
            _curriculumPhase = phase;
            _samplerPosition = _step;
            if (_step == 1 || _step % 100 == 0)
                Console.WriteLine($"STEP {_step,6} OF {plannedSteps,6} PHASE {phase,-10} LOSS {loss:F4} " +
                                  $"STRUCTURED {_structuredHeads.Updates,6}");

            if (_step % TeachingCheckpointInterval != 0) continue;
            var metrics = _structuredHeads.Evaluate(validation.StructuredSamples);
            _confidenceCalibration = _structuredHeads.Calibrate(validation.StructuredSamples);
            var realizationLoss = DebugAverageLoss(validation.LanguageSamples.Take(64));
            var bestStructured = double.IsFinite(metrics.Composite) && metrics.Composite > _bestPerceptionScore;
            var bestGeneration = double.IsFinite(realizationLoss) && realizationLoss < _bestRealizationLoss;
            if (bestStructured)
            {
                _bestPerceptionScore = metrics.Composite;
                _bestPerceptionStep = _step;
            }
            if (bestGeneration)
            {
                _bestRealizationLoss = realizationLoss;
                _bestRealizationStep = _step;
            }
            Console.WriteLine(
                $"VALIDATION STEP {_step,6} SPEECH_F1 {metrics.SpeechActMacroF1:F4} " +
                $"DOMAIN_F1 {metrics.DomainMacroF1:F4} GOAL_F1 {metrics.GoalMacroF1:F4} " +
                $"AFFECT_ACC {metrics.AffectAccuracy:F4} POLICY_ACC {metrics.PolicyAccuracy:F4} " +
                $"CONTENT_F1 {metrics.ContentMacroF1:F4} SLOT_F1 {metrics.SlotSpanF1:F4} " +
                $"TOOL_ACC {metrics.ToolAccuracy:F4} MUTATING_PRECISION {metrics.MutatingToolPrecision:F4} " +
                $"RESPONSE_TOP1 {metrics.ResponseTop1:F4} RESPONSE_TOP3 {metrics.ResponseTop3:F4} " +
                $"COMPOSITE {metrics.Composite:F4} GENERATION_LOSS {realizationLoss:F4}");
            Console.WriteLine($"BEST STRUCTURED {_bestPerceptionScore:F4} AT {_bestPerceptionStep} " +
                              $"GENERATION {_bestRealizationLoss:F4} AT {_bestRealizationStep}");

            Save(checkpointPath);
            if (bestStructured) Save(CheckpointRolePath(checkpointPath, "best-structured"));
            if (bestGeneration) Save(CheckpointRolePath(checkpointPath, "best-generation"));
            var now = timer.Elapsed;
            WriteTrainingTelemetry(checkpointPath, corpusHash, validation.StructuredSamples, metrics, realizationLoss,
                _step - intervalStep, now - intervalStart, _step is 10_000 or 20_000 or 30_000 or 40_000);
            intervalStep = _step;
            intervalStart = now;
            lastSavedStep = _step;
            if (recovery is not null)
            {
                Console.WriteLine($"CHECKPOINT SAVED STEP {_step}: {recovery.CheckpointPath}");
                Console.Out.Flush();
            }
        }

        if (lastSavedStep != _step) SaveTeachingCheckpoint(checkpointPath, recovery);
        if (_step == plannedSteps && recovery is not null)
        {
            var bestPath = CheckpointRolePath(checkpointPath, "best-structured");
            if (!File.Exists(bestPath)) throw new InvalidDataException("Training completed without a best structured checkpoint.");
            var output = Path.Combine(Path.GetDirectoryName(recovery.CorpusDirectory)!, "models", "model-v10-latest.fbm");
            Load(bestPath).ExportInference(output, corpusHash);
            Console.WriteLine($"EXPORTED BEST STRUCTURED CHECKPOINT {output}");
        }
        if (recovery is not null) PrintMilestoneCommands(recovery);
    }

    private void WriteTrainingTelemetry(
        string checkpointPath, string corpusHash, IReadOnlyList<V10TrainingExample> validation,
        StructuredMetrics metrics, double generationLoss,
        int intervalSteps, TimeSpan elapsed, bool fullStage)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var path = Path.Combine(root, "data", "telemetry", "training-v10.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var checkpointHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(checkpointPath))).ToLowerInvariant();
        var responseSources = new Dictionary<ResponseSource, int>();
        var tools = DemoGameTools.CreateMerchant();
        foreach (var example in validation
                     .OrderBy(item => item.SemanticFamilyId, StringComparer.Ordinal).Take(32))
        {
            var result = Reply(new ReplyRequest("TRAINING-TELEMETRY", $"STEP-{_step}-{responseSources.Values.Sum()}",
                [new DialogueTurn(DialogueRole.Player, example.Input)], NpcDialogueState.Initial,
                Config.Seed), tools);
            responseSources[result.Diagnostics.ResponseSource] =
                responseSources.GetValueOrDefault(result.Diagnostics.ResponseSource) + 1;
        }
        var payload = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            milestone = fullStage ? "V10_STAGE" : "V10_CHECKPOINT",
            step = _step,
            corpusHash,
            checkpointHash,
            environment = $"{Environment.OSVersion}; {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; .NET {Environment.Version}",
            vectorWidth = Vector<double>.Count,
            embeddingSize = Config.EmbeddingSize,
            throughputStepsPerSecond = intervalSteps / Math.Max(0.001, elapsed.TotalSeconds),
            losses = new { generation = generationLoss },
            rawMetrics = metrics,
            constrainedMetrics = new { hardStructuralInvariants = true },
            responseSources = responseSources.ToDictionary(item => item.Key.ToString(), item => item.Value),
            checkpointRole = "training-resume"
        };
        File.AppendAllText(path, JsonSerializer.Serialize(payload) + Environment.NewLine, Encoding.UTF8);
        Console.WriteLine($"TELEMETRY {path}");
    }

    private static string ComputeCorpusHash(string directory)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var name in new[] { "train.jsonl", "validation.jsonl", "test.jsonl" })
            hash.AppendData(File.ReadAllBytes(Path.Combine(directory, name)));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private ValidationMetrics EvaluateValidation(TrainingData validation)
    {
        SyncScalarWeights();
        var intentSamples = validation.PerceptionSamples
            .Where(sample => sample.TargetFields.HasFlag(PerceptionFields.Intent)).Take(512).ToArray();
        var affectSamples = validation.PerceptionSamples
            .Where(sample => sample.TargetFields.HasFlag(PerceptionFields.Affect)).Take(512).ToArray();
        var expectedSamples = validation.PerceptionSamples
            .Where(sample => sample.TargetFields.HasFlag(PerceptionFields.Expected)).Take(512).ToArray();
        var intentPredicted = intentSamples.Select(PredictPerceptionSample).ToArray();
        var affectPredicted = affectSamples.Select(PredictPerceptionSample).ToArray();
        var expectedPredicted = expectedSamples.Select(PredictPerceptionSample).ToArray();
        var realizationLoss = DebugAverageLoss(validation.LanguageSamples.Take(64));
        return new ValidationMetrics(
            MacroF1(intentSamples.Select(sample => sample.PerceptionTarget!.Intent), intentPredicted.Select(value => value.Intent)),
            MacroF1(affectSamples.Select(sample => sample.PerceptionTarget!.Affect), affectPredicted.Select(value => value.Affect)),
            BinaryF1(expectedSamples.Select(sample => sample.PerceptionTarget!.ResponseExpected), expectedPredicted.Select(value => value.ResponseExpected)),
            SubsetIntentMacroF1(intentSamples, intentPredicted, sample => sample.Family.EndsWith("_DIRECT", StringComparison.Ordinal)),
            SubsetIntentMacroF1(intentSamples, intentPredicted, sample => sample.Family.EndsWith("_HISTORY", StringComparison.Ordinal)),
            realizationLoss);
    }

    private TurnPerception PredictPerceptionSample(TrainingSample sample)
    {
        using var _ = Value.NoGrad();
        var representation = ForwardLastHidden(sample.Tokens, sample.PositionOffset);
        return new TurnPerception(
            (DialogueIntent)ArgMax(Linear(representation, _intentHead)),
            (UserAffect)ArgMax(Linear(representation, _affectHead)),
            ArgMax(Linear(representation, _expectedHead)) == 1);
    }

    private static double SubsetIntentMacroF1(
        IReadOnlyList<TrainingSample> samples,
        IReadOnlyList<TurnPerception> predicted,
        Func<TrainingSample, bool> include)
    {
        var indices = Enumerable.Range(0, samples.Count).Where(index => include(samples[index])).ToArray();
        return indices.Length == 0
            ? double.NaN
            : MacroF1(indices.Select(index => samples[index].PerceptionTarget!.Intent),
                indices.Select(index => predicted[index].Intent));
    }

    private static double MacroF1<T>(IEnumerable<T> expectedValues, IEnumerable<T> predictedValues) where T : struct, Enum
    {
        var pairs = expectedValues.Zip(predictedValues).ToArray();
        if (pairs.Length == 0) return double.NaN;
        return pairs.Select(pair => pair.First).Distinct().Select(label =>
        {
            var tp = pairs.Count(pair => pair.First.Equals(label) && pair.Second.Equals(label));
            var fp = pairs.Count(pair => !pair.First.Equals(label) && pair.Second.Equals(label));
            var fn = pairs.Count(pair => pair.First.Equals(label) && !pair.Second.Equals(label));
            return 2.0 * tp / Math.Max(1, 2 * tp + fp + fn);
        }).Average();
    }

    private static double BinaryF1(IEnumerable<bool> expectedValues, IEnumerable<bool> predictedValues)
    {
        var pairs = expectedValues.Zip(predictedValues).ToArray();
        var tp = pairs.Count(pair => pair.First && pair.Second);
        var fp = pairs.Count(pair => !pair.First && pair.Second);
        var fn = pairs.Count(pair => pair.First && !pair.Second);
        return 2.0 * tp / Math.Max(1, 2 * tp + fp + fn);
    }

    private static string CheckpointRolePath(string checkpointPath, string role)
    {
        var directory = Path.GetDirectoryName(checkpointPath) ?? "";
        var extension = Path.GetExtension(checkpointPath);
        var stem = Path.GetFileNameWithoutExtension(checkpointPath);
        if (stem.EndsWith("-latest", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^"-latest".Length];
        return Path.Combine(directory, $"{stem}-{role}{extension}");
    }

    private sealed record ValidationMetrics(
        double IntentMacroF1,
        double AffectMacroF1,
        double ExpectedF1,
        double DirectIntentMacroF1,
        double HistoryIntentMacroF1,
        double RealizationLoss);

    private static TrainingSample[][] PerceptionBuckets(
        IEnumerable<TrainingSample> samples, Func<TrainingSample, string> key) =>
        samples.GroupBy(key, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.ToArray())
            .ToArray();

    private static TrainingSample BalancedPerception(TrainingSample[][][] dimensions, int index)
    {
        var buckets = dimensions[index % dimensions.Length];
        var dimensionIndex = index / dimensions.Length;
        var bucket = buckets[dimensionIndex % buckets.Length];
        return bucket[DeterministicIndex(dimensionIndex / buckets.Length, bucket.Length, 211)];
    }

    private static int DeterministicIndex(int step, int count, int salt)
    {
        var random = new DeterministicRandom(unchecked(step * 7919 + salt));
        return random.NextInt(count);
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
        for (var i = 0; i < inputs.Length; i++) inputs[i] = window[i];
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
        for (; index < values.Length; index++) total += values[index] * values[index];
        return total;
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

    private Value[] ForwardLastHidden(IReadOnlyList<int> tokens, int positionOffset)
    {
        if (tokens.Count is < 1 || tokens.Count > Config.ContextLength)
            throw new ArgumentOutOfRangeException(nameof(tokens));
        if (positionOffset < 0) throw new ArgumentOutOfRangeException(nameof(positionOffset));

        var keys = new List<Value[]>(tokens.Count);
        var values = new List<Value[]>(tokens.Count);
        for (var position = 0; position < tokens.Count - 1; position++)
            PrepareContextToken(tokens[position], positionOffset + position, keys, values);
        return ForwardHiddenToken(tokens[^1], positionOffset + tokens.Count - 1, keys, values);
    }

    private Value[][] ForwardTargets(IReadOnlyList<int> tokens, int positionOffset, int firstLogitPosition)
    {
        if (tokens.Count is < 1 || tokens.Count > Config.ContextLength)
            throw new ArgumentOutOfRangeException(nameof(tokens));
        if (positionOffset < 0) throw new ArgumentOutOfRangeException(nameof(positionOffset));
        if ((uint)firstLogitPosition >= (uint)tokens.Count)
            throw new ArgumentOutOfRangeException(nameof(firstLogitPosition));

        var keys = new List<Value[]>(tokens.Count);
        var values = new List<Value[]>(tokens.Count);
        var result = new Value[tokens.Count - firstLogitPosition][];
        // With one Transformer layer, earlier positions contribute to later predictions
        // only through their keys and values. Their query, MLP, and vocabulary head are unused.
        for (var position = 0; position < firstLogitPosition; position++)
            PrepareContextToken(tokens[position], positionOffset + position, keys, values);
        for (var position = firstLogitPosition; position < tokens.Count; position++)
            result[position - firstLogitPosition] = ForwardToken(
                tokens[position], positionOffset + position, keys, values);
        return result;
    }

    private Value[] ForwardToken(int token, int position, List<Value[]> keys, List<Value[]> values)
        => Linear(ForwardHiddenToken(token, position, keys, values), _outputHead);

    private Value[] ForwardHiddenToken(int token, int position, List<Value[]> keys, List<Value[]> values)
    {
        var (x, normalized) = PrepareToken(token, position, keys, values);
        var residual = x;
        var query = Linear(normalized, _query);

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

        return x;
    }

    private void PrepareContextToken(int token, int position, List<Value[]> keys, List<Value[]> values) =>
        PrepareToken(token, position, keys, values);

    private (Value[] X, Value[] Normalized) PrepareToken(
        int token, int position, List<Value[]> keys, List<Value[]> values)
    {
        var x = new Value[Config.EmbeddingSize];
        for (var i = 0; i < x.Length; i++)
            x[i] = _tokenEmbedding[token][i] + _positionEmbedding[position % Config.PositionPeriod][i];

        var normalized = RmsNorm(x);
        var key = Linear(normalized, _key);
        var value = Linear(normalized, _value);
        keys.Add(key);
        values.Add(value);
        return (x, normalized);
    }

    private double[] NextLogits(IReadOnlyList<int> context)
    {
        var retainedStart = Math.Max(0, context.Count - Config.ContextLength);
        var localStart = Math.Max(retainedStart, context.Count - Config.AttentionWindow);
        var count = context.Count - localStart;
        var tail = new int[count];
        for (var i = 0; i < count; i++) tail[i] = context[localStart + i];

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

    private bool TryGenerateToolCall(
        List<int> context,
        out string toolName,
        out string[] arguments,
        out int[] callBody)
    {
        toolName = string.Empty;
        arguments = [];
        callBody = [];

        var candidates = Tools.RegisteredNames.Order(StringComparer.Ordinal)
            .Select(name => (Name: name, Input: _vocabulary.InputId(name)))
            .Where(candidate => candidate.Input != Tokenizer.Unknown)
            .Select(candidate => (candidate.Name, candidate.Input, Output: _tokenizer.OutputId(candidate.Input)))
            .DistinctBy(candidate => candidate.Output)
            .ToArray();
        if (candidates.Length == 0) return false;

        var body = new List<int>();
        var session = new InferenceSession(this, context);
        var selectedOutput = Greedy(session.Logits, candidates.Select(candidate => candidate.Output).ToArray());
        var selectedCandidate = candidates.Single(candidate => candidate.Output == selectedOutput);
        if (!Tools.TryGet(selectedCandidate.Name, out var selected)) return false;
        toolName = selectedCandidate.Name;
        context.Add(selectedCandidate.Input);
        body.Add(selectedCandidate.Input);
        session.Append(selectedCandidate.Input);

        var rawArguments = new List<string>();
        for (var parameterIndex = 0; parameterIndex < selected.ParameterTypes.Length; parameterIndex++)
        {
            if (parameterIndex > 0)
            {
                context.Add(Tokenizer.ArgumentSeparator);
                body.Add(Tokenizer.ArgumentSeparator);
                session.Append(Tokenizer.ArgumentSeparator);
            }
            var numeric = selected.ParameterTypes[parameterIndex] == typeof(int);
            var choices = _vocabulary.OutputWords
                .Where(word => word.Length is > 0 and <= 32 &&
                               (numeric ? word.All(char.IsDigit) : word.All(Tokenizer.IsIdentifierCharacter)))
                .Select(word => _vocabulary.InputId(word))
                .Select(input => (Input: input, Output: _tokenizer.OutputId(input)))
                .ToArray();
            if (choices.Length == 0) return false;
            var chosenOutput = Greedy(session.Logits, choices.Select(choice => choice.Output).ToArray());
            var chosen = choices.First(choice => choice.Output == chosenOutput);
            var value = _vocabulary.WordForInput(chosen.Input);
            rawArguments.Add(value);
            context.Add(chosen.Input);
            body.Add(chosen.Input);
            session.Append(chosen.Input);
        }

        arguments = rawArguments.ToArray();
        callBody = body.ToArray();
        return true;
    }

    private sealed class InferenceSession
    {
        private readonly Brain _brain;
        private readonly List<Value[]> _keys = [];
        private readonly List<Value[]> _values = [];
        private int _position;

        public InferenceSession(Brain brain, IReadOnlyList<int> context)
        {
            _brain = brain;
            var retainedStart = Math.Max(0, context.Count - brain.Config.ContextLength);
            var localStart = Math.Max(retainedStart, context.Count - brain.Config.AttentionWindow);
            _position = localStart - retainedStart;
            using var _ = Value.NoGrad();
            if (localStart >= context.Count)
                throw new ArgumentException("Inference context cannot be empty.", nameof(context));
            for (var index = localStart; index < context.Count - 1; index++)
                brain.PrepareContextToken(context[index], _position++, _keys, _values);
            var logits = brain.ForwardToken(context[^1], _position++, _keys, _values);
            Logits = logits.Select(x => x.Data).ToArray();
        }

        public double[] Logits { get; private set; }

        public void Append(int token)
        {
            if (_keys.Count >= _brain.Config.AttentionWindow)
            {
                _keys.RemoveAt(0);
                _values.RemoveAt(0);
            }
            using var _ = Value.NoGrad();
            var position = Math.Min(_position++, _brain.Config.ContextLength - 1);
            Logits = _brain.ForwardToken(token, position, _keys, _values).Select(x => x.Data).ToArray();
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
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string ComputeCheckpointIntegrity(Checkpoint checkpoint) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(checkpoint, JsonOptions())))).ToLowerInvariant();

    private sealed class Checkpoint
    {
        public int Version { get; set; }
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
        public Dictionary<string, V10Schemas.ConfidenceThreshold>? ConfidenceCalibration { get; set; }
        public Dictionary<string, string[]>? LabelSchemas { get; set; }
        public ToolSchema[]? ToolSchemas { get; set; }
        public ResponseCandidate[]? CandidateCatalog { get; set; }
        public string? CorpusHash { get; set; }
        public string IntegrityChecksum { get; set; } = "";
    }

}

internal sealed record TeachingRecovery(
    string ProjectPath,
    string CorpusDirectory,
    string CheckpointPath,
    int PlannedSteps,
    int UntilStep)
{
    public string TeachCommand(int untilStep) =>
        $"dotnet run -c Release --project {Quote(ProjectPath)} -- teach {Quote(CorpusDirectory)} " +
        $"{Quote(CheckpointPath)} --planned {PlannedSteps} --until {untilStep}";

    public string EvaluateCommand() =>
        $"dotnet run -c Release --project {Quote(ProjectPath)} -- evaluate " +
        $"{Quote(Path.Combine(CorpusDirectory, "test.jsonl"))} {Quote(CheckpointPath)}";

    internal static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}

internal static class Tokenizer
{
    public const int Bos = 0;
    public const int Sep = 1;
    public const int Eos = 2;
    public const int Text = 3;
    public const int Call = 4;
    public const int Result = 5;
    public const int State = 6;
    public const int Decide = 7;
    public const int RapportStart = 8;
    public const int MoodStart = 12;
    public const int IntentStart = 16;
    public const int ActionStart = 36;
    public const int ToneStart = 41;
    public const int TopicStart = 45;
    public const int GoalStart = 52;
    public const int AffectStart = 60;
    public const int ExpectedFalse = 65;
    public const int ExpectedTrue = 66;
    public const int Period = 67;
    public const int Comma = 68;
    public const int Question = 69;
    public const int Exclamation = 70;
    public const int Colon = 71;
    public const int ArgumentSeparator = 72;
    public const int WordBegin = 73;
    public const int WordEnd = 74;
    public const int CharacterStart = 75;
    public const int CharacterCount = 38;
    public const int WordStart = CharacterStart + CharacterCount;
    public const int Unknown = WordBegin;

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

            var character = original switch
            {
                '\u2018' or '\u2019' => '\'',
                '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' => '-',
                _ => char.ToUpperInvariant(original)
            };
            if (!IsVisibleCharacter(character))
                throw new ArgumentException(
                    $"Unsupported character '{original}'. Only A-Z, 0-9, whitespace, and . , ? ! ' - : are allowed.");

            if (character is '.' or '?' or '!')
            {
                TrimTrailingSpace(output);
                if (output.Length > 0 && output[^1] is '.' or '?' or '!')
                    output[^1] = MergeTerminal(output[^1], character);
                else
                    output.Append(character);
                pendingSpace = true;
                continue;
            }

            if (character is ',' or ':')
            {
                TrimTrailingSpace(output);
                if (output.Length == 0 || output[^1] != character) output.Append(character);
                pendingSpace = true;
                continue;
            }

            if (character is '\'' or '-')
            {
                TrimTrailingSpace(output);
                output.Append(character);
                pendingSpace = false;
                continue;
            }

            if (pendingSpace && output.Length > 0 && output[^1] is not ('\'' or '-')) output.Append(' ');
            output.Append(character);
            pendingSpace = false;
        }

        return output.ToString();
    }

    private static void TrimTrailingSpace(StringBuilder output)
    {
        if (output.Length > 0 && output[^1] == ' ') output.Length--;
    }

    private static char MergeTerminal(char first, char second) =>
        first == '?' || second == '?' ? '?' : first == '!' || second == '!' ? '!' : '.';

    public static IReadOnlyList<LexicalToken> Lex(string normalized)
    {
        var result = new List<LexicalToken>();
        var word = new StringBuilder();
        void FlushWord()
        {
            if (word.Length == 0) return;
            result.Add(new LexicalToken(LexicalTokenKind.Word, word.ToString()));
            word.Clear();
        }

        foreach (var character in normalized)
        {
            if (IsIdentifierCharacter(character) ||
                character is '\'' or '-' && word.Length > 0)
            {
                word.Append(character);
                continue;
            }
            FlushWord();
            if (character is '.' or ',' or '?' or '!' or ':')
                result.Add(new LexicalToken(LexicalTokenKind.Punctuation, character.ToString()));
        }
        FlushWord();
        return result;
    }

    public static string NormalizeWord(string word)
    {
        var normalized = Normalize(word);
        var tokens = Lex(normalized);
        if (tokens.Count != 1 || tokens[0].Kind != LexicalTokenKind.Word || tokens[0].Text != normalized)
            throw new InvalidDataException($"'{word}' is not one canonical word.");
        return normalized;
    }

    public static bool IsVisibleCharacter(char character) =>
        IsIdentifierCharacter(character) || character is ' ' or '.' or ',' or '?' or '!' or '\'' or '-' or ':';

    public static bool IsIdentifierCharacter(char character) =>
        character is >= 'A' and <= 'Z' or >= '0' and <= '9';

    public static int Character(char character) => character switch
    {
        >= 'A' and <= 'Z' => CharacterStart + character - 'A',
        >= '0' and <= '9' => CharacterStart + 26 + character - '0',
        '\'' => CharacterStart + 36,
        '-' => CharacterStart + 37,
        _ => throw new ArgumentOutOfRangeException(nameof(character))
    };

    public static char DecodeCharacter(int token) => token switch
    {
        >= CharacterStart and < CharacterStart + 26 => (char)('A' + token - CharacterStart),
        >= CharacterStart + 26 and < CharacterStart + 36 => (char)('0' + token - CharacterStart - 26),
        CharacterStart + 36 => '\'',
        CharacterStart + 37 => '-',
        _ => throw new ArgumentOutOfRangeException(nameof(token))
    };

    public static int Mood(NpcMood value) => MoodStart + (int)value;
    public static int Intent(DialogueIntent value) => IntentStart + (int)value;
    public static int Action(ResponseAction value) => ActionStart + (int)value;
    public static int Tone(ResponseTone value) => ToneStart + (int)value;
    public static int Topic(DialogueTopic value) => TopicStart + (int)value;
    public static int Goal(NpcGoal value) => GoalStart + (int)value;
    public static int Affect(UserAffect value) => AffectStart + (int)value;

    public static DialogueIntent DecodeIntent(int token) =>
        token >= IntentStart && token < IntentStart + Enum.GetValues<DialogueIntent>().Length
            ? (DialogueIntent)(token - IntentStart)
            : throw new ArgumentOutOfRangeException(nameof(token));

    public static UserAffect DecodeAffect(int token) =>
        token >= AffectStart && token < AffectStart + Enum.GetValues<UserAffect>().Length
            ? (UserAffect)(token - AffectStart)
            : throw new ArgumentOutOfRangeException(nameof(token));
}

/// <summary>Immutable vocabulary-bound tokenizer owned by one model.</summary>
internal sealed class DialogueTokenizer
{
    private readonly WordVocabulary _vocabulary;

    public DialogueTokenizer(WordVocabulary vocabulary) =>
        _vocabulary = vocabulary ?? throw new ArgumentNullException(nameof(vocabulary));

    public WordVocabulary Vocabulary => _vocabulary;
    public int VocabularySize => _vocabulary.InputSize;
    public int OutputSize => _vocabulary.OutputSize;
    public IReadOnlyCollection<int> GeneratedTextOutputs => _vocabulary.GeneratedTextOutputs;
    public bool ContainsUnknown(string text) => Encode(text).Contains(Tokenizer.WordBegin);
    public IReadOnlyList<string> UnknownWords(string text) => Tokenizer.Lex(DialogueText.Normalize(text))
        .Where(token => token.Kind == LexicalTokenKind.Word &&
                        _vocabulary.InputId(token.Text) == Tokenizer.Unknown)
        .Select(token => token.Text)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public int[] Encode(string normalized)
    {
        var result = new List<int>();
        foreach (var token in Tokenizer.Lex(DialogueText.Normalize(normalized)))
        {
            if (token.Kind == LexicalTokenKind.Word)
            {
                var known = _vocabulary.InputId(token.Text);
                if (known != Tokenizer.Unknown) result.Add(known);
                else
                {
                    result.Add(Tokenizer.WordBegin);
                    result.AddRange(token.Text.Select(Tokenizer.Character));
                    result.Add(Tokenizer.WordEnd);
                }
                continue;
            }
            result.Add(token.Text[0] switch
            {
                '.' => Tokenizer.Period,
                ',' => Tokenizer.Comma,
                '?' => Tokenizer.Question,
                '!' => Tokenizer.Exclamation,
                ':' => Tokenizer.Colon,
                _ => throw new InvalidDataException($"Unsupported punctuation token '{token.Text}'.")
            });
        }
        return result.ToArray();
    }

    public string DecodeInputToken(int token) => token switch
    {
        Tokenizer.Period => ".",
        Tokenizer.Comma => ",",
        Tokenizer.Question => "?",
        Tokenizer.Exclamation => "!",
        Tokenizer.Colon => ":",
        Tokenizer.WordBegin => "<WORD_BEGIN>",
        Tokenizer.WordEnd => "<WORD_END>",
        _ when token >= Tokenizer.CharacterStart && token < Tokenizer.WordStart => Tokenizer.DecodeCharacter(token).ToString(),
        _ when _vocabulary.IsWord(token) => _vocabulary.WordForInput(token),
        _ => throw new ArgumentOutOfRangeException(nameof(token), "Control tokens are not visible text.")
    };

    public string DetokenizeOutput(IEnumerable<int> outputTokens) =>
        DetokenizeInput(outputTokens.Select(_vocabulary.InputIdFromOutput));

    public string DetokenizeInput(IEnumerable<int> inputTokens)
    {
        var text = new StringBuilder();
        var oov = new StringBuilder();
        var inOov = false;
        foreach (var inputToken in inputTokens)
        {
            if (inputToken == Tokenizer.Eos) break;
            if (inputToken == Tokenizer.WordBegin)
            {
                if (inOov) throw new InvalidDataException("Nested OOV word markers are invalid.");
                inOov = true;
                oov.Clear();
                continue;
            }
            if (inputToken == Tokenizer.WordEnd)
            {
                if (!inOov || oov.Length == 0) throw new InvalidDataException("Invalid OOV word boundary.");
                if (text.Length > 0) text.Append(' ');
                text.Append(oov);
                inOov = false;
                continue;
            }
            if (inOov)
            {
                oov.Append(Tokenizer.DecodeCharacter(inputToken));
                continue;
            }
            if (inputToken is Tokenizer.Period or Tokenizer.Comma or Tokenizer.Question or
                Tokenizer.Exclamation or Tokenizer.Colon)
            {
                if (text.Length > 0 && text[^1] == ' ') text.Length--;
                text.Append(DecodeInputToken(inputToken));
                continue;
            }
            if (!_vocabulary.IsWord(inputToken)) continue;
            if (text.Length > 0) text.Append(' ');
            text.Append(_vocabulary.WordForInput(inputToken));
        }
        if (inOov) throw new InvalidDataException("Unterminated OOV word.");
        return text.ToString();
    }

    public int OutputId(int inputToken) => _vocabulary.OutputId(inputToken);
    public int InputIdFromOutput(int outputToken) => _vocabulary.InputIdFromOutput(outputToken);
}

internal enum LexicalTokenKind { Word, Punctuation }
internal readonly record struct LexicalToken(LexicalTokenKind Kind, string Text);

internal enum TrainingTask { Language, Perception, Tool }

[Flags]
internal enum PerceptionFields { None = 0, Intent = 1, Affect = 2, Expected = 4, All = Intent | Affect | Expected }

internal sealed record TrainingSample(
    int[] Tokens,
    int PositionOffset,
    int FirstTargetIndex,
    TrainingTask Task = TrainingTask.Language,
    string Bucket = "",
    string Source = "synthetic",
    TurnPerception? PerceptionTarget = null,
    string Family = "",
    PerceptionFields TargetFields = PerceptionFields.All);

#if false
internal static class DialogueKeys
{
    public static string Catalog(DialogueIntent intent, ResponseStyle style) => $"{intent}|{style}";

    public static string StateInput(string input, NpcState state) =>
        $"{state.Rapport}|{(int)state.Mood}|{(int)state.LastIntent}|{(int)state.ActiveTopic}|{(int)state.ActiveGoal}|{input}";

    public static string Example(
        string input,
        NpcState state,
        TurnDecision decision,
        ResponseTone tone) =>
        $"{StateInput(input, state)}|{(int)decision.Intent}|{(int)decision.Action}|{(int)tone}";
}

internal sealed class TrainingData
{
    internal const int ConditioningLength = 32;
    internal const int TargetChunkLength = 32;
    internal const int MaximumSampleLength = ConditioningLength + TargetChunkLength;

    private TrainingData(
        List<TrainingSample> samples,
        List<V10TrainingExample> structuredSamples,
        HashSet<string> toolNames,
        Dictionary<string, HashSet<string>> responseCatalog,
        Dictionary<string, string> examples)
    {
        Samples = samples;
        StructuredSamples = structuredSamples;
        ToolNames = toolNames;
        ResponseCatalog = responseCatalog;
        Examples = examples;
    }

    public IReadOnlyList<TrainingSample> Samples { get; }
    public IReadOnlyList<V10TrainingExample> StructuredSamples { get; }
    public IReadOnlySet<string> ToolNames { get; }
    public IReadOnlyDictionary<string, HashSet<string>> ResponseCatalog { get; }
    public IReadOnlyDictionary<string, string> Examples { get; }

    public static TrainingData Load(string path, DialogueTokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        var samples = new List<TrainingSample>();
        var structuredSamples = new List<V10TrainingExample>();
        var tools = new HashSet<string>(StringComparer.Ordinal);
        var responseCatalog = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var examples = new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
        var lineNumber = 0;

        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var row = JsonSerializer.Deserialize<TrainingRow>(line, options)
                    ?? throw new InvalidDataException("Empty object.");
                AddRow(row, samples, tools, responseCatalog, examples, rows);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
            {
                throw new InvalidDataException($"Invalid training data on line {lineNumber}: {exception.Message}", exception);
            }
        }

        if (samples.Count == 0) throw new InvalidDataException("Training data contains no examples.");
        return new TrainingData(samples, tools, responseCatalog, examples);
    }

    private static void AddRow(
        TrainingRow row,
        List<TrainingSample> samples,
        HashSet<string> tools,
        Dictionary<string, HashSet<string>> responseCatalog,
        Dictionary<string, string> examples,
        Dictionary<string, string> rows)
    {
        if (row.Input is null || row.Response is null || row.State is null ||
            row.Intent is null || row.Action is null)
            throw new InvalidDataException("Input, state, intent, action, and response are required.");

        var input = Tokenizer.Normalize(row.Input);
        var response = Tokenizer.Normalize(row.Response);
        if (input != row.Input || response != row.Response)
            throw new InvalidDataException("Training dialogue must already use canonical uppercase spacing and punctuation.");
        if (input.Length == 0 || response.Length == 0)
            throw new InvalidDataException("Input and response cannot be empty.");
        if (input.Length > 256) throw new InvalidDataException("Input exceeds 256 characters.");
        if (response.Length > 256) throw new InvalidDataException("Response exceeds 256 characters.");
        row.State.Validate();
        var decision = new TurnDecision(row.Intent.Value, row.Action.Value);
        if (!Enum.IsDefined(decision.Intent) || !Enum.IsDefined(decision.Action))
            throw new InvalidDataException("Intent or action is undefined.");
        if (decision.Action != Cognition.ActionFor(decision.Intent))
            throw new InvalidDataException("Action is invalid for the selected intent.");

        var hasAnyToolField = row.Tool is not null || row.Arguments is not null || row.Result is not null;
        var hasAllToolFields = row.Tool is not null && row.Arguments is not null && row.Result is not null;
        if (hasAnyToolField != hasAllToolFields)
            throw new InvalidDataException("Tool, arguments, and result must be supplied together.");
        if (hasAllToolFields != (decision.Action == ResponseAction.CallTool))
            throw new InvalidDataException("CALL_TOOL rows require tool, arguments, and result; other rows must omit them.");

        var transition = Cognition.Apply(row.State, decision, hasAllToolFields);
        var rowKey = DialogueKeys.StateInput(input, row.State);
        var rowValue = $"{decision.Intent}|{decision.Action}|{response}";
        if (rows.TryGetValue(rowKey, out var existingRow) && existingRow != rowValue)
            throw new InvalidDataException("The same state and input cannot have competing decisions or responses.");
        rows[rowKey] = rowValue;

        AddSamples(SerializeDecision(input, row.State, decision), samples);

        if (!hasAllToolFields)
        {
            var catalogKey = DialogueKeys.Catalog(decision.Intent, Cognition.StyleFor(transition.Tone));
            if (!responseCatalog.TryGetValue(catalogKey, out var catalog))
                responseCatalog.Add(catalogKey, catalog = new HashSet<string>(StringComparer.Ordinal));
            catalog.Add(response);

            var exampleKey = DialogueKeys.Example(input, row.State, decision, transition.Tone);
            examples[exampleKey] = response;
            AddSamples(SerializeResponse(input, transition.State, decision, transition.Tone, response), samples);
            return;
        }

        var tool = row.Tool!;
        if (tool.Length is < 1 or > 32 || tool.Any(c => !Tokenizer.IsIdentifierCharacter(c)))
            throw new InvalidDataException("Tool names must be 1-32 uppercase alphanumeric characters without spaces.");

        var arguments = row.Arguments!;
        if (arguments.Any(x => x.Length is < 1 or > 32 || x.Any(c => !Tokenizer.IsIdentifierCharacter(c))))
            throw new InvalidDataException("Tool arguments must be 1-32 character uppercase alphanumeric identifiers or integers.");

        var result = Tokenizer.Normalize(row.Result!);
        if (result != row.Result)
            throw new InvalidDataException("Tool results must already use canonical uppercase spacing and punctuation.");
        if (result.Length is < 1 or > 64) throw new InvalidDataException("Tool results must contain 1-64 characters.");
        tools.Add(tool);
        AddSamples(SerializeToolCall(input, row.State, decision, tool, arguments), samples);
        AddSamples(SerializeToolResult(
            input, transition.State, decision, transition.Tone, tool, arguments, result, response), samples);
    }

    private static SerializedStream SerializeDecision(string input, NpcState state, TurnDecision decision)
    {
        var tokens = Start(input);
        Brain.AppendState(tokens, state);
        tokens.Add(Tokenizer.Decide);
        var targetStart = tokens.Count;
        tokens.Add(Tokenizer.Intent(decision.Intent));
        tokens.Add(Tokenizer.Action(decision.Action));
        tokens.Add(Tokenizer.Eos);
        return new SerializedStream(tokens.ToArray(), targetStart);
    }

    private static SerializedStream SerializeResponse(
        string input,
        NpcState updatedState,
        TurnDecision decision,
        ResponseTone tone,
        string response)
    {
        var tokens = Start(input);
        Brain.AppendState(tokens, updatedState);
        tokens.Add(Tokenizer.Decide);
        tokens.Add(Tokenizer.Intent(decision.Intent));
        tokens.Add(Tokenizer.Action(decision.Action));
        tokens.Add(Tokenizer.Tone(tone));
        var targetStart = tokens.Count;
        tokens.Add(Tokenizer.Text);
        tokens.AddRange(Tokenizer.Encode(response));
        tokens.Add(Tokenizer.Eos);
        return new SerializedStream(tokens.ToArray(), targetStart);
    }

    private static SerializedStream SerializeToolCall(
        string input,
        NpcState state,
        TurnDecision decision,
        string tool,
        IReadOnlyList<string> arguments)
    {
        var tokens = Start(input);
        Brain.AppendState(tokens, state);
        tokens.Add(Tokenizer.Decide);
        tokens.Add(Tokenizer.Intent(decision.Intent));
        tokens.Add(Tokenizer.Action(decision.Action));
        tokens.Add(Tokenizer.Call);
        var targetStart = tokens.Count;
        AddCallBody(tokens, tool, arguments);
        tokens.Add(Tokenizer.Eos);
        return new SerializedStream(tokens.ToArray(), targetStart);
    }

    private static SerializedStream SerializeToolResult(
        string input,
        NpcState updatedState,
        TurnDecision decision,
        ResponseTone tone,
        string tool,
        IReadOnlyList<string> arguments,
        string result,
        string response)
    {
        var tokens = Start(input);
        Brain.AppendState(tokens, updatedState);
        tokens.Add(Tokenizer.Decide);
        tokens.Add(Tokenizer.Intent(decision.Intent));
        tokens.Add(Tokenizer.Action(decision.Action));
        tokens.Add(Tokenizer.Tone(tone));
        tokens.Add(Tokenizer.Call);
        AddCallBody(tokens, tool, arguments);
        tokens.Add(Tokenizer.Result);
        tokens.AddRange(Tokenizer.Encode(result));
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
            tokens.Add(Tokenizer.ArgumentSeparator);
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
        public NpcState? State { get; set; }
        public DialogueIntent? Intent { get; set; }
        public ResponseAction? Action { get; set; }
        public string? Tool { get; set; }
        public string[]? Arguments { get; set; }
        public string? Result { get; set; }
    }
}

#endif

internal static class DialogueKeys
{
    public static string Catalog(DialogueIntent intent, ResponseTone tone) => $"{intent}|{tone}";

    public static string StateInput(string input, NpcState state) =>
        $"{state.Rapport}|{(int)state.Mood}|{(int)state.LastIntent}|{(int)state.LastAffect}|" +
        $"{(int)state.ActiveTopic}|{(int)state.ActiveGoal}|{input}";

    public static string Example(
        string input,
        NpcState state,
        TurnPerception perception,
        TurnDecision decision,
        ResponseTone tone) =>
        $"{StateInput(input, state)}|{(int)perception.Intent}|{(int)perception.Affect}|" +
        $"{perception.ResponseExpected}|{(int)decision.Action}|{(int)tone}";
}

internal sealed class TrainingData
{
    internal const int ConditioningLength = 96;
    internal const int TargetChunkLength = 32;
    internal const int MaximumSampleLength = ConditioningLength + TargetChunkLength;

    private TrainingData(
        List<TrainingSample> samples,
        List<V10TrainingExample> structuredSamples,
        HashSet<string> toolNames,
        Dictionary<string, string> examples,
        Dictionary<string, string[]> responseCatalog)
    {
        Samples = samples;
        StructuredSamples = structuredSamples;
        ToolNames = toolNames;
        Examples = examples;
        ResponseCatalog = responseCatalog;
    }

    public IReadOnlyList<TrainingSample> Samples { get; }
    public IReadOnlyList<V10TrainingExample> StructuredSamples { get; }
    public IReadOnlySet<string> ToolNames { get; }
    public IReadOnlyDictionary<string, string> Examples { get; }
    public IReadOnlyDictionary<string, string[]> ResponseCatalog { get; }
    public IReadOnlyList<TrainingSample> LanguageSamples => Samples.Where(x => x.Task == TrainingTask.Language).ToArray();
    public IReadOnlyList<TrainingSample> PerceptionSamples => Samples.Where(x => x.Task == TrainingTask.Perception).ToArray();
    public IReadOnlyList<TrainingSample> ToolSamples => Samples.Where(x => x.Task == TrainingTask.Tool).ToArray();

    public static TrainingData Load(string path, DialogueTokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        var samples = new List<TrainingSample>();
        var structuredSamples = new List<V10TrainingExample>();
        var tools = new HashSet<string>(StringComparer.Ordinal);
        var examples = new Dictionary<string, string>(StringComparer.Ordinal);
        var responseCatalog = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var row = JsonSerializer.Deserialize<TrainingRow>(line, options)
                    ?? throw new InvalidDataException("Empty object.");
                AddRow(row, samples, structuredSamples, tools, examples, responseCatalog, rows, tokenizer);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
            {
                throw new InvalidDataException($"Invalid training data on line {lineNumber}: {exception.Message}", exception);
            }
        }
        if (samples.Count == 0) throw new InvalidDataException("Training data contains no examples.");
        return new TrainingData(samples, structuredSamples, tools, examples, responseCatalog
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                item => item.Key,
                item => item.Value.Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal));
    }

    private static void AddRow(
        TrainingRow row,
        List<TrainingSample> samples,
        List<V10TrainingExample> structuredSamples,
        HashSet<string> tools,
        Dictionary<string, string> examples,
        Dictionary<string, HashSet<string>> responseCatalog,
        Dictionary<string, string> rows,
        DialogueTokenizer tokenizer)
    {
        if (row.Input is null || row.State is null || row.Perception is null || row.Action is null)
            throw new InvalidDataException("Input, state, perception, and action are required.");
        var input = Tokenizer.Normalize(row.Input);
        if (input != row.Input || input.Length is < 1 or > 256)
            throw new InvalidDataException("Input must be canonical and contain 1-256 characters.");
        row.State.Validate();
        var perception = row.Perception;
        var decision = new TurnDecision(row.Action.Value);
        if (decision.Action != Cognition.ActionFor(perception))
            throw new InvalidDataException("Action is invalid for the supplied perception.");
        var source = string.IsNullOrWhiteSpace(row.Source) ? "manual" : row.Source;
        var bucket = $"{perception.Intent}|{perception.Affect}|{perception.ResponseExpected}";

        var hasAnyTool = row.Tool is not null || row.Arguments is not null || row.Result is not null;
        var hasAllTool = row.Tool is not null && row.Arguments is not null && row.Result is not null;
        if (hasAnyTool != hasAllTool) throw new InvalidDataException("Tool, arguments, and result must be supplied together.");
        if (hasAllTool != (decision.Action == ResponseAction.CallTool))
            throw new InvalidDataException("CALL_TOOL rows require all tool fields and other rows must omit them.");
        if (decision.Action == ResponseAction.NoResponse && row.Response is not null && row.Response != string.Empty)
            throw new InvalidDataException("NO_RESPONSE rows require an explicitly empty response.");

        string? response = null;
        if (row.Response is not null)
        {
            response = Tokenizer.Normalize(row.Response);
            if (response != row.Response || response.Length > 256)
                throw new InvalidDataException("Response must be canonical and no longer than 256 characters.");
            if (decision.Action != ResponseAction.NoResponse && response.Length == 0)
                throw new InvalidDataException("Response-producing rows cannot contain an empty response.");
        }

        var rowKey = DialogueKeys.StateInput(input, row.State);
        var rowValue = $"{perception}|{decision.Action}|{response ?? "<NULL>"}";
        if (rows.TryGetValue(rowKey, out var existing) && existing != rowValue)
            throw new InvalidDataException("The same state and input cannot have competing supervision.");
        rows[rowKey] = rowValue;

        samples.Add(CreatePerceptionSample(input, perception, bucket, source, row.Family, tokenizer));
        if (row.StructuredPerception is not null)
        {
            var structured = row.StructuredPerception;
            if (structured.SpeechActs.Any(value => !Enum.IsDefined(value)) ||
                structured.Domains.Any(value => !Enum.IsDefined(value)) ||
                structured.Goals.Any(value => !Enum.IsDefined(value)) ||
                structured.ContentFlags.Any(value => !Enum.IsDefined(value)) ||
                !Enum.IsDefined(structured.Affect) || !Enum.IsDefined(structured.Stance) || !Enum.IsDefined(structured.Policy))
                throw new InvalidDataException("Structured perception contains an unknown label.");
            if (string.IsNullOrWhiteSpace(row.SemanticFamilyId))
                throw new InvalidDataException("V10 structured rows require semanticFamilyId.");
            var supervised = (row.SupervisedHeads ?? V10Schemas.Labels.Keys.Concat(["tool", "responseCandidate"]).ToArray())
                .ToHashSet(StringComparer.Ordinal);
            var currentTurn = Brain.ExtractCurrentPlayerTurn(input);
            var currentOffset = input.LastIndexOf(currentTurn, StringComparison.Ordinal);
            var normalizedSlots = structured.Slots.Select(slot => NormalizeSlot(slot, currentTurn, currentOffset)).ToArray();
            structuredSamples.Add(new V10TrainingExample(
                currentTurn, structured.SpeechActs.ToArray(), structured.Domains.ToArray(),
                structured.Goals.ToArray(), structured.Affect, structured.Stance, structured.Policy,
                normalizedSlots, structured.ContentFlags.ToArray(), structured.ToolSchema ?? "NONE",
                structured.ResponseCandidateId ?? "ACKNOWLEDGE", source, row.SemanticFamilyId, supervised));
        }

        var transition = Cognition.Apply(row.State, perception, decision, hasAllTool);
        if (!hasAllTool)
        {
            if (!string.IsNullOrEmpty(response))
            {
                examples[DialogueKeys.Example(input, row.State, perception, decision, transition.Tone)] = response;
                if (source.Equals("SYNTHETIC", StringComparison.OrdinalIgnoreCase))
                {
                    var catalogKey = DialogueKeys.Catalog(perception.Intent, transition.Tone);
                    if (!responseCatalog.TryGetValue(catalogKey, out var responses))
                        responseCatalog.Add(catalogKey, responses = new HashSet<string>(StringComparer.Ordinal));
                    responses.Add(response);
                }
                AddSamples(SerializeResponse(input, transition.State, perception, decision, transition.Tone, response, tokenizer),
                    samples, TrainingTask.Language, bucket, source);
            }
            return;
        }

        var tool = row.Tool!;
        if (tool.Length is < 1 or > 32 || tool.Any(c => !Tokenizer.IsIdentifierCharacter(c)))
            throw new InvalidDataException("Tool names must be uppercase alphanumeric identifiers.");
        var arguments = row.Arguments!;
        if (arguments.Any(x => x.Length is < 1 or > 32 || x.Any(c => !Tokenizer.IsIdentifierCharacter(c))))
            throw new InvalidDataException("Tool arguments must be uppercase alphanumeric identifiers.");
        var result = Tokenizer.Normalize(row.Result!);
        if (result != row.Result || result.Length is < 1 or > 64)
            throw new InvalidDataException("Tool results must be canonical and contain 1-64 characters.");
        if (string.IsNullOrEmpty(response)) throw new InvalidDataException("Tool rows require a response.");
        tools.Add(tool);
        AddSamples(SerializeToolCall(input, row.State, perception, decision, tool, arguments, tokenizer),
            samples, TrainingTask.Tool, bucket, source);
        AddSamples(SerializeToolResult(input, transition.State, perception, decision, transition.Tone,
            tool, arguments, result, response, tokenizer), samples, TrainingTask.Language, bucket, source);
    }

    private static DialogueSlot NormalizeSlot(DialogueSlot slot, string currentTurn, int currentOffset)
    {
        if (!Enum.IsDefined(slot.Type) || slot.Value.Length == 0 || slot.Length != slot.Value.Length)
            throw new InvalidDataException("Structured slot metadata is invalid.");
        var normalizedValue = DialogueText.Normalize(slot.Value);
        if (normalizedValue != slot.Value)
            throw new InvalidDataException("Structured slot values must already be normalized.");
        if (Matches(slot.Start)) return slot;
        var relative = slot.Start - currentOffset;
        if (Matches(relative)) return slot with { Start = relative };
        throw new InvalidDataException($"Structured slot '{slot.Value}' does not match its source span.");

        bool Matches(int start) => start >= 0 && start + slot.Length <= currentTurn.Length &&
            currentTurn.AsSpan(start, slot.Length).SequenceEqual(slot.Value.AsSpan());
    }

    private static TrainingSample CreatePerceptionSample(
        string input, TurnPerception perception, string bucket, string source, string? family,
        DialogueTokenizer tokenizer)
    {
        var currentTurn = Brain.ExtractCurrentPlayerTurn(input);
        var encoded = tokenizer.Encode(currentTurn);
        var maximumText = 254;
        if (encoded.Length > maximumText) encoded = encoded[^maximumText..];
        var tokens = new List<int>(encoded.Length + 2) { Tokenizer.Bos };
        tokens.AddRange(encoded);
        tokens.Add(Tokenizer.Sep);
        var fields = source switch
        {
            "CLINC150" => PerceptionFields.Intent,
            "GOEMOTIONS" => PerceptionFields.Affect,
            _ => PerceptionFields.All
        };
        return new TrainingSample(
            tokens.ToArray(), 0, 1, TrainingTask.Perception, bucket, source, perception, family ?? "", fields);
    }

    private static SerializedStream SerializeResponse(
        string input, NpcState state, TurnPerception perception, TurnDecision decision,
        ResponseTone tone, string response, DialogueTokenizer tokenizer)
    {
        var tokens = Start(input, tokenizer);
        Brain.AppendState(tokens, state);
        tokens.Add(Tokenizer.Decide);
        AddPerception(tokens, perception, decision);
        tokens.Add(Tokenizer.Tone(tone));
        var target = tokens.Count;
        tokens.Add(Tokenizer.Text);
        tokens.AddRange(tokenizer.Encode(response));
        tokens.Add(Tokenizer.Eos);
        return new(tokens.ToArray(), target);
    }

    private static SerializedStream SerializeToolCall(
        string input, NpcState state, TurnPerception perception, TurnDecision decision,
        string tool, IReadOnlyList<string> arguments, DialogueTokenizer tokenizer)
    {
        var tokens = Start(input, tokenizer);
        Brain.AppendState(tokens, state);
        tokens.Add(Tokenizer.Decide);
        AddPerception(tokens, perception, decision);
        tokens.Add(Tokenizer.Call);
        var target = tokens.Count;
        AddCallBody(tokens, tool, arguments, tokenizer);
        tokens.Add(Tokenizer.Eos);
        return new(tokens.ToArray(), target);
    }

    private static SerializedStream SerializeToolResult(
        string input, NpcState state, TurnPerception perception, TurnDecision decision,
        ResponseTone tone, string tool, IReadOnlyList<string> arguments, string result, string response,
        DialogueTokenizer tokenizer)
    {
        var tokens = Start(input, tokenizer);
        Brain.AppendState(tokens, state);
        tokens.Add(Tokenizer.Decide);
        AddPerception(tokens, perception, decision);
        tokens.Add(Tokenizer.Tone(tone));
        tokens.Add(Tokenizer.Call);
        AddCallBody(tokens, tool, arguments, tokenizer);
        tokens.Add(Tokenizer.Result);
        tokens.AddRange(tokenizer.Encode(result));
        var target = tokens.Count;
        tokens.Add(Tokenizer.Text);
        tokens.AddRange(tokenizer.Encode(response));
        tokens.Add(Tokenizer.Eos);
        return new(tokens.ToArray(), target);
    }

    private static List<int> Start(string input, DialogueTokenizer tokenizer)
    {
        var tokens = new List<int> { Tokenizer.Bos };
        tokens.AddRange(tokenizer.Encode(input));
        tokens.Add(Tokenizer.Sep);
        return tokens;
    }

    private static void AddPerception(List<int> tokens, TurnPerception perception, TurnDecision decision)
    {
        tokens.Add(Tokenizer.Intent(perception.Intent));
        tokens.Add(Tokenizer.Affect(perception.Affect));
        tokens.Add(perception.ResponseExpected ? Tokenizer.ExpectedTrue : Tokenizer.ExpectedFalse);
        tokens.Add(Tokenizer.Action(decision.Action));
    }

    private static void AddCallBody(
        List<int> tokens, string tool, IReadOnlyList<string> arguments, DialogueTokenizer tokenizer)
    {
        tokens.AddRange(tokenizer.Encode(tool));
        foreach (var argument in arguments)
        {
            tokens.Add(Tokenizer.ArgumentSeparator);
            tokens.AddRange(tokenizer.Encode(argument));
        }
    }

    private static void AddSamples(
        SerializedStream stream, List<TrainingSample> samples, TrainingTask task, string bucket, string source)
    {
        for (var targetStart = stream.FirstTargetIndex;
             targetStart < stream.Tokens.Length;
             targetStart += TargetChunkLength)
        {
            var start = Math.Max(0, targetStart - ConditioningLength);
            var end = Math.Min(stream.Tokens.Length, targetStart + TargetChunkLength);
            samples.Add(new TrainingSample(
                stream.Tokens[start..end], start, targetStart - start, task, bucket, source));
        }
    }

    private sealed record SerializedStream(int[] Tokens, int FirstTargetIndex);
    private sealed class TrainingRow
    {
        public string? Input { get; set; }
        public NpcState? State { get; set; }
        public TurnPerception? Perception { get; set; }
        public ResponseAction? Action { get; set; }
        public string? Response { get; set; }
        public string? Source { get; set; }
        public string? Split { get; set; }
        public string? GroupId { get; set; }
        public string? Family { get; set; }
        public string? SemanticFamilyId { get; set; }
        public StructuredPerception? StructuredPerception { get; set; }
        public string[]? SupervisedHeads { get; set; }
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

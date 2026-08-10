using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fishbrain;

public sealed class BrainConfig
{
    public int LayerCount { get; set; } = 2;
    public int EmbeddingSize { get; set; } = 128;
    public int HeadCount { get; set; } = 8;
    public int MlpSize { get; set; } = 256;
    public int ContextLength { get; set; } = 256;
    public int AttentionWindow { get; set; } = 256;
    public int PositionPeriod { get; set; } = 256;
    public int MaximumOutputLength { get; set; } = 64;
    public double LearningRate { get; set; } = 0.005;
    public double Beta1 { get; set; } = 0.85;
    public double Beta2 { get; set; } = 0.99;
    public double AdamEpsilon { get; set; } = 1e-8;
    public int Seed { get; set; } = 42;
    public int PlannedSteps { get; set; } = 260_000;

    internal void Validate()
    {
        if (LayerCount <= 0 || LayerCount > 8)
            throw new InvalidDataException("LayerCount must be between 1 and 8.");
        if (EmbeddingSize <= 0 || EmbeddingSize > 1_024 || HeadCount <= 0 || HeadCount > 64 ||
            EmbeddingSize % HeadCount != 0)
            throw new InvalidDataException("EmbeddingSize must be 1-1024 and divisible by a 1-64 HeadCount.");
        if (MlpSize <= 0 || MlpSize > 8_192 || ContextLength <= 0 || ContextLength > 4_096 ||
            MaximumOutputLength <= 0 || MaximumOutputLength > 512)
            throw new InvalidDataException("Model dimensions exceed the supported bounds.");
        if (AttentionWindow <= 0 || AttentionWindow > ContextLength)
            throw new InvalidDataException("AttentionWindow must be within the context length.");
        if (PositionPeriod <= 0 || PositionPeriod > ContextLength || ContextLength % PositionPeriod != 0)
            throw new InvalidDataException("PositionPeriod must be a positive divisor of ContextLength.");
        if (!double.IsFinite(LearningRate) || !double.IsFinite(Beta1) || !double.IsFinite(Beta2) ||
            !double.IsFinite(AdamEpsilon) || LearningRate <= 0 || Beta1 is <= 0 or >= 1 ||
            Beta2 is <= 0 or >= 1 || AdamEpsilon <= 0)
            throw new InvalidDataException("Optimizer settings are invalid.");
        if (PlannedSteps <= 0 || PlannedSteps > 10_000_000)
            throw new InvalidDataException("PlannedSteps must be between 1 and 10,000,000.");
    }
}
/// <summary>A deliberately tiny word-level GPT for uppercase video-game dialogue.</summary>
public sealed partial class Brain
{
    private const int TeachingCheckpointInterval = 5_000;
    private const int HeadPolishStartStep = 200_000;
    private const int ResponsePolishStartStep = 245_000;
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
    private readonly Value[][][] _queryLayers;
    private readonly Value[][][] _keyLayers;
    private readonly Value[][][] _valueLayers;
    private readonly Value[][][] _attentionOutputLayers;
    private readonly Value[][][] _mlpInLayers;
    private readonly Value[][][] _mlpOutLayers;
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
    private Dictionary<string, ModelSchemas.ConfidenceThreshold> _confidenceCalibration;
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
        _ = ExpectedTransformerWeightCount(config, vocabulary.Words.Length, vocabulary.OutputWords.Length);
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
            ["BUY", "GET_BALANCE", "GET_CURRENT_LOCATION", "LIST_INVENTORY", "LIST_WARES",
             "LOOKUP_LOCATION", "LOOKUP_PRICE", "LOOKUP_WORLD_FACT", "SELL"],
            ResponseCandidates.Select(candidate => candidate.Id), config.Seed);
        _confidenceCalibration = ModelSchemas.DefaultCalibration;

        _tokenEmbedding = CreateMatrix(_tokenizer.VocabularySize, config.EmbeddingSize);
        _outputHead = CreateMatrix(_tokenizer.OutputSize, config.EmbeddingSize);
        _positionEmbedding = CreateMatrix(config.PositionPeriod, config.EmbeddingSize);
        _queryLayers = new Value[config.LayerCount][][];
        _keyLayers = new Value[config.LayerCount][][];
        _valueLayers = new Value[config.LayerCount][][];
        _attentionOutputLayers = new Value[config.LayerCount][][];
        _mlpInLayers = new Value[config.LayerCount][][];
        _mlpOutLayers = new Value[config.LayerCount][][];
        for (var layer = 0; layer < config.LayerCount; layer++)
        {
            _queryLayers[layer] = CreateMatrix(config.EmbeddingSize, config.EmbeddingSize);
            _keyLayers[layer] = CreateMatrix(config.EmbeddingSize, config.EmbeddingSize);
            _valueLayers[layer] = CreateMatrix(config.EmbeddingSize, config.EmbeddingSize);
            _attentionOutputLayers[layer] = CreateMatrix(config.EmbeddingSize, config.EmbeddingSize);
            _mlpInLayers[layer] = CreateMatrix(config.MlpSize, config.EmbeddingSize);
            _mlpOutLayers[layer] = CreateMatrix(config.EmbeddingSize, config.MlpSize);
        }
        _intentHead = CreateMatrix(Enum.GetValues<DialogueIntent>().Length, config.EmbeddingSize);
        _affectHead = CreateMatrix(Enum.GetValues<UserAffect>().Length, config.EmbeddingSize);
        _expectedHead = CreateMatrix(2, config.EmbeddingSize);

        AddParameters(_tokenEmbedding);
        AddParameters(_outputHead);
        AddParameters(_positionEmbedding);
        for (var layer = 0; layer < config.LayerCount; layer++)
        {
            AddParameters(_queryLayers[layer]);
            AddParameters(_keyLayers[layer]);
            AddParameters(_valueLayers[layer]);
            AddParameters(_attentionOutputLayers[layer]);
            AddParameters(_mlpInLayers[layer]);
            AddParameters(_mlpOutLayers[layer]);
        }
        AddParameters(_intentHead);
        AddParameters(_affectHead);
        AddParameters(_expectedHead);

        _weights = _parameters.Select(parameter => parameter.Data).ToArray();
        _packedGradients = new double[_parameters.Count];
        _adamM = new double[_parameters.Count];
        _adamV = new double[_parameters.Count];
    }

    public BrainConfig Config { get; }
    public int CompletedSteps => _step;
    public IReadOnlyCollection<string> TrainedTools => _trainedTools;
    internal DialogueTokenizer DialogueTokenizer => _tokenizer;

    public static Brain Load(string path)
    {
        if (IsInferenceCheckpoint(path)) return LoadInferenceCheckpoint(path);
        var checkpoint = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(path), JsonOptions())
            ?? throw new InvalidDataException("Checkpoint is empty.");
        if (checkpoint.Config is null || checkpoint.Words is null || checkpoint.OutputWords is null ||
            checkpoint.Weights is null || checkpoint.AdamM is null || checkpoint.AdamV is null ||
            checkpoint.StructuredWeights is null)
            throw new InvalidDataException("Training checkpoint contains null model data.");
        var expectedIntegrity = checkpoint.IntegrityChecksum;
        checkpoint.IntegrityChecksum = "";
        if (expectedIntegrity is null || expectedIntegrity.Length != 64 ||
            !ComputeCheckpointIntegrity(checkpoint).Equals(expectedIntegrity, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Training checkpoint integrity checksum failed.");
        checkpoint.IntegrityChecksum = expectedIntegrity;
        checkpoint.Config.Validate();
        if (checkpoint.CompletedSteps < 0 || checkpoint.CompletedSteps > checkpoint.Config.PlannedSteps ||
            checkpoint.SamplerPosition < 0 || checkpoint.SamplerPosition > checkpoint.CompletedSteps ||
            checkpoint.BestPerceptionStep < 0 ||
            checkpoint.BestPerceptionStep > checkpoint.CompletedSteps || checkpoint.BestRealizationStep < 0 ||
            checkpoint.BestRealizationStep > checkpoint.CompletedSteps ||
            !double.IsFinite(checkpoint.BestPerceptionScore) || !double.IsFinite(checkpoint.BestRealizationLoss) ||
            checkpoint.BestPerceptionScore is < -1 or > 1 || checkpoint.BestRealizationLoss < 0 ||
            checkpoint.StructuredUpdates < 0 ||
            string.IsNullOrWhiteSpace(checkpoint.CurriculumPhase) ||
            checkpoint.CurriculumPhase.Any(character => character is not (>= 'A' and <= 'Z') and not '_'))
            throw new InvalidDataException("Training checkpoint progress metadata is invalid.");
        if (checkpoint.Words.Length == 0 || checkpoint.OutputWords.Length == 0)
            throw new InvalidDataException("The checkpoint does not contain a word vocabulary.");
        ValidateVocabularyArrays(checkpoint.Words, checkpoint.OutputWords);
        ValidateCorpusHash(checkpoint.CorpusHash ?? "UNKNOWN");
        if (checkpoint.TrainedTools is null || checkpoint.TrainedExamples is null || checkpoint.ResponseCatalog is null ||
            checkpoint.ConfidenceCalibration is null || checkpoint.LabelSchemas is null || checkpoint.ToolSchemas is null ||
            checkpoint.CandidateCatalog is null || checkpoint.StructuredLabelThresholds is null ||
            checkpoint.FrozenStructuredHeads is null)
            throw new InvalidDataException("Training checkpoint is missing required schema data.");
        ValidateTrainedTools(checkpoint.TrainedTools);
        ValidateTrainingResponseCatalog(checkpoint.ResponseCatalog);
        if (checkpoint.TrainedExamples.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value is null ||
            item.Key.Length > 4_096 || item.Value.Length > 256 || !DialogueText.IsCanonical(item.Value)))
            throw new InvalidDataException("Training checkpoint examples are invalid.");
        if (checkpoint.Weights.Length != ExpectedTransformerWeightCount(checkpoint.Config,
                checkpoint.Words.Length, checkpoint.OutputWords.Length) || checkpoint.StructuredWeights.Length > 10_000_000)
            throw new InvalidDataException("Training checkpoint parameter count exceeds the supported architecture.");
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
        if (checkpoint.Weights.Any(value => !double.IsFinite(value)) ||
            checkpoint.AdamM.Any(value => !double.IsFinite(value)) ||
            checkpoint.AdamV.Any(value => !double.IsFinite(value)))
            throw new InvalidDataException("Training checkpoint contains non-finite optimizer parameters.");

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
        ModelSchemas.Validate(checkpoint.LabelSchemas, checkpoint.ConfidenceCalibration);
        brain._confidenceCalibration = checkpoint.ConfidenceCalibration;
        if (checkpoint.CandidateCatalog.Any(candidate => candidate is null) ||
            !SchemaEquals(checkpoint.CandidateCatalog.OrderBy(candidate => candidate.Id).ToArray(),
                ResponseCandidates.OrderBy(candidate => candidate.Id).ToArray()))
            throw new InvalidDataException("Training checkpoint response candidate schema does not match this runtime.");
        var expectedToolSchemas = DemoGameTools.CreateMerchant().Schemas.OrderBy(schema => schema.Name).ToArray();
        if (checkpoint.ToolSchemas.Any(schema => schema is null) ||
            !SchemaEquals(checkpoint.ToolSchemas.OrderBy(schema => schema.Name).ToArray(), expectedToolSchemas))
            throw new InvalidDataException("Training checkpoint tool schemas do not match this runtime.");
        brain._corpusHash = checkpoint.CorpusHash ?? "UNKNOWN";
        brain._structuredHeads.Restore(checkpoint.StructuredWeights, checkpoint.StructuredUpdates,
            checkpoint.StructuredLabelThresholds, checkpoint.FrozenStructuredHeads);
        return brain;
    }

    private static void ValidateTrainingResponseCatalog(IReadOnlyDictionary<string, string[]> catalog)
    {
        if (catalog.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value is null || item.Value.Length == 0 ||
            item.Value.Distinct(StringComparer.Ordinal).Count() != item.Value.Length ||
            item.Value.Any(value => !DialogueText.IsCanonical(value) || value.Length > 256)))
            throw new InvalidDataException("Training checkpoint response catalog is invalid.");
    }

    internal GeneratedReplyResult Reply(string recentDialogue, NpcState state, double temperature = 0.2) =>
        ReplyCore(recentDialogue, state, temperature, useExactMemory: true, seedOverride: null);

    internal GeneratedReplyResult DebugReplyWithoutMemory(string recentDialogue, NpcState state, double temperature = 0.2) =>
        ReplyCore(recentDialogue, state, temperature, useExactMemory: false, seedOverride: null);

    private GeneratedReplyResult GeneratedReply(
        string recentDialogue, string currentTurn, NpcState state, int seed, double temperature = 0.2) =>
        ReplyCore(recentDialogue, state, temperature, useExactMemory: false, seed, currentTurn);

    private GeneratedReplyResult ReplyCore(
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

        if (decision.Action == ResponseAction.CallTool)
        {
            var failed = Cognition.Apply(state, perception, decision);
            return new GeneratedReplyResult(SafeFallback, failed.State, perception, decision, failed.Tone);
        }

        var transition = Cognition.Apply(state, perception, decision);
        if (decision.Action == ResponseAction.NoResponse)
            return new GeneratedReplyResult(string.Empty, transition.State, perception, decision, transition.Tone);

        var memoryKey = DialogueKeys.Example(input, state, perception, decision, transition.Tone);
        if (useExactMemory && decision.Action != ResponseAction.CallTool &&
            _trainedExamples.TryGetValue(memoryKey, out var trainedResponse))
        {
            return new GeneratedReplyResult(trainedResponse, transition.State, perception, decision, transition.Tone);
        }

        var responsePrompt = StartPrompt(input);
        AppendState(responsePrompt, transition.State);
        responsePrompt.Add(Tokenizer.Decide);
        responsePrompt.Add(Tokenizer.Intent(perception.Intent));
        responsePrompt.Add(Tokenizer.Affect(perception.Affect));
        responsePrompt.Add(perception.ResponseExpected ? Tokenizer.ExpectedTrue : Tokenizer.ExpectedFalse);
        responsePrompt.Add(Tokenizer.Action(decision.Action));
        responsePrompt.Add(Tokenizer.Tone(transition.Tone));
        responsePrompt.Add(Tokenizer.Text);

        var text = GenerateText(responsePrompt, temperature, ReplyRandom(input, state, seedOverride));
        text = SelectSafeResponse(text, input, perception.Intent, decision.Action, transition.Tone);
        return new GeneratedReplyResult(text, transition.State, perception, decision, transition.Tone);
    }
}

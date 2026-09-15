using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fishbrain;

public sealed partial class LegacyBrain
{
    internal static LegacyBrain CreateForTesting(BrainConfig config, params string[] trainedTools) =>
            new(config, WordVocabulary.Testing(), new DeterministicRandom(config.Seed), trainedTools, [], []);

    internal static LegacyBrain CreateForTesting(BrainConfig config, WordVocabulary vocabulary, params string[] trainedTools) =>
        new(config, vocabulary, new DeterministicRandom(config.Seed), trainedTools, [], []);

    internal static LegacyBrain CreateForTestingWithExamples(
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
        _structuredHeads.Predict(DialogueText.Normalize(input), [], ContextVector(input), ExtractCurrentPlayerTurn(input));

    internal StructuredMetrics DebugEvaluateStructured(IReadOnlyList<TrainingExample> examples) =>
        _structuredHeads.Evaluate(examples, example => ContextVector(example.Context));

    internal (StructuredMetrics Metrics, StructuredPerception[] Predictions) DebugEvaluateStructuredBatch(
        IReadOnlyList<TrainingExample> examples)
    {
        var vectors = new double[examples.Count][];
        var predictions = new StructuredPerception[examples.Count];
        Parallel.For(0, examples.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, index =>
            {
                vectors[index] = ContextVector(examples[index].Context);
                predictions[index] = _structuredHeads.Predict(
                    examples[index].Context, [], vectors[index], examples[index].Input,
                    examples[index].Turns);
            });
        var contextByExample = Enumerable.Range(0, examples.Count).ToDictionary(
            index => EvaluationExampleKey(examples[index]),
            index => (IReadOnlyList<double>)vectors[index], StringComparer.Ordinal);
        var metrics = _structuredHeads.EvaluateWithPredictions(examples, predictions,
            example => contextByExample[EvaluationExampleKey(example)]);
        return (metrics, predictions);
    }

    internal static string DiagnoseTeaching(string corpusDirectory, string checkpointPath, string split = "validation")
    {
        if (split is not ("validation" or "test"))
            throw new ArgumentException("Diagnostic split must be validation or test.", nameof(split));
        var fullCorpusDirectory = Path.GetFullPath(corpusDirectory);
        var brain = Load(Path.GetFullPath(checkpointPath));
        var corpusHash = ComputeCorpusHash(fullCorpusDirectory);
        if (brain._corpusHash != "UNKNOWN" && brain._corpusHash != corpusHash)
            throw new InvalidDataException("Diagnostic corpus hash differs from the checkpoint.");
        var diagnostics = TrainingData.Load(Path.Combine(fullCorpusDirectory, split + ".jsonl"), brain._tokenizer);
        var examples = FamilyBalancedEvaluationSet(diagnostics.StructuredSamples, brain.Config.Seed);
        var (metrics, predictions) = brain.DebugEvaluateStructuredBatch(examples);
        var policyConfusions = examples.Select((example, index) => new
        {
            Supervised = example.SupervisedHeads.Contains("policy"),
            Expected = example.Policy,
            Predicted = predictions[index].Policy
        })
            .Where(item => item.Supervised && item.Expected != item.Predicted)
            .GroupBy(item => new { item.Expected, item.Predicted })
            .Select(group => new { group.Key.Expected, group.Key.Predicted, Count = group.Count() })
            .OrderByDescending(item => item.Count).ThenBy(item => item.Expected)
            .ThenBy(item => item.Predicted).ToArray();
        var policyConfusionExamples = examples.Select((example, index) => new
        {
            Example = example,
            Prediction = predictions[index]
        })
            .Where(item => item.Example.SupervisedHeads.Contains("policy") &&
                           item.Example.Policy != item.Prediction.Policy)
            .Select(item => new
            {
                item.Example.Source,
                item.Example.SemanticFamilyId,
                item.Example.Input,
                ExpectedPolicy = item.Example.Policy,
                PredictedPolicy = item.Prediction.Policy,
                ExpectedTool = item.Example.ToolSchema,
                PredictedTool = item.Prediction.ToolSchema ?? "NONE",
                ExpectedDiscourseAct = item.Example.Discourse.Act,
                PredictedDiscourseAct = item.Prediction.Discourse?.Act ?? DiscourseAct.None
            })
            .Take(100).ToArray();
        var toolConfusions = examples.Select((example, index) => new
        {
            Supervised = example.SupervisedHeads.Contains("tool"),
            Expected = example.ToolSchema,
            Predicted = predictions[index].ToolSchema ?? "NONE"
        })
            .Where(item => item.Supervised && item.Expected != item.Predicted)
            .GroupBy(item => new { item.Expected, item.Predicted })
            .Select(group => new { group.Key.Expected, group.Key.Predicted, Count = group.Count() })
            .OrderByDescending(item => item.Count).ThenBy(item => item.Expected, StringComparer.Ordinal)
            .ThenBy(item => item.Predicted, StringComparer.Ordinal).Take(20).ToArray();
        var responseConfusions = examples.Select((example, index) => new
        {
            Supervised = example.SupervisedHeads.Contains("responseCandidate"),
            Expected = example.ResponseCandidateId,
            Predicted = predictions[index].ResponseCandidateId ?? ""
        })
            .Where(item => item.Supervised && item.Expected != item.Predicted)
            .GroupBy(item => new { item.Expected, item.Predicted })
            .Select(group => new { group.Key.Expected, group.Key.Predicted, Count = group.Count() })
            .OrderByDescending(item => item.Count).ThenBy(item => item.Expected, StringComparer.Ordinal)
            .ThenBy(item => item.Predicted, StringComparer.Ordinal).Take(20).ToArray();
        var mutatingFalsePositives = examples.Select((example, index) => new
        {
            Example = example,
            Prediction = predictions[index]
        })
            .Where(item => item.Example.SupervisedHeads.Contains("tool") &&
                           item.Prediction.ToolSchema is "BUY" or "SELL" &&
                           item.Example.ToolSchema != item.Prediction.ToolSchema)
            .Select(item => new
            {
                item.Example.Source,
                item.Example.Input,
                ExpectedTool = item.Example.ToolSchema,
                PredictedTool = item.Prediction.ToolSchema,
                ExpectedPolicy = item.Example.Policy,
                PredictedPolicy = item.Prediction.Policy,
                item.Example.Domains,
                item.Example.Goals
            }).Take(50).ToArray();
        var report = new
        {
            checkpoint = Path.GetFullPath(checkpointPath),
            completedSteps = brain.CompletedSteps,
            split,
            corpusHash,
            records = examples.Count,
            metrics,
            speechActs = MultiLabelDiagnostics("speechActs", Enum.GetValues<SpeechAct>(),
                example => example.SpeechActs, prediction => prediction.SpeechActs),
            domains = MultiLabelDiagnostics("domains", Enum.GetValues<DialogueDomain>(),
                example => example.Domains, prediction => prediction.Domains),
            goals = MultiLabelDiagnostics("goals", Enum.GetValues<DialogueGoal>(),
                example => example.Goals, prediction => prediction.Goals),
            content = MultiLabelDiagnostics("content", Enum.GetValues<ContentFlag>(),
                example => example.ContentFlags, prediction => prediction.ContentFlags),
            slots = SlotDiagnostics(),
            policyConfusions,
            policyConfusionExamples,
            toolConfusions,
            mutatingFalsePositives,
            responseConfusions
        };
        return JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });

        object[] MultiLabelDiagnostics<T>(
            string head, IReadOnlyList<T> labels,
            Func<TrainingExample, IReadOnlyList<T>> expected,
            Func<StructuredPerception, IReadOnlyList<T>> predicted) where T : struct, Enum
        {
            var supervised = examples.Select((example, index) => (Example: example, Index: index))
                .Where(item => item.Example.SupervisedHeads.Contains(head)).ToArray();
            return labels.Select(label =>
            {
                var truePositive = supervised.Count(item => expected(item.Example).Contains(label) &&
                                                          predicted(predictions[item.Index]).Contains(label));
                var falsePositive = supervised.Count(item => !expected(item.Example).Contains(label) &&
                                                           predicted(predictions[item.Index]).Contains(label));
                var falseNegative = supervised.Count(item => expected(item.Example).Contains(label) &&
                                                           !predicted(predictions[item.Index]).Contains(label));
                var support = truePositive + falseNegative;
                var f1 = truePositive == 0 ? 0.0 :
                    2.0 * truePositive / (2.0 * truePositive + falsePositive + falseNegative);
                return (object)new { label = label.ToString(), support, truePositive, falsePositive, falseNegative, f1 };
            }).ToArray();
        }

        object[] SlotDiagnostics()
        {
            var supervised = examples.Select((example, index) => (Example: example, Index: index))
                .Where(item => item.Example.SupervisedHeads.Contains("slots")).ToArray();
            return Enum.GetValues<SlotType>().Select(type =>
            {
                var expected = supervised.SelectMany(item => item.Example.Slots
                    .Where(slot => slot.Type == type)
                    .Select(slot => (item.Index, slot.Start, slot.Length))).ToHashSet();
                var predicted = supervised.SelectMany(item => predictions[item.Index].Slots
                    .Where(slot => slot.Type == type)
                    .Select(slot => (item.Index, slot.Start, slot.Length))).ToHashSet();
                var truePositive = expected.Intersect(predicted).Count();
                var falsePositive = predicted.Count - truePositive;
                var falseNegative = expected.Count - truePositive;
                var f1 = truePositive == 0 ? 0.0 :
                    2.0 * truePositive / (2.0 * truePositive + falsePositive + falseNegative);
                return (object)new { label = type.ToString(), support = expected.Count, truePositive, falsePositive, falseNegative, f1 };
            }).ToArray();
        }
    }

    private double[] ContextVector(string text)
    {
        var normalized = DialogueText.Normalize(text);
        var encoded = _tokenizer.Encode(normalized);
        if (encoded.Length > Config.ContextLength) encoded = encoded[^Config.ContextLength..];
        var current = ExtractCurrentPlayerTurn(normalized);
        var currentTokenCount = Math.Min(encoded.Length, _tokenizer.Encode(current).Length);
        return PackedTrainer.ContextVector(Config, _tokenizer, _weights, encoded, currentTokenCount);
    }

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
}

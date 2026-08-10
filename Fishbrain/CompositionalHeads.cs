using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace Fishbrain;

internal sealed partial class CompositionalHeadModel
{
    private const int LexicalFeatureCount = 4_096;
    private const int ContextFeatureCount = 128;
    private const int FeatureCount = LexicalFeatureCount + ContextFeatureCount;
    private const double ContextFeatureScale = 1.0 / 11.313708498984761;
    private const double SlotLearningRateScale = 0.25;
    private const double SlotPositiveWeight = 2.0;
    private const int FactSpanClassCount = 3;
    private const double MaximumPositiveWeight = 2.0;
    private const double NoToolWeight = 1.0;
    private const double MutatingToolWeight = 2.0;
    private const double ReadOnlyToolWeight = 2.0;
    private const string ToolNoneMarginKey = "tool:none-margin";
    private static readonly int SlotClassCount = 1 + 2 * Enum.GetValues<SlotType>().Length;
    private static readonly ConditionalWeakTable<double[], SparseFeatureIndices> SparseFeatureCache = new();
    private readonly string[] _tools;
    private readonly string[] _candidates;
    private readonly Layout _layout;
    private readonly double[] _weights;
    private readonly HashSet<string> _frozenHeads = new(StringComparer.Ordinal);
    private Dictionary<string, double> _labelThresholds = DefaultLabelThresholds();

    public CompositionalHeadModel(IEnumerable<string> tools, IEnumerable<string> candidates, int seed)
    {
        _tools = new[] { "NONE" }.Concat(tools).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        _candidates = candidates.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (_candidates.Length == 0) throw new ArgumentException("At least one response candidate is required.", nameof(candidates));
        _layout = new Layout(_tools.Length, _candidates.Length);
        _weights = new double[_layout.WeightCount];
        var random = new DeterministicRandom(seed ^ 0x5a17c9);
        for (var index = 0; index < _weights.Length; index++) _weights[index] = random.NextGaussian() * 0.01;
        for (var row = 0; row < _weights.Length / FeatureCount; row++)
        {
            Array.Clear(_weights, row * FeatureCount + LexicalFeatureCount, ContextFeatureCount);
        }
    }

    public int Updates { get; private set; }
    public IReadOnlyList<string> Tools => _tools;
    public IReadOnlyList<string> Candidates => _candidates;
    public int WeightCount => _weights.Length;
    public double[] Snapshot() => (double[])_weights.Clone();
    public Dictionary<string, double> SnapshotLabelThresholds() =>
        new(_labelThresholds, StringComparer.Ordinal);
    public string[] SnapshotFrozenHeads() => _frozenHeads.Order(StringComparer.Ordinal).ToArray();

    internal double[] SnapshotHeadWeights(string head)
    {
        var (offset, count) = head switch
        {
            "slots" => (_layout.Slot, SlotClassCount * FeatureCount),
            "factSpan" => (_layout.FactSpan, FactSpanClassCount * FeatureCount),
            "antecedent" => (_layout.Antecedent, FeatureCount),
            "responseCandidate" => (_layout.Candidate, _candidates.Length * FeatureCount),
            _ => throw new ArgumentOutOfRangeException(nameof(head))
        };
        return _weights.AsSpan(offset, count).ToArray();
    }

    internal static bool MeetsReleaseNeuralThresholds(StructuredMetrics metrics) =>
        metrics.SpeechActMacroF1 >= 0.85 && metrics.DomainMacroF1 >= 0.84 &&
        metrics.GoalMacroF1 >= 0.80 && metrics.AffectAccuracy >= 0.85 &&
        metrics.PolicyAccuracy >= 0.90 && metrics.ContentMacroF1 >= 0.90 &&
        metrics.SlotSpanF1 >= 0.85 && metrics.ToolAccuracy >= 0.95 &&
        metrics.MutatingToolPrecision >= 0.97 && metrics.KnowledgeTargetAccuracy >= 0.90 &&
        metrics.ResponseTop1 >= 0.85 && metrics.ResponseTop3 >= 0.95 &&
        metrics.VariationRecallAt10 >= 0.95 && metrics.VariationMrr >= 0.80 &&
        metrics.DiscourseActAccuracy >= 0.90 && metrics.SpeakerAttributionAccuracy >= 0.95 &&
        metrics.FactSpanF1 >= 0.90 && metrics.AntecedentAccuracy >= 0.90 &&
        metrics.CorrectionStateAccuracy >= 0.95;

    internal static double SelectToolNoneMargin(
        IReadOnlyList<(double Margin, double Accuracy, double MutatingPrecision)> candidates,
        double minimumMutatingPrecision = 0.99)
    {
        if (candidates.Count == 0)
        {
            throw new ArgumentException("At least one tool-margin candidate is required.", nameof(candidates));
        }

        var eligible = candidates
            .Where(item => item.MutatingPrecision >= minimumMutatingPrecision)
            .OrderByDescending(item => item.Accuracy)
            .ThenBy(item => item.Margin)
            .ToArray();
        var selected = eligible.Length > 0
            ? eligible[0]
            : candidates
                .OrderByDescending(item => item.MutatingPrecision)
                .ThenByDescending(item => item.Accuracy)
                .ThenBy(item => item.Margin)
                .First();
        return selected.Margin;
    }

    public void Restore(
        IReadOnlyList<double> weights, int updates,
        IReadOnlyDictionary<string, double>? labelThresholds = null,
        IReadOnlyCollection<string>? frozenHeads = null)
    {
        if (weights.Count != _weights.Length || weights.Any(weight => !double.IsFinite(weight)))
            throw new InvalidDataException("Structured head parameters are invalid.");
        if (updates < 0) throw new InvalidDataException("Structured update count cannot be negative.");
        for (var index = 0; index < weights.Count; index++) _weights[index] = weights[index];
        Updates = updates;
        if (labelThresholds is not null)
        {
            var expected = DefaultLabelThresholds();
            if (labelThresholds.Count != expected.Count || expected.Keys.Any(key => !labelThresholds.ContainsKey(key)) ||
                labelThresholds.Any(item => !double.IsFinite(item.Value) ||
                    item.Key == ToolNoneMarginKey
                        ? item.Value is < 0.0 or > 0.50
                        : item.Value is < 0.05 or > 0.99))
                throw new InvalidDataException("Structured per-label calibration does not match the model schema.");
            _labelThresholds = new Dictionary<string, double>(labelThresholds, StringComparer.Ordinal);
        }
        _frozenHeads.Clear();
        if (frozenHeads is not null)
        {
            var knownHeads = ModelSchemas.Labels.Keys.Concat(["tool", "responseCandidate"])
                .ToHashSet(StringComparer.Ordinal);
            if (frozenHeads.Any(head => !knownHeads.Contains(head)))
            {
                throw new InvalidDataException("Frozen structured heads do not match the model schema.");
            }

            foreach (var head in frozenHeads)
            {
                _frozenHeads.Add(head);
            }
        }
    }

    public StructuredPerception Predict(
        string input, IReadOnlyList<DialogueSlot> preservedSlots, IReadOnlyList<double>? contextVector = null,
        string? currentInput = null, IReadOnlyList<DialogueUtterance>? utterances = null)
    {
        var features = Features(input, contextVector);
        var speech = PredictMulti<SpeechAct>("speechActs", _layout.Speech, features, maximum: 3);
        var domains = PredictMulti<DialogueDomain>("domains", _layout.Domain, features, maximum: 3);
        var goals = PredictMulti<DialogueGoal>("goals", _layout.Goal, features, maximum: 3);
        var (affect, affectConfidence) = PredictSoftmax<UserAffect>(_layout.Affect, features);
        var (stance, stanceConfidence) = PredictSoftmax<DialogueStance>(_layout.Stance, features);
        var content = PredictMulti<ContentFlag>("content", _layout.Content, features, maximum: null, allowEmpty: true);
        var (knowledgeTarget, knowledgeConfidence) = PredictSoftmax<KnowledgeTarget>(_layout.KnowledgeTarget, features);
        var (discourseAct, discourseConfidence) = PredictSoftmax<DiscourseAct>(_layout.DiscourseAct, features);
        var (discourseSubject, subjectConfidence) = PredictSoftmax<DialogueParticipant>(_layout.DiscourseSubject, features);
        var (discourseTarget, targetConfidence) = PredictSoftmax<DialogueParticipant>(_layout.DiscourseTarget, features);
        var (factIndex, factConfidence) = PredictSoftmaxIndex(_layout.FactKind,
            Enum.GetValues<DialogueFactKind>().Length + 1, features);
        var (polarityIndex, polarityConfidence) = PredictSoftmaxIndex(_layout.FactPolarity, 2, features);
        var (toolIndex, toolConfidence) = PredictToolIndex(features);
        var (policy, policyConfidence) = PredictSoftmax<ResponsePolicy>(_layout.Policy, features);
        var (candidateIndex, candidateConfidence) = PredictSoftmaxIndex(_layout.Candidate, _candidates.Length, features);
        var learnedSlots = PredictSlots(currentInput ?? input);
        var factValueSpan = PredictFactSpan(currentInput ?? input);
        var slots = preservedSlots.Count == 0
            ? learnedSlots
            : preservedSlots.Concat(learnedSlots)
                .DistinctBy(slot => (slot.Type, slot.Start, slot.Length))
                .OrderBy(slot => slot.Start).ThenBy(slot => slot.Type).ToArray();
        var confidence = new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["SPEECH_ACT"] = speech.Confidence,
            ["DOMAIN"] = domains.Confidence,
            ["GOAL"] = goals.Confidence,
            ["AFFECT"] = affectConfidence,
            ["STANCE"] = stanceConfidence,
            ["POLICY"] = policyConfidence,
            ["SLOTS"] = learnedSlots.Count == 0 ? PredictSlotConfidence(input) : learnedSlots.Min(slot => slot.Confidence),
            ["CONTENT"] = content.Confidence,
            ["KNOWLEDGE_TARGET"] = knowledgeConfidence,
            ["TOOL"] = toolConfidence,
            ["RESPONSE_CANDIDATE"] = candidateConfidence,
            ["DISCOURSE_ACT"] = discourseConfidence,
            ["DISCOURSE_SUBJECT"] = subjectConfidence,
            ["DISCOURSE_TARGET"] = targetConfidence,
            ["FACT_KIND"] = factConfidence,
            ["FACT_POLARITY"] = polarityConfidence,
            ["FACT_SPAN"] = factValueSpan.Confidence
        });
        var factKind = factIndex == 0 ? null : (DialogueFactKind?)(factIndex - 1);
        var antecedent = PredictAntecedent(
            currentInput ?? input,
            utterances ?? [],
            discourseAct,
            contextVector);
        var discourse = new DiscourseFrame(discourseAct, discourseSubject, discourseTarget,
            factKind, factValueSpan.Span, polarityIndex == 1, antecedent.Sequence, discourseConfidence,
            antecedent.Sequence is null ? "LEARNED_NO_ANTECEDENT" : "LEARNED_ANTECEDENT_POINTER");
        return new StructuredPerception(speech.Values, domains.Values, goals.Values, affect, stance, policy,
            slots, content.Values, _tools[toolIndex] == "NONE" ? null : _tools[toolIndex],
            _candidates[candidateIndex], knowledgeTarget, confidence, discourse);
    }

    public StructuredMetrics Evaluate(
        IReadOnlyList<TrainingExample> examples,
        Func<TrainingExample, IReadOnlyList<double>>? context = null)
    {
        var predictions = examples.Select(example => Predict(
            example.Context, [], context?.Invoke(example), example.Input, example.Turns)).ToArray();
        return EvaluatePredictions(examples, predictions,
            CandidateTopKAccuracy(examples, 3, context),
            CandidateTopKAccuracy(examples, 10, context), CandidateMeanReciprocalRank(examples, context));
    }

    public StructuredMetrics EvaluateWithPredictions(
        IReadOnlyList<TrainingExample> examples,
        IReadOnlyList<StructuredPerception> predictions,
        Func<TrainingExample, IReadOnlyList<double>>? context = null) =>
        EvaluatePredictions(examples, predictions,
            CandidateTopKAccuracy(examples, 3, context),
            CandidateTopKAccuracy(examples, 10, context), CandidateMeanReciprocalRank(examples, context));

    public Dictionary<string, ModelSchemas.ConfidenceThreshold> Calibrate(
        IReadOnlyList<TrainingExample> examples,
        Func<TrainingExample, IReadOnlyList<double>>? context = null)
    {
        var result = ModelSchemas.DefaultCalibration;
        CalibrateMulti("speechActs", _layout.Speech, example => example.SpeechActs.Select(value => (int)value).ToHashSet());
        CalibrateMulti("domains", _layout.Domain, example => example.Domains.Select(value => (int)value).ToHashSet());
        CalibrateMulti("goals", _layout.Goal, example => example.Goals.Select(value => (int)value).ToHashSet());
        CalibrateMulti("content", _layout.Content, example => example.ContentFlags.Select(value => (int)value).ToHashSet());
        CalibrateToolNoneMargin();
        var predictions = examples.Select(example =>
            Predict(example.Context, [], context?.Invoke(example), example.Input, example.Turns)).ToArray();
        CalibrateHead("speechActs", "SPEECH_ACT", example =>
            SetEqual(example.Example.SpeechActs, predictions[example.Index].SpeechActs));
        CalibrateHead("domains", "DOMAIN", example => SetEqual(example.Example.Domains, predictions[example.Index].Domains));
        CalibrateHead("goals", "GOAL", example => SetEqual(example.Example.Goals, predictions[example.Index].Goals));
        CalibrateHead("affect", "AFFECT", example => example.Example.Affect == predictions[example.Index].Affect);
        CalibrateHead("stance", "STANCE", example => example.Example.Stance == predictions[example.Index].Stance);
        CalibrateHead("policy", "POLICY", example => example.Example.Policy == predictions[example.Index].Policy);
        CalibrateHead("content", "CONTENT", example => SetEqual(example.Example.ContentFlags, predictions[example.Index].ContentFlags));
        CalibrateHead("knowledgeTarget", "KNOWLEDGE_TARGET",
            example => example.Example.KnowledgeTarget == predictions[example.Index].KnowledgeTarget);
        CalibrateHead("slots", "SLOTS", example => SetEqual(
            example.Example.Slots.Select(slot => (slot.Type, slot.Start, slot.Length)),
            predictions[example.Index].Slots.Select(slot => (slot.Type, slot.Start, slot.Length))));
        CalibrateHead("responseCandidate", "RESPONSE_CANDIDATE",
            example => example.Example.ResponseCandidateId == predictions[example.Index].ResponseCandidateId);
        return result;

        void CalibrateToolNoneMargin()
        {
            var supervised = examples.Where(example => example.SupervisedHeads.Contains("tool")).ToArray();
            if (supervised.Length == 0 || _tools.Length == 1) return;
            var noneIndex = Array.IndexOf(_tools, "NONE");
            var scored = supervised.Select(example =>
            {
                var probabilities = Softmax(_layout.Tool, _tools.Length,
                    Features(example.Context, context?.Invoke(example)));
                var bestReal = Enumerable.Range(0, _tools.Length)
                    .Where(index => index != noneIndex)
                    .OrderByDescending(index => probabilities[index])
                    .ThenBy(index => index)
                    .First();
                return (Expected: example.ToolSchema, Probabilities: probabilities, BestReal: bestReal);
            }).ToArray();
            var candidates = Enumerable.Range(0, 101).Select(value => value * 0.005)
                .Select(margin =>
                {
                    var predictions = scored.Select(item =>
                    {
                        var best = Array.IndexOf(item.Probabilities, item.Probabilities.Max());
                        if (best == noneIndex &&
                            item.Probabilities[noneIndex] - item.Probabilities[item.BestReal] <= margin)
                            best = item.BestReal;
                        return (item.Expected, Predicted: _tools[best]);
                    }).ToArray();
                    var mutating = predictions.Where(item => item.Predicted is "BUY" or "SELL").ToArray();
                    var mutatingPrecision = mutating.Length == 0
                        ? 1.0
                        : (double)mutating.Count(item => item.Expected == item.Predicted) / mutating.Length;
                    var accuracy = (double)predictions.Count(item => item.Expected == item.Predicted) /
                        predictions.Length;
                    return (Margin: margin, Accuracy: accuracy, MutatingPrecision: mutatingPrecision);
                })
                .ToArray();
            _labelThresholds[ToolNoneMarginKey] = SelectToolNoneMargin(candidates);
        }

        void CalibrateMulti(
            string head, int offset, Func<TrainingExample, IReadOnlySet<int>> expectedLabels)
        {
            var supervised = examples.Where(example => example.SupervisedHeads.Contains(head)).ToArray();
            if (supervised.Length == 0) return;
            var classes = head switch
            {
                "speechActs" => Enum.GetValues<SpeechAct>().Length,
                "domains" => Enum.GetValues<DialogueDomain>().Length,
                "goals" => Enum.GetValues<DialogueGoal>().Length,
                "content" => Enum.GetValues<ContentFlag>().Length,
                _ => throw new ArgumentOutOfRangeException(nameof(head))
            };
            for (var label = 0; label < classes; label++)
            {
                var scored = supervised.Select(example =>
                {
                    var features = Features(example.Context, context?.Invoke(example));
                    return (Probability: Sigmoid(Dot(offset + label * FeatureCount, features)),
                        Positive: expectedLabels(example).Contains(label));
                }).ToArray();
                var selected = Enumerable.Range(5, 91).Select(value => value / 100.0)
                    .Select(threshold =>
                    {
                        var accepted = scored.Where(item => item.Probability >= threshold).ToArray();
                        var precision = accepted.Length == 0 ? 0.0 :
                            (double)accepted.Count(item => item.Positive) / accepted.Length;
                        var recall = scored.Count(item => item.Positive) == 0 ? 1.0 :
                            (double)accepted.Count(item => item.Positive) / scored.Count(item => item.Positive);
                        var f1 = precision + recall == 0.0 ? 0.0 : 2.0 * precision * recall / (precision + recall);
                        return (Threshold: threshold, Precision: precision, Recall: recall, F1: f1,
                            Accepted: accepted.Length);
                    })
                    .Where(item => item.Accepted > 0)
                    .OrderByDescending(item => item.F1)
                    .ThenByDescending(item => item.Precision)
                    .ThenByDescending(item => item.Recall)
                    .ThenBy(item => item.Threshold).FirstOrDefault();
                if (selected.Accepted > 0) _labelThresholds[LabelKey(head, label)] = selected.Threshold;
            }
        }

        void CalibrateHead(
            string schemaName, string confidenceName,
            Func<(TrainingExample Example, int Index), bool> correct)
        {
            var scored = examples.Select((example, index) => (Example: example, Index: index))
                .Where(item => item.Example.SupervisedHeads.Contains(schemaName) &&
                               predictions[item.Index].Confidence.ContainsKey(confidenceName))
                .Select(item => (Confidence: predictions[item.Index].Confidence[confidenceName], Correct: correct(item)))
                .ToArray();
            if (scored.Length == 0) return;
            var selected = Enumerable.Range(0, 50).Select(index => 0.50 + index * 0.01)
                .Select(threshold =>
                {
                    var accepted = scored.Where(item => item.Confidence >= threshold).ToArray();
                    var precision = accepted.Length == 0 ? 0.0 : (double)accepted.Count(item => item.Correct) / accepted.Length;
                    return (Threshold: threshold, Precision: precision, Coverage: (double)accepted.Length / scored.Length);
                })
                .Where(item => item.Coverage >= 0.25)
                .OrderByDescending(item => item.Precision >= 0.95)
                .ThenByDescending(item => item.Precision)
                .ThenByDescending(item => item.Coverage)
                .ThenBy(item => item.Threshold)
                .FirstOrDefault();
            if (selected.Coverage > 0)
                result[schemaName] = new ModelSchemas.ConfidenceThreshold(selected.Threshold,
                    result[schemaName].Margin);
        }

        static bool SetEqual<T>(IEnumerable<T> left, IEnumerable<T> right) =>
            left.ToHashSet().SetEquals(right);
    }

    internal static StructuredMetrics EvaluatePredictions(
        IReadOnlyList<TrainingExample> examples,
        IReadOnlyList<StructuredPerception> predictions,
        double? responseTop3 = null,
        double? responseTop10 = null,
        double? responseMrr = null)
    {
        if (examples.Count != predictions.Count)
            throw new ArgumentException("Prediction count does not match example count.", nameof(predictions));
        var speech = MultiLabelMacroF1(examples, predictions, "speechActs", example => example.SpeechActs,
            prediction => prediction.SpeechActs, Enum.GetValues<SpeechAct>());
        var domains = MultiLabelMacroF1(examples, predictions, "domains", example => example.Domains,
            prediction => prediction.Domains, Enum.GetValues<DialogueDomain>());
        var goals = MultiLabelMacroF1(examples, predictions, "goals", example => example.Goals,
            prediction => prediction.Goals, Enum.GetValues<DialogueGoal>());
        var affect = Accuracy(examples, predictions, "affect", example => example.Affect, prediction => prediction.Affect);
        var stance = Accuracy(examples, predictions, "stance", example => example.Stance, prediction => prediction.Stance);
        var policy = Accuracy(examples, predictions, "policy", example => example.Policy, prediction => prediction.Policy);
        var content = MultiLabelMacroF1(examples, predictions, "content", example => example.ContentFlags,
            prediction => prediction.ContentFlags, Enum.GetValues<ContentFlag>());
        var slots = SlotSpanF1(examples, predictions);
        var tool = Accuracy(examples, predictions, "tool", example => example.ToolSchema,
            prediction => prediction.ToolSchema ?? "NONE");
        var knowledge = Accuracy(examples, predictions, "knowledgeTarget", example => example.KnowledgeTarget,
            prediction => prediction.KnowledgeTarget);
        var mutatingToolPrecision = ToolPrecision(examples, predictions, ["BUY", "SELL"]);
        var candidate = Accuracy(examples, predictions, "responseCandidate", example => example.ResponseCandidateId,
            prediction => prediction.ResponseCandidateId ?? "");
        var candidateTop3 = responseTop3 ?? candidate;
        var candidateTop10 = responseTop10 ?? candidateTop3;
        var candidateMrr = responseMrr ?? candidate;
        var discourseAct = Accuracy(examples, predictions, "discourseAct", example => example.Discourse.Act,
            prediction => prediction.Discourse?.Act ?? DiscourseAct.None);
        var speaker = SubsetAccuracy(examples, predictions, "discourseSubject",
            example => example.Discourse.Act != DiscourseAct.None,
            example => example.Discourse.Subject,
            prediction => prediction.Discourse?.Subject ?? DialogueParticipant.None);
        var fact = FactSpanF1(examples, predictions);
        var correction = CorrectionStateAccuracy(examples, predictions);
        var antecedent = SubsetAccuracy(examples, predictions, "antecedent",
            example => example.Turns.Length > 1 && example.Discourse.Act is
                DiscourseAct.AskExplanation or DiscourseAct.ReferBack or
                DiscourseAct.Correct or DiscourseAct.RejectAssumption,
            example => example.Discourse.AntecedentUtterance,
            prediction => prediction.Discourse?.AntecedentUtterance);
        var composite = new[] { speech, domains, goals, affect, policy, content, slots, tool, knowledge, candidate,
            discourseAct, speaker, fact, correction, antecedent }
            .Where(double.IsFinite).DefaultIfEmpty(0.0).Average();
        return new StructuredMetrics(speech, domains, goals, affect, stance, policy, content, slots,
            tool, mutatingToolPrecision, knowledge, candidate, candidateTop3,
            candidateTop10, candidateMrr, discourseAct, speaker, fact, antecedent, correction, composite);
    }

    private double TrainAntecedent(
        TrainingExample example,
        double learningRate,
        IReadOnlyList<double>? contextVector,
        bool contextOnly = false)
    {
        var candidates = example.Turns.Take(Math.Max(0, example.Turns.Length - 1)).TakeLast(8).ToArray();
        var featureRows = candidates.Select(candidate => AntecedentFeatures(
                example.Input,
                candidate,
                example.Turns[^1].Sequence,
                example.Discourse.Act,
                contextVector))
            .Append(NoAntecedentFeatures(example.Input, example.Discourse.Act, contextVector))
            .ToArray();
        var scores = featureRows.Select(features => Dot(_layout.Antecedent, features)).ToArray();
        var probabilities = Softmax(scores);
        var target = Array.FindIndex(candidates,
            candidate => candidate.Sequence == example.Discourse.AntecedentUtterance);
        if (target < 0)
        {
            target = candidates.Length;
        }

        for (var candidateIndex = 0; candidateIndex < featureRows.Length; candidateIndex++)
        {
            var scale = learningRate * ((candidateIndex == target ? 1.0 : 0.0) - probabilities[candidateIndex]);
            var features = featureRows[candidateIndex];
            var start = contextOnly ? LexicalFeatureCount : 0;
            for (var index = start; index < FeatureCount; index++)
                _weights[_layout.Antecedent + index] += scale * features[index];
        }

        return -Math.Log(Math.Max(1e-12, probabilities[target]));
    }

    private (long? Sequence, double Confidence) PredictAntecedent(
        string currentInput,
        IReadOnlyList<DialogueUtterance> utterances,
        DiscourseAct discourseAct,
        IReadOnlyList<double>? contextVector)
    {
        if (utterances.Count < 2)
            return (null, 1.0);
        var currentSequence = utterances[^1].Sequence;
        var candidates = utterances.Take(utterances.Count - 1).TakeLast(8).ToArray();
        var scores = candidates.Select(candidate => Dot(_layout.Antecedent,
                AntecedentFeatures(currentInput, candidate, currentSequence, discourseAct, contextVector)))
            .Append(Dot(_layout.Antecedent, NoAntecedentFeatures(currentInput, discourseAct, contextVector)))
            .ToArray();
        var probabilities = Softmax(scores);
        var selected = Enumerable.Range(0, probabilities.Length)
            .OrderByDescending(index => probabilities[index])
            .ThenByDescending(index => index < candidates.Length ? candidates[index].Sequence : long.MinValue)
            .First();
        return selected == candidates.Length
            ? (null, probabilities[selected])
            : (candidates[selected].Sequence, probabilities[selected]);
    }

    private static double[] AntecedentFeatures(
        string currentInput,
        DialogueUtterance candidate,
        long currentSequence,
        DiscourseAct discourseAct,
        IReadOnlyList<double>? contextVector)
    {
        var speaker = candidate.Speaker == DialogueRole.Npc ? "NPC" : "PLAYER";
        var distance = Math.Clamp(currentSequence - candidate.Sequence, 0, 8);
        var currentWords = Words(currentInput);
        var candidateWords = Words(candidate.Text);
        var overlap = Math.Min(8, currentWords.Intersect(candidateWords).Count());
        var features = Features(
            $"CURRENT {currentInput} CANDIDATE {speaker} {candidate.Text} " +
            $"DISTANCE-{distance} OVERLAP-{overlap} ACT-{discourseAct}",
            contextVector);
        InteractContext(features, $"{speaker}|{distance}|{overlap}");
        return features;

        static HashSet<string> Words(string text) => Tokenizer.Lex(DialogueText.Normalize(text))
            .Where(token => token.Kind == LexicalTokenKind.Word)
            .Select(token => token.Text)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static double[] NoAntecedentFeatures(
        string currentInput,
        DiscourseAct discourseAct,
        IReadOnlyList<double>? contextVector)
    {
        var features = Features($"CURRENT {currentInput} CANDIDATE NONE ACT-{discourseAct}", contextVector);
        InteractContext(features, "NONE");
        return features;
    }

    private static void InteractContext(double[] features, string candidateClass)
    {
        for (var index = 0; index < ContextFeatureCount; index++)
        {
            if ((StableHash($"POINTER:{candidateClass}:{index}") & 1) != 0)
            {
                features[LexicalFeatureCount + index] = -features[LexicalFeatureCount + index];
            }
        }
    }

    private static double[] Softmax(IReadOnlyList<double> scores)
    {
        var maximum = scores.Max();
        var result = scores.Select(score => Math.Exp(score - maximum)).ToArray();
        var sum = result.Sum();
        for (var index = 0; index < result.Length; index++)
        {
            result[index] /= sum;
        }

        return result;
    }

    private double TrainSlots(TrainingExample example, double learningRate)
    {
        var tokens = Tokenizer.Lex(DialogueText.Normalize(example.Input)).Where(token => token.Kind == LexicalTokenKind.Word).ToArray();
        if (tokens.Length == 0) return 0.0;
        var cursor = 0;
        var loss = 0.0;
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            var start = example.Input.IndexOf(token.Text, cursor, StringComparison.Ordinal);
            cursor = Math.Max(cursor, start + token.Text.Length);
            var target = 0;
            var slot = example.Slots.FirstOrDefault(candidate => start >= candidate.Start && start < candidate.Start + candidate.Length);
            if (slot is not null) target = 1 + (int)slot.Type * 2 + (start == slot.Start ? 0 : 1);
            loss += TrainSoftmax(_layout.Slot, SlotClassCount,
                TokenFeatures(token.Text, index > 0 ? tokens[index - 1].Text : "<START>",
                    index + 1 < tokens.Length ? tokens[index + 1].Text : "<END>",
                    index > 1 ? tokens[index - 2].Text : "<START2>",
                    index + 2 < tokens.Length ? tokens[index + 2].Text : "<END2>"), target, learningRate,
                target == 0 ? 1.0 : SlotPositiveWeight);
        }
        return loss / tokens.Length;
    }

    private double TrainFactSpan(TrainingExample example, double learningRate)
    {
        var normalized = DialogueText.Normalize(example.Input);
        var words = Tokenizer.Lex(normalized)
            .Where(token => token.Kind == LexicalTokenKind.Word)
            .ToArray();
        if (words.Length == 0)
        {
            return 0.0;
        }

        var span = example.Discourse.FactValueSpan;
        var cursor = 0;
        var loss = 0.0;
        for (var index = 0; index < words.Length; index++)
        {
            var token = words[index];
            var start = normalized.IndexOf(token.Text, cursor, StringComparison.Ordinal);
            cursor = Math.Max(cursor, start + token.Text.Length);
            var target = span is not null && start >= span.Start && start < span.Start + span.Length
                ? start == span.Start ? 1 : 2
                : 0;
            loss += TrainSoftmax(
                _layout.FactSpan,
                FactSpanClassCount,
                TokenFeatures(
                    token.Text,
                    index > 0 ? words[index - 1].Text : "<START>",
                    index + 1 < words.Length ? words[index + 1].Text : "<END>",
                    index > 1 ? words[index - 2].Text : "<START2>",
                    index + 2 < words.Length ? words[index + 2].Text : "<END2>"),
                target,
                learningRate,
                target == 0 ? 1.0 : MaximumPositiveWeight);
        }

        return loss / words.Length;
    }

    private (DialogueTextSpan? Span, double Confidence) PredictFactSpan(string input)
    {
        var normalized = DialogueText.Normalize(input);
        var words = Tokenizer.Lex(normalized)
            .Where(token => token.Kind == LexicalTokenKind.Word)
            .ToArray();
        var positioned = new List<(int Start, int Length, int Class, double Confidence)>();
        var cursor = 0;
        for (var index = 0; index < words.Length; index++)
        {
            var word = words[index].Text;
            var tokenStart = normalized.IndexOf(word, cursor, StringComparison.Ordinal);
            if (tokenStart < 0)
            {
                continue;
            }

            cursor = tokenStart + word.Length;
            var prediction = PredictSoftmaxIndex(
                _layout.FactSpan,
                FactSpanClassCount,
                TokenFeatures(
                    word,
                    index > 0 ? words[index - 1].Text : "<START>",
                    index + 1 < words.Length ? words[index + 1].Text : "<END>",
                    index > 1 ? words[index - 2].Text : "<START2>",
                    index + 2 < words.Length ? words[index + 2].Text : "<END2>"));
            positioned.Add((tokenStart, word.Length, prediction.Index, prediction.Confidence));
        }

        var beginning = positioned.FindIndex(token => token.Class == 1);
        if (beginning < 0)
        {
            return (null, positioned.Count == 0 ? 1.0 : positioned.Average(token => token.Confidence));
        }

        var endIndex = beginning;
        while (endIndex + 1 < positioned.Count && positioned[endIndex + 1].Class == 2)
        {
            endIndex++;
        }

        var factStart = positioned[beginning].Start;
        var end = positioned[endIndex].Start + positioned[endIndex].Length;
        var value = normalized[factStart..end];
        var confidence = positioned.Skip(beginning).Take(endIndex - beginning + 1)
            .Min(token => token.Confidence);
        return (new DialogueTextSpan(value, factStart, end - factStart), confidence);
    }

    private IReadOnlyList<DialogueSlot> PredictSlots(string input)
    {
        var normalized = DialogueText.Normalize(input);
        var words = Tokenizer.Lex(normalized).Where(token => token.Kind == LexicalTokenKind.Word).ToArray();
        var positioned = new List<(string Text, int Start, int Class, double Confidence)>();
        var cursor = 0;
        for (var index = 0; index < words.Length; index++)
        {
            var word = words[index].Text;
            var start = normalized.IndexOf(word, cursor, StringComparison.Ordinal);
            if (start < 0) continue;
            cursor = start + word.Length;
            var prediction = PredictSoftmaxIndex(_layout.Slot, SlotClassCount,
                TokenFeatures(word, index > 0 ? words[index - 1].Text : "<START>",
                    index + 1 < words.Length ? words[index + 1].Text : "<END>",
                    index > 1 ? words[index - 2].Text : "<START2>",
                    index + 2 < words.Length ? words[index + 2].Text : "<END2>"));
            positioned.Add((word, start, prediction.Index, prediction.Confidence));
        }

        var result = new List<DialogueSlot>();
        for (var index = 0; index < positioned.Count; index++)
        {
            var current = positioned[index];
            if (current.Class == 0) continue;
            var type = (SlotType)((current.Class - 1) / 2);
            var isBeginning = (current.Class - 1) % 2 == 0;
            if (!isBeginning && result.Count > 0 && result[^1].Type == type &&
                result[^1].Start + result[^1].Length <= current.Start &&
                normalized.AsSpan(result[^1].Start + result[^1].Length,
                    current.Start - result[^1].Start - result[^1].Length).Trim().Length == 0)
            {
                var prior = result[^1];
                var end = current.Start + current.Text.Length;
                result[^1] = prior with
                {
                    Value = normalized[prior.Start..end],
                    Length = end - prior.Start,
                    Confidence = Math.Min(prior.Confidence, current.Confidence)
                };
                continue;
            }
            result.Add(new DialogueSlot(type, BioTag.B, current.Text, current.Start,
                current.Text.Length, current.Confidence));
        }
        return result;
    }

    private double PredictSlotConfidence(string input)
    {
        var words = Tokenizer.Lex(DialogueText.Normalize(input)).Where(token => token.Kind == LexicalTokenKind.Word).ToArray();
        if (words.Length == 0) return 1.0;
        return words.Select((word, index) => PredictSoftmaxIndex(_layout.Slot, SlotClassCount,
            TokenFeatures(word.Text, index > 0 ? words[index - 1].Text : "<START>",
                index + 1 < words.Length ? words[index + 1].Text : "<END>",
                index > 1 ? words[index - 2].Text : "<START2>",
                index + 2 < words.Length ? words[index + 2].Text : "<END2>")).Confidence).Average();
    }

    private double TrainMulti(
        int offset, int classes, double[] features, IReadOnlySet<int> targets, double rate,
        double positiveWeight = 2.0, IReadOnlyList<double>? positiveWeights = null,
        bool contextOnly = false)
    {
        var loss = 0.0;
        for (var label = 0; label < classes; label++)
        {
            var probability = Sigmoid(Dot(offset + label * FeatureCount, features));
            var target = targets.Contains(label) ? 1.0 : 0.0;
            var weight = target == 1.0
                ? Math.Min(MaximumPositiveWeight, positiveWeights?[label] ?? positiveWeight)
                : 1.0;
            loss -= weight * (target * Math.Log(Math.Max(1e-12, probability)) +
                    (1.0 - target) * Math.Log(Math.Max(1e-12, 1.0 - probability)));
            UpdateSelected(offset + label * FeatureCount, features, rate * weight * (target - probability), contextOnly);
        }
        return loss / classes;
    }

    private double TrainSoftmax(
        int offset, int classes, double[] features, int target, double rate, double targetWeight = 1.0,
        bool contextOnly = false)
    {
        var probabilities = Softmax(offset, classes, features);
        var loss = -targetWeight * Math.Log(Math.Max(1e-12, probabilities[target]));
        for (var label = 0; label < classes; label++)
            UpdateSelected(offset + label * FeatureCount, features,
                rate * targetWeight * ((label == target ? 1.0 : 0.0) - probabilities[label]), contextOnly);
        return loss;
    }

    private (T[] Values, double Confidence) PredictMulti<T>(
        string head, int offset, double[] features, int? maximum, bool allowEmpty = false)
        where T : struct, Enum
    {
        var classes = Enum.GetValues<T>().Length;
        var values = Enumerable.Range(0, classes).Select(label => Sigmoid(Dot(offset + label * FeatureCount, features))).ToArray();
        var selected = Enumerable.Range(0, classes)
            .Where(label => values[label] >= _labelThresholds[LabelKey(head, label)])
            .OrderByDescending(label => values[label]).ThenBy(label => label).ToList();
        if (selected.Count > 2)
            selected = selected.Where((label, rank) => rank < 2 ||
                values[label] >= Math.Min(0.99, _labelThresholds[LabelKey(head, label)] + 0.15)).ToList();
        if (maximum is not null) selected = selected.Take(maximum.Value).ToList();
        if (selected.Count == 0 && !allowEmpty) selected = [Array.IndexOf(values, values.Max())];
        return (selected.Select(label => (T)Enum.ToObject(typeof(T), label)).ToArray(),
            selected.Count == 0 ? 1.0 - values.Max() : selected.Min(label => values[label]));
    }

    private (T Value, double Confidence) PredictSoftmax<T>(int offset, double[] features) where T : struct, Enum
    {
        var result = PredictSoftmaxIndex(offset, Enum.GetValues<T>().Length, features);
        return ((T)Enum.ToObject(typeof(T), result.Index), result.Confidence);
    }

    private (int Index, double Confidence) PredictSoftmaxIndex(int offset, int classes, double[] features)
    {
        var probabilities = Softmax(offset, classes, features);
        var index = Array.IndexOf(probabilities, probabilities.Max());
        return (index, probabilities[index]);
    }

    private (int Index, double Confidence) PredictToolIndex(double[] features)
    {
        var probabilities = Softmax(_layout.Tool, _tools.Length, features);
        if (_tools.Length == 1) return (0, probabilities[0]);
        var index = Array.IndexOf(probabilities, probabilities.Max());
        var noneIndex = Array.IndexOf(_tools, "NONE");
        if (index == noneIndex)
        {
            var bestReal = Enumerable.Range(0, _tools.Length)
                .Where(candidate => candidate != noneIndex)
                .OrderByDescending(candidate => probabilities[candidate])
                .ThenBy(candidate => candidate)
                .First();
            if (probabilities[noneIndex] - probabilities[bestReal] <= _labelThresholds[ToolNoneMarginKey])
            {
                index = bestReal;
            }
        }

        return (index, probabilities[index]);
    }

    private double[] Softmax(int offset, int classes, double[] features)
    {
        var logits = Enumerable.Range(0, classes).Select(label => Dot(offset + label * FeatureCount, features)).ToArray();
        var maximum = logits.Max();
        var values = logits.Select(value => Math.Exp(value - maximum)).ToArray();
        var sum = values.Sum();
        for (var index = 0; index < values.Length; index++) values[index] /= sum;
        return values;
    }

    private static double[] Features(string text, IReadOnlyList<double>? context = null)
    {
        var result = new double[FeatureCount];
        result[0] = 1.0;
        var words = Tokenizer.Lex(DialogueText.Normalize(text)).Where(token => token.Kind == LexicalTokenKind.Word)
            .Select(token => token.Text).ToArray();
        foreach (var word in words) result[1 + StableHash(word) % (LexicalFeatureCount - 1)] += 1.0;
        for (var index = 1; index < words.Length; index++)
            result[1 + StableHash(words[index - 1] + "_" + words[index]) % (LexicalFeatureCount - 1)] += 0.7;
        if (context is not null)
            for (var index = 0; index < Math.Min(ContextFeatureCount, context.Count); index++)
                result[LexicalFeatureCount + index] = context[index] * ContextFeatureScale;
        var norm = Math.Sqrt(result.Sum(value => value * value));
        for (var index = 0; index < result.Length; index++) result[index] /= Math.Max(1.0, norm);
        return result;
    }

    private static double[] TokenFeatures(
        string word, string previous, string next, string previous2, string next2)
    {
        var result = new double[FeatureCount];
        result[0] = 1.0;
        result[1 + StableHash(word) % (LexicalFeatureCount - 1)] = 1.0;
        if (word.Length >= 3) result[1 + StableHash(word[..3]) % (LexicalFeatureCount - 1)] += 0.5;
        result[1 + StableHash("P:" + previous) % (LexicalFeatureCount - 1)] += 0.8;
        result[1 + StableHash("N:" + next) % (LexicalFeatureCount - 1)] += 0.5;
        result[1 + StableHash(previous + ">" + word) % (LexicalFeatureCount - 1)] += 0.7;
        result[1 + StableHash("P2:" + previous2) % (LexicalFeatureCount - 1)] += 0.5;
        result[1 + StableHash("N2:" + next2) % (LexicalFeatureCount - 1)] += 0.3;
        result[1 + StableHash(previous2 + ">" + previous + ">" + word) % (LexicalFeatureCount - 1)] += 0.6;
        return result;
    }

    private double Dot(int offset, double[] features)
    {
        var result = 0.0;
        foreach (var index in SparseIndices(features))
        {
            result += _weights[offset + index] * features[index];
        }

        return result;
    }

    private void Update(int offset, double[] features, double scale)
    {
        foreach (var index in SparseIndices(features))
        {
            _weights[offset + index] += scale * features[index];
        }
    }

    private void UpdateSelected(int offset, double[] features, double scale, bool contextOnly)
    {
        if (!contextOnly)
        {
            Update(offset, features, scale);
            return;
        }

        foreach (var index in SparseIndices(features))
        {
            if (index >= LexicalFeatureCount)
            {
                _weights[offset + index] += scale * features[index];
            }
        }
    }

    private static int[] SparseIndices(double[] features) => SparseFeatureCache
        .GetValue(features, static values => new SparseFeatureIndices(Enumerable.Range(0, values.Length)
            .Where(index => values[index] != 0.0)
            .ToArray()))
        .Indices;

    private sealed record SparseFeatureIndices(int[] Indices);

    private static double Sigmoid(double value) => value >= 0
        ? 1.0 / (1.0 + Math.Exp(-value))
        : Math.Exp(value) / (1.0 + Math.Exp(value));

    private static int StableHash(string text)
    {
        uint hash = 2166136261;
        foreach (var character in text) hash = (hash ^ character) * 16777619;
        return (int)(hash & 0x7fffffff);
    }

    private static string LabelKey(string head, int label) => head + ":" + label;

    private static Dictionary<string, double> DefaultLabelThresholds()
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        Add("speechActs", Enum.GetValues<SpeechAct>().Length);
        Add("domains", Enum.GetValues<DialogueDomain>().Length);
        Add("goals", Enum.GetValues<DialogueGoal>().Length);
        Add("content", Enum.GetValues<ContentFlag>().Length);
        result[ToolNoneMarginKey] = 0.0;
        return result;
        void Add(string head, int count)
        {
            for (var label = 0; label < count; label++) result[LabelKey(head, label)] = 0.50;
        }
    }

    private sealed class Layout
    {
        public Layout(int tools, int candidates)
        {
            Speech = 0;
            Domain = Speech + Enum.GetValues<SpeechAct>().Length * FeatureCount;
            Goal = Domain + Enum.GetValues<DialogueDomain>().Length * FeatureCount;
            Affect = Goal + Enum.GetValues<DialogueGoal>().Length * FeatureCount;
            Stance = Affect + Enum.GetValues<UserAffect>().Length * FeatureCount;
            Policy = Stance + Enum.GetValues<DialogueStance>().Length * FeatureCount;
            Content = Policy + Enum.GetValues<ResponsePolicy>().Length * FeatureCount;
            Slot = Content + Enum.GetValues<ContentFlag>().Length * FeatureCount;
            KnowledgeTarget = Slot + SlotClassCount * FeatureCount;
            DiscourseAct = KnowledgeTarget + Enum.GetValues<KnowledgeTarget>().Length * FeatureCount;
            DiscourseSubject = DiscourseAct + Enum.GetValues<DiscourseAct>().Length * FeatureCount;
            DiscourseTarget = DiscourseSubject + Enum.GetValues<DialogueParticipant>().Length * FeatureCount;
            FactKind = DiscourseTarget + Enum.GetValues<DialogueParticipant>().Length * FeatureCount;
            FactPolarity = FactKind + (Enum.GetValues<DialogueFactKind>().Length + 1) * FeatureCount;
            FactSpan = FactPolarity + 2 * FeatureCount;
            Antecedent = FactSpan + FactSpanClassCount * FeatureCount;
            Tool = Antecedent + FeatureCount;
            Candidate = Tool + tools * FeatureCount;
            WeightCount = Candidate + candidates * FeatureCount;
        }
        public int Speech { get; }
        public int Domain { get; }
        public int Goal { get; }
        public int Affect { get; }
        public int Stance { get; }
        public int Policy { get; }
        public int Content { get; }
        public int Slot { get; }
        public int KnowledgeTarget { get; }
        public int DiscourseAct { get; }
        public int DiscourseSubject { get; }
        public int DiscourseTarget { get; }
        public int FactKind { get; }
        public int FactPolarity { get; }
        public int FactSpan { get; }
        public int Antecedent { get; }
        public int Tool { get; }
        public int Candidate { get; }
        public int WeightCount { get; }
    }

}

internal sealed record StructuredMetrics(
    double SpeechActMacroF1,
    double DomainMacroF1,
    double GoalMacroF1,
    double AffectAccuracy,
    double StanceAccuracy,
    double PolicyAccuracy,
    double ContentMacroF1,
    double SlotSpanF1,
    double ToolAccuracy,
    double MutatingToolPrecision,
    double KnowledgeTargetAccuracy,
    double ResponseTop1,
    double ResponseTop3,
    double VariationRecallAt10,
    double VariationMrr,
    double DiscourseActAccuracy,
    double SpeakerAttributionAccuracy,
    double FactSpanF1,
    double AntecedentAccuracy,
    double CorrectionStateAccuracy,
    double Composite);

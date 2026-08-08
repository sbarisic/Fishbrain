using System.Collections.ObjectModel;
using System.Text;

namespace Fishbrain;

internal sealed record V10TrainingExample(
    string Context,
    string Input,
    DialogueTurn[] Turns,
    SpeechAct[] SpeechActs,
    DialogueDomain[] Domains,
    DialogueGoal[] Goals,
    UserAffect Affect,
    DialogueStance Stance,
    ResponsePolicy Policy,
    DialogueSlot[] Slots,
    ContentFlag[] ContentFlags,
    string ToolSchema,
    string ResponseCandidateId,
    KnowledgeTarget KnowledgeTarget,
    string Source,
    string SemanticFamilyId,
    IReadOnlySet<string> SupervisedHeads);

internal sealed class CompositionalHeadModel
{
    private const int LexicalFeatureCount = 512;
    private const int ContextFeatureCount = 128;
    private const int FeatureCount = LexicalFeatureCount + ContextFeatureCount;
    private const int SlotClassCount = 1 + 2 * 14;
    private readonly string[] _tools;
    private readonly string[] _candidates;
    private readonly Layout _layout;
    private readonly double[] _weights;
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
    }

    public int Updates { get; private set; }
    public IReadOnlyList<string> Tools => _tools;
    public IReadOnlyList<string> Candidates => _candidates;
    public int WeightCount => _weights.Length;
    public double[] Snapshot() => (double[])_weights.Clone();
    public Dictionary<string, double> SnapshotLabelThresholds() =>
        new(_labelThresholds, StringComparer.Ordinal);

    public void Restore(
        IReadOnlyList<double> weights, int updates,
        IReadOnlyDictionary<string, double>? labelThresholds = null)
    {
        if (weights.Count != _weights.Length) throw new InvalidDataException("Structured head parameter count mismatch.");
        for (var index = 0; index < weights.Count; index++) _weights[index] = weights[index];
        Updates = updates;
        if (labelThresholds is not null)
        {
            var expected = DefaultLabelThresholds();
            if (labelThresholds.Count != expected.Count || expected.Keys.Any(key => !labelThresholds.ContainsKey(key)) ||
                labelThresholds.Values.Any(value => value is < 0.05 or > 0.99))
                throw new InvalidDataException("Structured per-label calibration does not match the v11 schema.");
            _labelThresholds = new Dictionary<string, double>(labelThresholds, StringComparer.Ordinal);
        }
    }

    public double Train(V10TrainingExample example, double learningRate, IReadOnlyList<double>? contextVector = null)
    {
        var features = Features(example.Context, contextVector);
        var heads = 0;
        var loss = 0.0;
        Add("speechActs", () => TrainMulti(_layout.Speech, Enum.GetValues<SpeechAct>().Length, features,
            example.SpeechActs.Select(value => (int)value).ToHashSet(), learningRate));
        Add("domains", () => TrainMulti(_layout.Domain, Enum.GetValues<DialogueDomain>().Length, features,
            example.Domains.Select(value => (int)value).ToHashSet(), learningRate, 3.0));
        Add("goals", () => TrainMulti(_layout.Goal, Enum.GetValues<DialogueGoal>().Length, features,
            example.Goals.Select(value => (int)value).ToHashSet(), learningRate, 2.5));
        Add("affect", () => TrainSoftmax(_layout.Affect, Enum.GetValues<UserAffect>().Length, features, (int)example.Affect, learningRate));
        Add("stance", () => TrainSoftmax(_layout.Stance, Enum.GetValues<DialogueStance>().Length, features, (int)example.Stance, learningRate));
        Add("policy", () => TrainSoftmax(_layout.Policy, Enum.GetValues<ResponsePolicy>().Length, features, (int)example.Policy, learningRate));
        Add("content", () => TrainMulti(_layout.Content, Enum.GetValues<ContentFlag>().Length, features,
            example.ContentFlags.Select(value => (int)value).ToHashSet(), learningRate, 4.0));
        Add("knowledgeTarget", () => TrainSoftmax(_layout.KnowledgeTarget, Enum.GetValues<KnowledgeTarget>().Length,
            features, (int)example.KnowledgeTarget, learningRate));
        Add("tool", () => TrainSoftmax(_layout.Tool, _tools.Length, features,
            Math.Max(0, Array.IndexOf(_tools, example.ToolSchema)), learningRate));
        Add("responseCandidate", () => TrainSoftmax(_layout.Candidate, _candidates.Length, features,
            Math.Max(0, Array.IndexOf(_candidates, example.ResponseCandidateId)), learningRate));
        Add("slots", () => TrainSlots(example, learningRate * 0.10));
        Updates++;
        return loss / Math.Max(1, heads);

        void Add(string head, Func<double> train)
        {
            if (!example.SupervisedHeads.Contains(head)) return;
            loss += train();
            heads++;
        }
    }

    public double TrainRanking(V10TrainingExample example, double learningRate, IReadOnlyList<double>? contextVector = null)
    {
        if (!example.SupervisedHeads.Contains("responseCandidate")) return 0.0;
        var target = Array.IndexOf(_candidates, example.ResponseCandidateId);
        if (target < 0) return 0.0;
        var features = Features(example.Context, contextVector);
        var scores = Enumerable.Range(0, _candidates.Length)
            .Select(index => Dot(_layout.Candidate + index * FeatureCount, features)).ToArray();
        var negative = Enumerable.Range(0, scores.Length).Where(index => index != target)
            .OrderByDescending(index => scores[index]).ThenBy(index => index).First();
        var difference = Math.Clamp(scores[target] - scores[negative], -30.0, 30.0);
        var probability = 1.0 / (1.0 + Math.Exp(-difference));
        var gradient = 1.0 - probability;
        Update(_layout.Candidate + target * FeatureCount, features, learningRate * gradient);
        Update(_layout.Candidate + negative * FeatureCount, features, -learningRate * gradient);
        Updates++;
        return -Math.Log(Math.Max(1e-12, probability));
    }

    public double TrainSlotsOnly(V10TrainingExample example, double learningRate)
    {
        if (!example.SupervisedHeads.Contains("slots"))
            throw new ArgumentException("The auxiliary slot pass requires slot supervision.", nameof(example));
        return TrainSlots(example, learningRate * 0.10);
    }

    public StructuredPerception Predict(
        string input, IReadOnlyList<DialogueSlot> preservedSlots, IReadOnlyList<double>? contextVector = null,
        string? currentInput = null)
    {
        var features = Features(input, contextVector);
        var speech = PredictMulti<SpeechAct>("speechActs", _layout.Speech, features, maximum: 3);
        var domains = PredictMulti<DialogueDomain>("domains", _layout.Domain, features, maximum: 3);
        var goals = PredictMulti<DialogueGoal>("goals", _layout.Goal, features, maximum: 3);
        var (affect, affectConfidence) = PredictSoftmax<UserAffect>(_layout.Affect, features);
        var (stance, stanceConfidence) = PredictSoftmax<DialogueStance>(_layout.Stance, features);
        var (policy, policyConfidence) = PredictSoftmax<ResponsePolicy>(_layout.Policy, features);
        var content = PredictMulti<ContentFlag>("content", _layout.Content, features, maximum: null, allowEmpty: true);
        var (knowledgeTarget, knowledgeConfidence) = PredictSoftmax<KnowledgeTarget>(_layout.KnowledgeTarget, features);
        var (toolIndex, toolConfidence) = PredictSoftmaxIndex(_layout.Tool, _tools.Length, features);
        var (candidateIndex, candidateConfidence) = PredictSoftmaxIndex(_layout.Candidate, _candidates.Length, features);
        var learnedSlots = PredictSlots(currentInput ?? input);
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
            ["RESPONSE_CANDIDATE"] = candidateConfidence
        });
        return new StructuredPerception(speech.Values, domains.Values, goals.Values, affect, stance, policy,
            slots, content.Values, _tools[toolIndex] == "NONE" ? null : _tools[toolIndex],
            _candidates[candidateIndex], knowledgeTarget, confidence);
    }

    public StructuredMetrics Evaluate(
        IReadOnlyList<V10TrainingExample> examples,
        Func<V10TrainingExample, IReadOnlyList<double>>? context = null)
    {
        var predictions = examples.Select(example => Predict(example.Context, [], context?.Invoke(example), example.Input)).ToArray();
        return EvaluatePredictions(examples, predictions,
            CandidateTopKAccuracy(examples, 3, context),
            CandidateTopKAccuracy(examples, 10, context), CandidateMeanReciprocalRank(examples, context));
    }

    public StructuredMetrics EvaluateWithPredictions(
        IReadOnlyList<V10TrainingExample> examples,
        IReadOnlyList<StructuredPerception> predictions,
        Func<V10TrainingExample, IReadOnlyList<double>>? context = null) =>
        EvaluatePredictions(examples, predictions,
            CandidateTopKAccuracy(examples, 3, context),
            CandidateTopKAccuracy(examples, 10, context), CandidateMeanReciprocalRank(examples, context));

    public Dictionary<string, V11Schemas.ConfidenceThreshold> Calibrate(
        IReadOnlyList<V10TrainingExample> examples,
        Func<V10TrainingExample, IReadOnlyList<double>>? context = null)
    {
        var predictions = examples.Select(example => Predict(example.Context, [], context?.Invoke(example), example.Input)).ToArray();
        var result = V11Schemas.DefaultCalibration;
        CalibrateMulti("speechActs", _layout.Speech, example => example.SpeechActs.Select(value => (int)value).ToHashSet());
        CalibrateMulti("domains", _layout.Domain, example => example.Domains.Select(value => (int)value).ToHashSet());
        CalibrateMulti("goals", _layout.Goal, example => example.Goals.Select(value => (int)value).ToHashSet());
        CalibrateMulti("content", _layout.Content, example => example.ContentFlags.Select(value => (int)value).ToHashSet());
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

        void CalibrateMulti(
            string head, int offset, Func<V10TrainingExample, IReadOnlySet<int>> expectedLabels)
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
                var selected = Enumerable.Range(20, 76).Select(value => value / 100.0)
                    .Select(threshold =>
                    {
                        var accepted = scored.Where(item => item.Probability >= threshold).ToArray();
                        var precision = accepted.Length == 0 ? 0.0 :
                            (double)accepted.Count(item => item.Positive) / accepted.Length;
                        var recall = scored.Count(item => item.Positive) == 0 ? 1.0 :
                            (double)accepted.Count(item => item.Positive) / scored.Count(item => item.Positive);
                        return (Threshold: threshold, Precision: precision, Recall: recall, Accepted: accepted.Length);
                    })
                    .Where(item => item.Accepted > 0)
                    .OrderByDescending(item => item.Precision >= 0.90)
                    .ThenByDescending(item => item.Recall)
                    .ThenByDescending(item => item.Precision)
                    .ThenBy(item => item.Threshold).FirstOrDefault();
                if (selected.Accepted > 0) _labelThresholds[LabelKey(head, label)] = selected.Threshold;
            }
        }

        void CalibrateHead(
            string schemaName, string confidenceName,
            Func<(V10TrainingExample Example, int Index), bool> correct)
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
                result[schemaName] = new V11Schemas.ConfidenceThreshold(selected.Threshold,
                    result[schemaName].Margin);
        }

        static bool SetEqual<T>(IEnumerable<T> left, IEnumerable<T> right) =>
            left.ToHashSet().SetEquals(right);
    }

    internal static StructuredMetrics EvaluatePredictions(
        IReadOnlyList<V10TrainingExample> examples,
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
        var composite = new[] { speech, domains, goals, affect, policy, content, slots, tool, knowledge, candidate }
            .Where(double.IsFinite).DefaultIfEmpty(0.0).Average();
        return new StructuredMetrics(speech, domains, goals, affect, stance, policy, content, slots,
            tool, mutatingToolPrecision, knowledge, candidate, candidateTop3,
            candidateTop10, candidateMrr, composite);
    }

    private double TrainSlots(V10TrainingExample example, double learningRate)
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
                target == 0 ? 1.0 : 4.0);
        }
        return loss / tokens.Length;
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
        double positiveWeight = 2.0)
    {
        var loss = 0.0;
        for (var label = 0; label < classes; label++)
        {
            var probability = Sigmoid(Dot(offset + label * FeatureCount, features));
            var target = targets.Contains(label) ? 1.0 : 0.0;
            var weight = target == 1.0 ? positiveWeight : 1.0;
            loss -= weight * (target * Math.Log(Math.Max(1e-12, probability)) +
                    (1.0 - target) * Math.Log(Math.Max(1e-12, 1.0 - probability)));
            Update(offset + label * FeatureCount, features, rate * weight * (target - probability));
        }
        return loss / classes;
    }

    private double TrainSoftmax(
        int offset, int classes, double[] features, int target, double rate, double targetWeight = 1.0)
    {
        var probabilities = Softmax(offset, classes, features);
        var loss = -targetWeight * Math.Log(Math.Max(1e-12, probabilities[target]));
        for (var label = 0; label < classes; label++)
            Update(offset + label * FeatureCount, features,
                rate * targetWeight * ((label == target ? 1.0 : 0.0) - probabilities[label]));
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
                result[LexicalFeatureCount + index] = context[index];
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

    private double CandidateTopKAccuracy(
        IReadOnlyList<V10TrainingExample> examples, int count,
        Func<V10TrainingExample, IReadOnlyList<double>>? context)
    {
        var scored = examples.Where(example => example.SupervisedHeads.Contains("responseCandidate")).ToArray();
        if (scored.Length == 0) return double.NaN;
        return (double)scored.Count(example =>
        {
            var probabilities = Softmax(_layout.Candidate, _candidates.Length,
                Features(example.Context, context?.Invoke(example)));
            return Enumerable.Range(0, probabilities.Length).OrderByDescending(index => probabilities[index])
                .ThenBy(index => index).Take(count).Any(index => _candidates[index] == example.ResponseCandidateId);
        }) / scored.Length;
    }

    private double CandidateMeanReciprocalRank(
        IReadOnlyList<V10TrainingExample> examples,
        Func<V10TrainingExample, IReadOnlyList<double>>? context)
    {
        var scored = examples.Where(example => example.SupervisedHeads.Contains("responseCandidate")).ToArray();
        if (scored.Length == 0) return double.NaN;
        return scored.Average(example =>
        {
            var probabilities = Softmax(_layout.Candidate, _candidates.Length,
                Features(example.Context, context?.Invoke(example)));
            var ranking = Enumerable.Range(0, probabilities.Length).OrderByDescending(index => probabilities[index])
                .ThenBy(index => index).ToArray();
            var rank = Array.FindIndex(ranking, index => _candidates[index] == example.ResponseCandidateId);
            return rank < 0 ? 0.0 : 1.0 / (rank + 1);
        });
    }

    private static double ToolPrecision(
        IReadOnlyList<V10TrainingExample> examples, IReadOnlyList<StructuredPerception> predictions,
        IReadOnlyCollection<string> positiveTools)
    {
        var indices = Enumerable.Range(0, examples.Count)
            .Where(index => examples[index].SupervisedHeads.Contains("tool")).ToArray();
        var predictedPositive = indices.Where(index =>
            predictions[index].ToolSchema is { } tool && positiveTools.Contains(tool)).ToArray();
        if (predictedPositive.Length == 0) return 1.0;
        return (double)predictedPositive.Count(index =>
            positiveTools.Contains(examples[index].ToolSchema) &&
            examples[index].ToolSchema == predictions[index].ToolSchema) / predictedPositive.Length;
    }

    private static double SlotSpanF1(
        IReadOnlyList<V10TrainingExample> examples, IReadOnlyList<StructuredPerception> predictions)
    {
        var indices = Enumerable.Range(0, examples.Count)
            .Where(index => examples[index].SupervisedHeads.Contains("slots")).ToArray();
        if (indices.Length == 0) return double.NaN;
        var expected = indices.SelectMany(index => examples[index].Slots.Select(slot =>
            $"{index}|{slot.Type}|{slot.Start}|{slot.Length}|{slot.Value}")).ToHashSet(StringComparer.Ordinal);
        var actual = indices.SelectMany(index => predictions[index].Slots.Select(slot =>
            $"{index}|{slot.Type}|{slot.Start}|{slot.Length}|{slot.Value}")).ToHashSet(StringComparer.Ordinal);
        var correct = expected.Intersect(actual).Count();
        return 2.0 * correct / Math.Max(1, expected.Count + actual.Count);
    }

    private static double Accuracy<T>(
        IReadOnlyList<V10TrainingExample> examples, IReadOnlyList<StructuredPerception> predictions,
        string head, Func<V10TrainingExample, T> expected, Func<StructuredPerception, T> actual)
    {
        var indices = Enumerable.Range(0, examples.Count)
            .Where(index => examples[index].SupervisedHeads.Contains(head)).ToArray();
        return indices.Length == 0 ? double.NaN :
            (double)indices.Count(index => EqualityComparer<T>.Default.Equals(expected(examples[index]), actual(predictions[index]))) /
            indices.Length;
    }

    private static double MultiLabelMacroF1<T>(
        IReadOnlyList<V10TrainingExample> examples, IReadOnlyList<StructuredPerception> predictions,
        string head, Func<V10TrainingExample, IReadOnlyCollection<T>> expected,
        Func<StructuredPerception, IReadOnlyCollection<T>> actual, IReadOnlyList<T> labels)
    {
        var indices = Enumerable.Range(0, examples.Count)
            .Where(index => examples[index].SupervisedHeads.Contains(head)).ToArray();
        if (indices.Length == 0) return double.NaN;
        var present = labels.Where(label => indices.Any(index => expected(examples[index]).Contains(label))).ToArray();
        if (present.Length == 0)
            return indices.All(index => actual(predictions[index]).Count == 0) ? 1.0 : 0.0;
        return present.Select(label =>
        {
            var tp = indices.Count(index => expected(examples[index]).Contains(label) && actual(predictions[index]).Contains(label));
            var fp = indices.Count(index => !expected(examples[index]).Contains(label) && actual(predictions[index]).Contains(label));
            var fn = indices.Count(index => expected(examples[index]).Contains(label) && !actual(predictions[index]).Contains(label));
            return 2.0 * tp / Math.Max(1, 2 * tp + fp + fn);
        }).Average();
    }

    private double Dot(int offset, IReadOnlyList<double> features)
    {
        var value = 0.0;
        for (var index = 0; index < FeatureCount; index++) value += _weights[offset + index] * features[index];
        return value;
    }

    private void Update(int offset, IReadOnlyList<double> features, double scale)
    {
        for (var index = 0; index < FeatureCount; index++) _weights[offset + index] += scale * features[index];
    }

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
            Tool = KnowledgeTarget + Enum.GetValues<KnowledgeTarget>().Length * FeatureCount;
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
    double Composite);

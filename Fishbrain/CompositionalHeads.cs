using System.Collections.ObjectModel;
using System.Text;

namespace Fishbrain;

internal sealed record V10TrainingExample(
    string Input,
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
    string Source,
    string SemanticFamilyId,
    IReadOnlySet<string> SupervisedHeads);

internal sealed class CompositionalHeadModel
{
    private const int FeatureCount = 512;
    private const int SlotClassCount = 1 + 2 * 14;
    private readonly string[] _tools;
    private readonly string[] _candidates;
    private readonly Layout _layout;
    private readonly double[] _weights;

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

    public void Restore(IReadOnlyList<double> weights, int updates)
    {
        if (weights.Count != _weights.Length) throw new InvalidDataException("Structured head parameter count mismatch.");
        for (var index = 0; index < weights.Count; index++) _weights[index] = weights[index];
        Updates = updates;
    }

    public double Train(V10TrainingExample example, double learningRate)
    {
        var features = Features(example.Input);
        var heads = 0;
        var loss = 0.0;
        Add("speechActs", () => TrainMulti(_layout.Speech, Enum.GetValues<SpeechAct>().Length, features,
            example.SpeechActs.Select(value => (int)value).ToHashSet(), learningRate));
        Add("domains", () => TrainMulti(_layout.Domain, Enum.GetValues<DialogueDomain>().Length, features,
            example.Domains.Select(value => (int)value).ToHashSet(), learningRate));
        Add("goals", () => TrainMulti(_layout.Goal, Enum.GetValues<DialogueGoal>().Length, features,
            example.Goals.Select(value => (int)value).ToHashSet(), learningRate));
        Add("affect", () => TrainSoftmax(_layout.Affect, Enum.GetValues<UserAffect>().Length, features, (int)example.Affect, learningRate));
        Add("stance", () => TrainSoftmax(_layout.Stance, Enum.GetValues<DialogueStance>().Length, features, (int)example.Stance, learningRate));
        Add("policy", () => TrainSoftmax(_layout.Policy, Enum.GetValues<ResponsePolicy>().Length, features, (int)example.Policy, learningRate));
        Add("content", () => TrainMulti(_layout.Content, Enum.GetValues<ContentFlag>().Length, features,
            example.ContentFlags.Select(value => (int)value).ToHashSet(), learningRate));
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

    public double TrainSlotsOnly(V10TrainingExample example, double learningRate)
    {
        if (!example.SupervisedHeads.Contains("slots"))
            throw new ArgumentException("The auxiliary slot pass requires slot supervision.", nameof(example));
        return TrainSlots(example, learningRate * 0.05);
    }

    public StructuredPerception Predict(string input, IReadOnlyList<DialogueSlot> preservedSlots)
    {
        var features = Features(input);
        var speech = PredictMulti<SpeechAct>(_layout.Speech, features, 0.50);
        var domains = PredictMulti<DialogueDomain>(_layout.Domain, features, 0.50);
        var goals = PredictMulti<DialogueGoal>(_layout.Goal, features, 0.50);
        var (affect, affectConfidence) = PredictSoftmax<UserAffect>(_layout.Affect, features);
        var (stance, stanceConfidence) = PredictSoftmax<DialogueStance>(_layout.Stance, features);
        var (policy, policyConfidence) = PredictSoftmax<ResponsePolicy>(_layout.Policy, features);
        var content = PredictMulti<ContentFlag>(_layout.Content, features, 0.50, allowEmpty: true);
        var (toolIndex, toolConfidence) = PredictSoftmaxIndex(_layout.Tool, _tools.Length, features);
        var (candidateIndex, candidateConfidence) = PredictSoftmaxIndex(_layout.Candidate, _candidates.Length, features);
        var learnedSlots = PredictSlots(input);
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
            ["TOOL"] = toolConfidence,
            ["RESPONSE_CANDIDATE"] = candidateConfidence
        });
        return new StructuredPerception(speech.Values, domains.Values, goals.Values, affect, stance, policy,
            slots, content.Values, _tools[toolIndex] == "NONE" ? null : _tools[toolIndex],
            _candidates[candidateIndex], confidence);
    }

    public StructuredMetrics Evaluate(IReadOnlyList<V10TrainingExample> examples)
    {
        var predictions = examples.Select(example => Predict(example.Input, [])).ToArray();
        return EvaluatePredictions(examples, predictions, CandidateTopKAccuracy(examples, 3));
    }

    public Dictionary<string, V10Schemas.ConfidenceThreshold> Calibrate(
        IReadOnlyList<V10TrainingExample> examples)
    {
        var predictions = examples.Select(example => Predict(example.Input, [])).ToArray();
        var result = V10Schemas.DefaultCalibration;
        CalibrateHead("speechActs", "SPEECH_ACT", example =>
            SetEqual(example.Example.SpeechActs, predictions[example.Index].SpeechActs));
        CalibrateHead("domains", "DOMAIN", example => SetEqual(example.Example.Domains, predictions[example.Index].Domains));
        CalibrateHead("goals", "GOAL", example => SetEqual(example.Example.Goals, predictions[example.Index].Goals));
        CalibrateHead("affect", "AFFECT", example => example.Example.Affect == predictions[example.Index].Affect);
        CalibrateHead("stance", "STANCE", example => example.Example.Stance == predictions[example.Index].Stance);
        CalibrateHead("policy", "POLICY", example => example.Example.Policy == predictions[example.Index].Policy);
        CalibrateHead("content", "CONTENT", example => SetEqual(example.Example.ContentFlags, predictions[example.Index].ContentFlags));
        CalibrateHead("slots", "SLOTS", example => SetEqual(
            example.Example.Slots.Select(slot => (slot.Type, slot.Start, slot.Length)),
            predictions[example.Index].Slots.Select(slot => (slot.Type, slot.Start, slot.Length))));
        CalibrateHead("responseCandidate", "RESPONSE_CANDIDATE",
            example => example.Example.ResponseCandidateId == predictions[example.Index].ResponseCandidateId);
        return result;

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
                result[schemaName] = new V10Schemas.ConfidenceThreshold(selected.Threshold,
                    result[schemaName].Margin);
        }

        static bool SetEqual<T>(IEnumerable<T> left, IEnumerable<T> right) =>
            left.ToHashSet().SetEquals(right);
    }

    internal static StructuredMetrics EvaluatePredictions(
        IReadOnlyList<V10TrainingExample> examples,
        IReadOnlyList<StructuredPerception> predictions,
        double? responseTop3 = null)
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
        var mutatingToolPrecision = ToolPrecision(examples, predictions, ["BUY", "SELL"]);
        var candidate = Accuracy(examples, predictions, "responseCandidate", example => example.ResponseCandidateId,
            prediction => prediction.ResponseCandidateId ?? "");
        var candidateTop3 = responseTop3 ?? candidate;
        var composite = new[] { speech, domains, goals, affect, policy, content, slots, tool, candidate }
            .Where(double.IsFinite).DefaultIfEmpty(0.0).Average();
        return new StructuredMetrics(speech, domains, goals, affect, stance, policy, content, slots,
            tool, mutatingToolPrecision, candidate, candidateTop3, composite);
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
                target == 0 ? 1.0 : 2.0);
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

    private double TrainMulti(int offset, int classes, double[] features, IReadOnlySet<int> targets, double rate)
    {
        var loss = 0.0;
        for (var label = 0; label < classes; label++)
        {
            var probability = Sigmoid(Dot(offset + label * FeatureCount, features));
            var target = targets.Contains(label) ? 1.0 : 0.0;
            var weight = target == 1.0 ? 2.0 : 1.0;
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
        int offset, double[] features, double threshold, bool allowEmpty = false)
        where T : struct, Enum
    {
        var classes = Enum.GetValues<T>().Length;
        var values = Enumerable.Range(0, classes).Select(label => Sigmoid(Dot(offset + label * FeatureCount, features))).ToArray();
        var selected = Enumerable.Range(0, classes).Where(label => values[label] >= threshold).ToArray();
        if (selected.Length == 0 && !allowEmpty) selected = [Array.IndexOf(values, values.Max())];
        return (selected.Select(label => (T)Enum.ToObject(typeof(T), label)).ToArray(),
            selected.Length == 0 ? 1.0 - values.Max() : selected.Min(label => values[label]));
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

    private static double[] Features(string text)
    {
        var result = new double[FeatureCount];
        result[0] = 1.0;
        var words = Tokenizer.Lex(DialogueText.Normalize(text)).Where(token => token.Kind == LexicalTokenKind.Word)
            .Select(token => token.Text).ToArray();
        foreach (var word in words) result[1 + StableHash(word) % (FeatureCount - 1)] += 1.0;
        for (var index = 1; index < words.Length; index++)
            result[1 + StableHash(words[index - 1] + "_" + words[index]) % (FeatureCount - 1)] += 0.7;
        var norm = Math.Sqrt(result.Sum(value => value * value));
        for (var index = 0; index < result.Length; index++) result[index] /= Math.Max(1.0, norm);
        return result;
    }

    private static double[] TokenFeatures(
        string word, string previous, string next, string previous2, string next2)
    {
        var result = new double[FeatureCount];
        result[0] = 1.0;
        result[1 + StableHash(word) % (FeatureCount - 1)] = 1.0;
        if (word.Length >= 3) result[1 + StableHash(word[..3]) % (FeatureCount - 1)] += 0.5;
        result[1 + StableHash("P:" + previous) % (FeatureCount - 1)] += 0.8;
        result[1 + StableHash("N:" + next) % (FeatureCount - 1)] += 0.5;
        result[1 + StableHash(previous + ">" + word) % (FeatureCount - 1)] += 0.7;
        result[1 + StableHash("P2:" + previous2) % (FeatureCount - 1)] += 0.5;
        result[1 + StableHash("N2:" + next2) % (FeatureCount - 1)] += 0.3;
        result[1 + StableHash(previous2 + ">" + previous + ">" + word) % (FeatureCount - 1)] += 0.6;
        return result;
    }

    private double CandidateTopKAccuracy(IReadOnlyList<V10TrainingExample> examples, int count)
    {
        var scored = examples.Where(example => example.SupervisedHeads.Contains("responseCandidate")).ToArray();
        if (scored.Length == 0) return double.NaN;
        return (double)scored.Count(example =>
        {
            var probabilities = Softmax(_layout.Candidate, _candidates.Length, Features(example.Input));
            return Enumerable.Range(0, probabilities.Length).OrderByDescending(index => probabilities[index])
                .ThenBy(index => index).Take(count).Any(index => _candidates[index] == example.ResponseCandidateId);
        }) / scored.Length;
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
            Tool = Slot + SlotClassCount * FeatureCount;
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
    double ResponseTop1,
    double ResponseTop3,
    double Composite);

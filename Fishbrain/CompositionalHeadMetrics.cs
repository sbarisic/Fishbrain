namespace Fishbrain;

internal sealed partial class CompositionalHeadModel
{
    private double CandidateTopKAccuracy(
        IReadOnlyList<TrainingExample> examples,
        int count,
        Func<TrainingExample, IReadOnlyList<double>>? context)
    {
        var scored = examples.Where(example =>
            example.SupervisedHeads.Contains("responseCandidate")).ToArray();
        if (scored.Length == 0) return double.NaN;
        return (double)scored.Count(example =>
        {
            var probabilities = Softmax(_layout.Candidate, _candidates.Length,
                Features(example.Context, context?.Invoke(example)));
            return Enumerable.Range(0, probabilities.Length)
                .OrderByDescending(index => probabilities[index])
                .ThenBy(index => index)
                .Take(count)
                .Any(index => _candidates[index] == example.ResponseCandidateId);
        }) / scored.Length;
    }

    private double CandidateMeanReciprocalRank(
        IReadOnlyList<TrainingExample> examples,
        Func<TrainingExample, IReadOnlyList<double>>? context)
    {
        var scored = examples.Where(example =>
            example.SupervisedHeads.Contains("responseCandidate")).ToArray();
        if (scored.Length == 0) return double.NaN;
        return scored.Average(example =>
        {
            var probabilities = Softmax(_layout.Candidate, _candidates.Length,
                Features(example.Context, context?.Invoke(example)));
            var ranking = Enumerable.Range(0, probabilities.Length)
                .OrderByDescending(index => probabilities[index])
                .ThenBy(index => index)
                .ToArray();
            var rank = Array.FindIndex(ranking,
                index => _candidates[index] == example.ResponseCandidateId);
            return rank < 0 ? 0.0 : 1.0 / (rank + 1);
        });
    }

    private static double ToolPrecision(
        IReadOnlyList<TrainingExample> examples,
        IReadOnlyList<StructuredPerception> predictions,
        IReadOnlyCollection<string> positiveTools)
    {
        var indices = Enumerable.Range(0, examples.Count)
            .Where(index => examples[index].SupervisedHeads.Contains("tool"))
            .ToArray();
        var predictedPositive = indices.Where(index =>
            predictions[index].ToolSchema is { } tool && positiveTools.Contains(tool)).ToArray();
        if (predictedPositive.Length == 0) return 1.0;
        return (double)predictedPositive.Count(index =>
            positiveTools.Contains(examples[index].ToolSchema) &&
            examples[index].ToolSchema == predictions[index].ToolSchema) / predictedPositive.Length;
    }

    private static double SlotSpanF1(
        IReadOnlyList<TrainingExample> examples,
        IReadOnlyList<StructuredPerception> predictions)
    {
        var indices = Enumerable.Range(0, examples.Count)
            .Where(index => examples[index].SupervisedHeads.Contains("slots"))
            .ToArray();
        if (indices.Length == 0) return double.NaN;
        var expected = indices.SelectMany(index => examples[index].Slots.Select(slot =>
            $"{index}|{slot.Type}|{slot.Start}|{slot.Length}|{slot.Value}"))
            .ToHashSet(StringComparer.Ordinal);
        var actual = indices.SelectMany(index => predictions[index].Slots.Select(slot =>
            $"{index}|{slot.Type}|{slot.Start}|{slot.Length}|{slot.Value}"))
            .ToHashSet(StringComparer.Ordinal);
        var correct = expected.Intersect(actual).Count();
        return 2.0 * correct / Math.Max(1, expected.Count + actual.Count);
    }

    private static double Accuracy<T>(
        IReadOnlyList<TrainingExample> examples,
        IReadOnlyList<StructuredPerception> predictions,
        string head,
        Func<TrainingExample, T> expected,
        Func<StructuredPerception, T> actual)
    {
        var indices = Enumerable.Range(0, examples.Count)
            .Where(index => examples[index].SupervisedHeads.Contains(head))
            .ToArray();
        return indices.Length == 0
            ? double.NaN
            : (double)indices.Count(index => EqualityComparer<T>.Default.Equals(
                expected(examples[index]), actual(predictions[index]))) / indices.Length;
    }

    private static double MultiLabelMacroF1<T>(
        IReadOnlyList<TrainingExample> examples,
        IReadOnlyList<StructuredPerception> predictions,
        string head,
        Func<TrainingExample, IReadOnlyCollection<T>> expected,
        Func<StructuredPerception, IReadOnlyCollection<T>> actual,
        IReadOnlyList<T> labels)
    {
        var indices = Enumerable.Range(0, examples.Count)
            .Where(index => examples[index].SupervisedHeads.Contains(head))
            .ToArray();
        if (indices.Length == 0) return double.NaN;
        var present = labels.Where(label =>
            indices.Any(index => expected(examples[index]).Contains(label))).ToArray();
        if (present.Length == 0)
            return indices.All(index => actual(predictions[index]).Count == 0) ? 1.0 : 0.0;
        return present.Select(label =>
        {
            var tp = indices.Count(index =>
                expected(examples[index]).Contains(label) && actual(predictions[index]).Contains(label));
            var fp = indices.Count(index =>
                !expected(examples[index]).Contains(label) && actual(predictions[index]).Contains(label));
            var fn = indices.Count(index =>
                expected(examples[index]).Contains(label) && !actual(predictions[index]).Contains(label));
            return 2.0 * tp / Math.Max(1, 2 * tp + fp + fn);
        }).Average();
    }

    private static double SubsetAccuracy<T>(
        IReadOnlyList<TrainingExample> examples,
        IReadOnlyList<StructuredPerception> predictions,
        string head,
        Func<TrainingExample, bool> include,
        Func<TrainingExample, T> expected,
        Func<StructuredPerception, T> actual)
    {
        var indices = Enumerable.Range(0, examples.Count)
            .Where(index => examples[index].SupervisedHeads.Contains(head) && include(examples[index]))
            .ToArray();
        return indices.Length == 0
            ? 0.0
            : (double)indices.Count(index => EqualityComparer<T>.Default.Equals(
                expected(examples[index]), actual(predictions[index]))) / indices.Length;
    }

    private static double FactSpanF1(
        IReadOnlyList<TrainingExample> examples,
        IReadOnlyList<StructuredPerception> predictions)
    {
        var indices = Enumerable.Range(0, examples.Count).Where(index =>
            examples[index].SupervisedHeads.Contains("factSpan") &&
            examples[index].Discourse.FactKind is not null).ToArray();
        if (indices.Length == 0) return 0.0;
        var expected = indices.Where(index => examples[index].Discourse.FactValueSpan is not null)
            .Select(index => SpanKey(index, examples[index].Discourse.FactValueSpan!))
            .ToHashSet(StringComparer.Ordinal);
        var actual = indices.Where(index => predictions[index].Discourse?.FactValueSpan is not null)
            .Select(index => SpanKey(index, predictions[index].Discourse!.FactValueSpan!))
            .ToHashSet(StringComparer.Ordinal);
        var correct = expected.Intersect(actual).Count();
        return 2.0 * correct / Math.Max(1, expected.Count + actual.Count);

        static string SpanKey(int index, DialogueTextSpan span) =>
            $"{index}|{span.Start}|{span.Length}|{span.NormalizedValue}";
    }

    private static double CorrectionStateAccuracy(
        IReadOnlyList<TrainingExample> examples,
        IReadOnlyList<StructuredPerception> predictions)
    {
        var indices = Enumerable.Range(0, examples.Count).Where(index =>
            examples[index].SupervisedHeads.Contains("factPolarity") &&
            examples[index].Discourse.Act is DiscourseAct.Correct or DiscourseAct.RejectAssumption)
            .ToArray();
        if (indices.Length == 0) return 0.0;

        return (double)indices.Count(index =>
        {
            var example = examples[index];
            var actual = DialogueStateReducer.ReduceFacts(
                example.InitialFacts,
                predictions[index].Discourse,
                example.Turns[^1].Sequence);
            return FactStateSignature(actual) == FactStateSignature(example.ExpectedFactState);
        }) / indices.Length;

        static string FactStateSignature(IEnumerable<DialogueFact> facts) => string.Join('|', facts
            .Select(fact => $"{fact.Subject}:{fact.Kind}:{fact.Value}:{fact.Negated}")
            .Order(StringComparer.Ordinal));
    }
}

namespace Fishbrain;

internal static class ModelSchemas
{
    internal sealed record ConfidenceThreshold(double Threshold, double Margin);

    public static Dictionary<string, string[]> Labels => new(StringComparer.Ordinal)
    {
        ["speechActs"] = Enum.GetNames<SpeechAct>(),
        ["domains"] = Enum.GetNames<DialogueDomain>(),
        ["goals"] = Enum.GetNames<DialogueGoal>(),
        ["affect"] = Enum.GetNames<UserAffect>(),
        ["stance"] = Enum.GetNames<DialogueStance>(),
        ["policy"] = Enum.GetNames<ResponsePolicy>(),
        ["slots"] = Enum.GetNames<SlotType>(),
        ["content"] = Enum.GetNames<ContentFlag>(),
        ["knowledgeTarget"] = Enum.GetNames<KnowledgeTarget>(),
        ["discourseAct"] = Enum.GetNames<DiscourseAct>(),
        ["discourseSubject"] = Enum.GetNames<DialogueParticipant>(),
        ["discourseTarget"] = Enum.GetNames<DialogueParticipant>(),
        ["factKind"] = new[] { "NONE" }.Concat(Enum.GetNames<DialogueFactKind>()).ToArray(),
        ["factPolarity"] = ["POSITIVE", "NEGATED"],
        ["factSpan"] = ["O", "B", "I"],
        ["antecedent"] = ["NONE", "RETAINED_UTTERANCE"]
    };

    public static Dictionary<string, ConfidenceThreshold> DefaultCalibration => new(StringComparer.Ordinal)
    {
        ["speechActs"] = new(0.50, 0.10),
        ["domains"] = new(0.50, 0.10),
        ["goals"] = new(0.50, 0.10),
        ["affect"] = new(0.65, 0.12),
        ["stance"] = new(0.65, 0.12),
        ["policy"] = new(0.75, 0.15),
        ["slots"] = new(0.80, 0.10),
        ["content"] = new(0.50, 0.10),
        ["discourseAct"] = new(0.65, 0.12),
        ["discourseSubject"] = new(0.65, 0.12),
        ["discourseTarget"] = new(0.65, 0.12),
        ["factKind"] = new(0.65, 0.12),
        ["factPolarity"] = new(0.65, 0.12),
        ["factSpan"] = new(0.65, 0.12),
        ["antecedent"] = new(0.65, 0.12),
        ["toolReadOnly"] = new(0.95, 0.05),
        ["toolMutating"] = new(0.99, 0.01),
        ["responseCandidate"] = new(0.70, 0.10),
        ["knowledgeTarget"] = new(0.85, 0.10)
    };

    public static void Validate(
        IReadOnlyDictionary<string, string[]> labels,
        IReadOnlyDictionary<string, ConfidenceThreshold> calibration)
    {
        if (labels.Count != Labels.Count)
            throw new InvalidDataException("Checkpoint label schemas contain unexpected entries.");
        foreach (var expected in Labels)
            if (!labels.TryGetValue(expected.Key, out var actual) || actual is null ||
                !actual.SequenceEqual(expected.Value))
                throw new InvalidDataException($"Checkpoint label schema '{expected.Key}' does not match this runtime.");
        if (calibration.Count != DefaultCalibration.Count)
            throw new InvalidDataException("Checkpoint confidence calibration contains unexpected entries.");
        foreach (var expected in DefaultCalibration.Keys)
            if (!calibration.ContainsKey(expected))
                throw new InvalidDataException($"Checkpoint confidence calibration '{expected}' is missing.");
        foreach (var item in calibration)
            if (item.Value is null || !double.IsFinite(item.Value.Threshold) || !double.IsFinite(item.Value.Margin) ||
                item.Value.Threshold is < 0 or > 1 || item.Value.Margin is < 0 or > 1)
                throw new InvalidDataException($"Invalid confidence calibration for '{item.Key}'.");
    }
}

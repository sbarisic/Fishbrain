using Fishbrain;

namespace Fishbrain.Tests;

internal static class DiscourseTrainingTestSuite
{
    public static void NeuralOverfit()
    {
        var examples = ControlledExamples();
        var model = new CompositionalHeadModel([], ["ACKNOWLEDGE"], 42);
        for (var epoch = 0; epoch < 2_500; epoch++)
        {
            model.TrainBatch(examples, 0.03, _ => Array.Empty<double>(), StructuredTrainingMode.Discourse);
        }

        var predictions = examples.Select(example => model.Predict(
            example.Context,
            [],
            currentInput: example.Input,
            utterances: example.Turns)).ToArray();
        var metrics = CompositionalHeadModel.EvaluatePredictions(examples, predictions);
        Assert(metrics.DiscourseActAccuracy >= 0.99, "discourse act did not overfit");
        Assert(metrics.SpeakerAttributionAccuracy >= 0.99, "participant attribution did not overfit");
        Assert(metrics.FactSpanF1 >= 0.99, "fact span did not overfit");
        Assert(metrics.AntecedentAccuracy >= 0.99, "antecedent pointer did not overfit retained and NONE targets");
        Assert(metrics.CorrectionStateAccuracy >= 0.99, "reducer-based correction state did not overfit");
        Assert(examples.Zip(predictions).All(pair =>
                pair.First.Discourse.FactKind == pair.Second.Discourse?.FactKind &&
                pair.First.Discourse.Negated == pair.Second.Discourse?.Negated),
            "predicate or polarity did not overfit");

        var slotWeights = model.SnapshotHeadWeights("slots");
        var responseWeights = model.SnapshotHeadWeights("responseCandidate");
        for (var epoch = 0; epoch < 50; epoch++)
        {
            model.TrainBatch(examples, 0.03, _ => Array.Empty<double>(), StructuredTrainingMode.Discourse);
        }

        Assert(slotWeights.SequenceEqual(model.SnapshotHeadWeights("slots")),
            "discourse polishing changed operational game-slot weights");
        model.TrainBatch(examples, 0.03, _ => Array.Empty<double>(), StructuredTrainingMode.All);
        Assert(responseWeights.SequenceEqual(model.SnapshotHeadWeights("responseCandidate")),
            "structured training changed the phase-isolated response-ranking weights");
    }

    public static void CurriculumSampler()
    {
        var examples = SamplerExamples();
        var first = new TrainingCurriculumSampler(examples, 42);
        var second = new TrainingCurriculumSampler(examples, 42);
        var firstBatch = Enumerable.Range(0, 10).SelectMany(index => first.BaseStructuredBatch(index)).ToArray();
        var secondBatch = Enumerable.Range(0, 10).SelectMany(index => second.BaseStructuredBatch(index)).ToArray();
        Assert(firstBatch.Select(Key).SequenceEqual(secondBatch.Select(Key)),
            "epoch shuffling is not deterministic");
        var firstRankingBatch = first.RankingBatch(17);
        var secondRankingBatch = second.RankingBatch(17);
        Assert(firstRankingBatch.Length == 32 &&
               firstRankingBatch.Select(Key).SequenceEqual(secondRankingBatch.Select(Key)) &&
               firstRankingBatch.Select(example => example.SemanticFamilyId).Distinct(StringComparer.Ordinal).Count() > 1,
            "ranking minibatches are not deterministic and family-diverse");
        Assert(firstBatch.Count(TrainingCurriculumSampler.IsDiscourse) == 80,
            "base structured quota is not 75 percent operational and 25 percent discourse");
        Assert(Enumerable.Range(0, 90)
                .Count(index => TrainingCurriculumSampler.RareOperationalFocus(index) == "POLICY") == 62 &&
               Enumerable.Range(0, 90)
                .Count(index => TrainingCurriculumSampler.RareOperationalFocus(index) == "TOOL") == 18 &&
               Enumerable.Range(0, 90)
                .Count(index => TrainingCurriculumSampler.RareOperationalFocus(index) is not ("POLICY" or "TOOL")) == 10,
            "rare operational focus does not match the exact audited head quota");
        Assert(firstBatch.Count(example => example.SemanticFamilyId == "OP-LARGE") >
               firstBatch.Count(example => example.SemanticFamilyId == "OP-SMALL"),
            "natural-frequency operational sampling does not weight families by row frequency");

        var toolSampler = new TrainingCurriculumSampler(ToolSamplerExamples(), 42);
        var toolBatch = Enumerable.Range(0, 100)
            .SelectMany(index => toolSampler.CorrectiveOperationalBatch(index))
            .ToArray();
        var toolFocused = toolBatch.Count(example => example.ToolSchema != "NONE");
        Assert(toolFocused >= 800,
            "the 20 percent rare-focus stream is not selecting rare real-tool families");
        var toolNullContrasts = toolBatch.Count(TrainingCurriculumSampler.IsToolNullContrast);
        Assert(toolNullContrasts >= 200,
            "the rare-tool stream is not retaining hostile NONE contrasts");
        var calibration = Brain.FamilyBalancedSubset(ToolSamplerExamples(), 4, 42);
        Assert(calibration.Any(example => example.ToolSchema is "BUY" or "SELL") &&
               calibration.Any(TrainingCurriculumSampler.IsToolNullContrast),
            "fixed calibration sampling omitted a mutating positive or hostile NONE contrast family");

        var discourse = Enumerable.Range(0, 10).SelectMany(index => first.DiscourseBatch(index)).ToArray();
        var counts = discourse.GroupBy(example => example.Source).ToDictionary(group => group.Key, group => group.Count());
        Assert(counts["PROJECT_DISCOURSE_FACTS"] == 128 &&
               counts["PROJECT_DISCOURSE_REFERENCES"] == 96 &&
               counts["PROJECT_CONVERSATION"] == 64 &&
               counts["PROJECT_DISCOURSE_NEGATIVES"] == 32,
            "discourse band quotas are not 40/30/20/10");
        var pointerSupervised = discourse.Where(example => example.SupervisedHeads.Contains("antecedent")).ToArray();
        Assert(pointerSupervised.Count(example => example.Discourse.AntecedentUtterance is not null) * 2 ==
               pointerSupervised.Length,
            "discourse sampling is not balanced between positive and NONE antecedent targets");
        Assert(discourse.GroupBy(example => example.SemanticFamilyId)
                .All(group => group.Select(example => example.Input).Distinct(StringComparer.Ordinal).Count() > 1),
            "family member rotation did not expose multiple members");
        Assert(Brain.StructuredLearningRate(0) == 0.03 &&
               Math.Abs(Brain.StructuredLearningRate(200_000) - 0.003) < 1e-12,
            "structured learning-rate cosine endpoints are incorrect");

        static string Key(TrainingExample example) => example.SemanticFamilyId + "|" + example.Input;
    }

    private static TrainingExample[] ToolSamplerExamples()
    {
        var result = SamplerExamples().Where(TrainingCurriculumSampler.IsDiscourse).ToList();
        AddOperationalFamily("TOOL-NONE-A", "NONE", 4, "BUY 2 ROPE, IDIOT");
        AddOperationalFamily("TOOL-NONE-B", "NONE", 4, "SELL 2 ROPE, IDIOT");
        AddOperationalFamily("TOOL-BUY", "BUY", 1, "BUY 2 ROPE");
        AddOperationalFamily("TOOL-SELL", "SELL", 1, "SELL 2 ROPE");
        return result.ToArray();

        void AddOperationalFamily(string family, string tool, int members, string input)
        {
            for (var member = 0; member < members; member++)
            {
                var example = Example(
                    $"{input} MEMBER {member}",
                    DiscourseAct.None,
                    DialogueParticipant.None,
                    null,
                    null,
                    false,
                    null,
                    family,
                    "PROJECT_OPERATIONAL");
                result.Add(example with
                {
                    ToolSchema = tool,
                    SupervisedHeads = new HashSet<string>(["tool", "policy"], StringComparer.Ordinal)
                });
            }
        }
    }

    private static TrainingExample[] ControlledExamples()
    {
        var scientist = Example(
            "I AM A SCIENTIST",
            DiscourseAct.Inform,
            DialogueParticipant.Player,
            DialogueFactKind.Occupation,
            "SCIENTIST",
            false,
            null,
            "CONTROL:INFORM");
        var correction = Example(
            "I AM NOT A TRAVELER",
            DiscourseAct.RejectAssumption,
            DialogueParticipant.Player,
            DialogueFactKind.Occupation,
            "TRAVELER",
            true,
            1,
            "CONTROL:CORRECT");
        var callback = Example(
            "WHAT DID YOU MEAN ABOUT THE ROAD",
            DiscourseAct.AskExplanation,
            DialogueParticipant.Player,
            null,
            null,
            false,
            1,
            "CONTROL:POINTER");
        var ambiguous = Example(
            "WHICH ROAD REMARK DID YOU MEAN",
            DiscourseAct.AskExplanation,
            DialogueParticipant.Player,
            null,
            null,
            false,
            null,
            "CONTROL:NONE");
        return [scientist, correction, callback, ambiguous];
    }

    private static TrainingExample[] SamplerExamples()
    {
        var result = new List<TrainingExample>();
        AddFamily("PROJECT_OPERATIONAL", "OP-LARGE", 8, DiscourseAct.None, null);
        AddFamily("PROJECT_OPERATIONAL", "OP-SMALL", 1, DiscourseAct.None, null);
        AddFamily("PROJECT_DISCOURSE_FACTS", "FACT-POS", 4, DiscourseAct.Correct, DialogueFactKind.Role, 1);
        AddFamily("PROJECT_DISCOURSE_FACTS", "FACT-NONE", 4, DiscourseAct.Inform, DialogueFactKind.Role);
        AddFamily("PROJECT_DISCOURSE_REFERENCES", "REF-POS", 4, DiscourseAct.AskExplanation, null, 1);
        AddFamily("PROJECT_DISCOURSE_REFERENCES", "REF-NONE", 4, DiscourseAct.AskExplanation, null);
        AddFamily("PROJECT_CONVERSATION", "CHAT", 4, DiscourseAct.None, null);
        AddFamily("PROJECT_DISCOURSE_NEGATIVES", "NEG", 2, DiscourseAct.None, null);
        return result.ToArray();

        void AddFamily(
            string source,
            string family,
            int members,
            DiscourseAct act,
            DialogueFactKind? kind,
            long? antecedent = null)
        {
            for (var member = 0; member < members; member++)
            {
                var input = $"{family} MEMBER {member}";
                result.Add(Example(input, act, DialogueParticipant.Player, kind,
                    kind is null ? null : family, false, antecedent, family, source));
            }
        }
    }

    private static TrainingExample Example(
        string input,
        DiscourseAct act,
        DialogueParticipant subject,
        DialogueFactKind? kind,
        string? value,
        bool negated,
        long? antecedent,
        string family,
        string source = "PROJECT_DISCOURSE_FACTS")
    {
        var span = value is null
            ? null
            : new DialogueTextSpan(value, input.IndexOf(value, StringComparison.Ordinal), value.Length);
        var turns = antecedent is null
            ? new[]
            {
                new DialogueUtterance(0, DialogueRole.Npc, "THE FIRST ROAD REMARK"),
                new DialogueUtterance(1, DialogueRole.Npc, "THE SECOND ROAD REMARK"),
                new DialogueUtterance(2, DialogueRole.Player, input)
            }
            : new[]
            {
                new DialogueUtterance(1, DialogueRole.Npc, "THE ROAD IS LONG"),
                new DialogueUtterance(2, DialogueRole.Player, input)
            };
        var frame = new DiscourseFrame(
            act,
            act == DiscourseAct.None ? DialogueParticipant.None : subject,
            act == DiscourseAct.AskExplanation ? DialogueParticipant.Npc : DialogueParticipant.None,
            kind,
            span,
            negated,
            antecedent,
            1.0,
            "CONTROLLED");
        var expected = kind is null || span is null
            ? []
            : new[]
            {
                new DialogueFact(subject, kind.Value, span.NormalizedValue, negated, turns[^1].Sequence, 1.0,
                    DialogueFactProvenance.SessionReported)
            };
        return new TrainingExample(
            input,
            input,
            turns,
            [act == DiscourseAct.AskExplanation ? SpeechAct.Ask : SpeechAct.Inform],
            [DialogueDomain.Social],
            [DialogueGoal.InformationExchange],
            UserAffect.Neutral,
            DialogueStance.Neutral,
            ResponsePolicy.Answer,
            [],
            [],
            "NONE",
            "ACKNOWLEDGE",
            KnowledgeTarget.None,
            source,
            family,
            new HashSet<string>(
                source == "PROJECT_CONVERSATION"
                    ? ["discourseAct", "discourseSubject", "discourseTarget"]
                    : ["discourseAct", "discourseSubject", "discourseTarget", "factKind", "factPolarity", "factSpan", "antecedent"],
                StringComparer.Ordinal),
            frame,
            [],
            expected,
            DiscourseResponseAction.None,
            [],
            null);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

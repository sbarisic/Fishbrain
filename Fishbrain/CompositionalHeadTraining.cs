namespace Fishbrain;

internal sealed partial class CompositionalHeadModel
{
    public double Train(
        TrainingExample example,
        double learningRate,
        IReadOnlyList<double>? contextVector = null,
        IReadOnlyList<double>? domainPositiveWeights = null)
    {
        var features = Features(example.Context, contextVector);
        var heads = 0;
        var loss = 0.0;
        Add("speechActs", () => TrainMulti(_layout.Speech, Enum.GetValues<SpeechAct>().Length, features,
            example.SpeechActs.Select(value => (int)value).ToHashSet(), learningRate, MaximumPositiveWeight));
        Add("domains", () => TrainMulti(_layout.Domain, Enum.GetValues<DialogueDomain>().Length, features,
            example.Domains.Select(value => (int)value).ToHashSet(), learningRate, MaximumPositiveWeight,
            domainPositiveWeights));
        Add("goals", () => TrainMulti(_layout.Goal, Enum.GetValues<DialogueGoal>().Length, features,
            example.Goals.Select(value => (int)value).ToHashSet(), learningRate, MaximumPositiveWeight));
        Add("affect", () => TrainSoftmax(_layout.Affect, Enum.GetValues<UserAffect>().Length, features,
            (int)example.Affect, learningRate, MaximumPositiveWeight));
        Add("stance", () => TrainSoftmax(_layout.Stance, Enum.GetValues<DialogueStance>().Length, features,
            (int)example.Stance, learningRate, MaximumPositiveWeight));
        Add("policy", () => TrainSoftmax(_layout.Policy, Enum.GetValues<ResponsePolicy>().Length, features,
            (int)example.Policy, learningRate, MaximumPositiveWeight));
        Add("content", () => TrainMulti(_layout.Content, Enum.GetValues<ContentFlag>().Length, features,
            example.ContentFlags.Select(value => (int)value).ToHashSet(), learningRate, MaximumPositiveWeight));
        Add("knowledgeTarget", () => TrainSoftmax(_layout.KnowledgeTarget,
            Enum.GetValues<KnowledgeTarget>().Length, features, (int)example.KnowledgeTarget, learningRate,
            MaximumPositiveWeight));
        Add("discourseAct", () => TrainSoftmax(_layout.DiscourseAct, Enum.GetValues<DiscourseAct>().Length,
            features, (int)example.Discourse.Act, learningRate));
        Add("discourseSubject", () => TrainSoftmax(_layout.DiscourseSubject,
            Enum.GetValues<DialogueParticipant>().Length, features, (int)example.Discourse.Subject, learningRate));
        Add("discourseTarget", () => TrainSoftmax(_layout.DiscourseTarget,
            Enum.GetValues<DialogueParticipant>().Length, features, (int)example.Discourse.Target, learningRate));
        Add("factKind", () => TrainSoftmax(_layout.FactKind, Enum.GetValues<DialogueFactKind>().Length + 1,
            features, example.Discourse.FactKind is { } kind ? (int)kind + 1 : 0, learningRate));
        Add("factPolarity", () => TrainSoftmax(_layout.FactPolarity, 2, features,
            example.Discourse.Negated ? 1 : 0, learningRate,
            example.Discourse.Negated ? MaximumPositiveWeight : 1.0));
        Add("factSpan", () => TrainFactSpan(example, learningRate));
        Add("antecedent", () => TrainAntecedent(example, learningRate, contextVector));
        Add("tool", () =>
        {
            var target = Math.Max(0, Array.IndexOf(_tools, example.ToolSchema));
            return TrainSoftmax(_layout.Tool, _tools.Length, features, target, learningRate,
                ToolTargetWeight(_tools[target]));
        });
        Add("slots", () => TrainSlots(example, learningRate * SlotLearningRateScale));
        Updates++;
        return loss / Math.Max(1, heads);

        void Add(string head, Func<double> train)
        {
            if (!example.SupervisedHeads.Contains(head) || _frozenHeads.Contains(head))
            {
                return;
            }

            loss += train();
            heads++;
        }
    }

    public double TrainDomainsOnly(
        TrainingExample example,
        double learningRate,
        IReadOnlyList<double>? contextVector,
        IReadOnlyList<double> domainPositiveWeights)
    {
        if (!example.SupervisedHeads.Contains("domains"))
        {
            return 0.0;
        }

        var loss = TrainMulti(_layout.Domain, Enum.GetValues<DialogueDomain>().Length,
            Features(example.Context, contextVector), example.Domains.Select(value => (int)value).ToHashSet(),
            learningRate, MaximumPositiveWeight, domainPositiveWeights);
        Updates++;
        return loss;
    }

    public double TrainToolOnly(
        TrainingExample example,
        double learningRate,
        IReadOnlyList<double>? contextVector)
    {
        if (!example.SupervisedHeads.Contains("tool") || _frozenHeads.Contains("tool"))
        {
            return 0.0;
        }

        var target = Math.Max(0, Array.IndexOf(_tools, example.ToolSchema));
        var loss = TrainSoftmax(_layout.Tool, _tools.Length, Features(example.Context, contextVector), target,
            learningRate, ToolTargetWeight(_tools[target]));
        Updates++;
        return loss;
    }

    public double TrainResponseOnly(
        TrainingExample example,
        double learningRate,
        IReadOnlyList<double>? contextVector)
    {
        if (!example.SupervisedHeads.Contains("responseCandidate") || _frozenHeads.Contains("responseCandidate"))
        {
            return 0.0;
        }

        var target = Math.Max(0, Array.IndexOf(_candidates, example.ResponseCandidateId));
        var loss = TrainSoftmax(_layout.Candidate, _candidates.Length, Features(example.Context, contextVector),
            target, learningRate, MaximumPositiveWeight);
        Updates++;
        return loss;
    }

    public double TrainDiscourseOnly(
        TrainingExample example,
        double learningRate,
        IReadOnlyList<double>? contextVector)
    {
        var features = Features(example.Context, contextVector);
        var heads = 0;
        var loss = 0.0;
        Add("policy", () => TrainSoftmax(_layout.Policy, Enum.GetValues<ResponsePolicy>().Length,
            features, (int)example.Policy, learningRate, MaximumPositiveWeight));
        Add("discourseAct", () => TrainSoftmax(_layout.DiscourseAct, Enum.GetValues<DiscourseAct>().Length,
            features, (int)example.Discourse.Act, learningRate));
        Add("discourseSubject", () => TrainSoftmax(_layout.DiscourseSubject,
            Enum.GetValues<DialogueParticipant>().Length, features, (int)example.Discourse.Subject, learningRate));
        Add("discourseTarget", () => TrainSoftmax(_layout.DiscourseTarget,
            Enum.GetValues<DialogueParticipant>().Length, features, (int)example.Discourse.Target, learningRate));
        Add("factKind", () => TrainSoftmax(_layout.FactKind, Enum.GetValues<DialogueFactKind>().Length + 1,
            features, example.Discourse.FactKind is { } kind ? (int)kind + 1 : 0, learningRate));
        Add("factPolarity", () => TrainSoftmax(_layout.FactPolarity, 2, features,
            example.Discourse.Negated ? 1 : 0, learningRate,
            example.Discourse.Negated ? MaximumPositiveWeight : 1.0));
        Add("factSpan", () => TrainFactSpan(example, learningRate));
        Add("antecedent", () => TrainAntecedent(example, learningRate, contextVector));
        Updates++;
        return loss / Math.Max(1, heads);

        void Add(string head, Func<double> train)
        {
            if (!example.SupervisedHeads.Contains(head) || _frozenHeads.Contains(head))
            {
                return;
            }

            loss += train();
            heads++;
        }
    }

    public double TrainBatch(
        IReadOnlyList<TrainingExample> examples,
        double learningRate,
        Func<TrainingExample, IReadOnlyList<double>> context,
        StructuredTrainingMode mode)
    {
        if (examples.Count == 0)
        {
            throw new ArgumentException("A structured minibatch cannot be empty.", nameof(examples));
        }

        var startingUpdates = Updates;
        var scaledRate = learningRate / examples.Count;
        var loss = 0.0;
        foreach (var example in examples)
        {
            loss += mode switch
            {
                StructuredTrainingMode.All => Train(example, scaledRate, Array.Empty<double>()),
                StructuredTrainingMode.Discourse => TrainDiscourseOnly(example, scaledRate, Array.Empty<double>()),
                StructuredTrainingMode.Ranking => TrainRanking(example, scaledRate, context(example)),
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
        }

        var contextual = examples
            .Select(example => (Example: example, Context: context(example)))
            .FirstOrDefault(item => item.Context.Count > 0);
        if (contextual.Context is not null && contextual.Context.Count > 0 && mode is not StructuredTrainingMode.Ranking)
        {
            loss += TrainContextOnly(
                contextual.Example,
                learningRate * 0.1,
                contextual.Context,
                discourseOnly: mode == StructuredTrainingMode.Discourse);
        }

        Updates = startingUpdates + 1;
        return loss / examples.Count;
    }

    private double TrainContextOnly(
        TrainingExample example,
        double learningRate,
        IReadOnlyList<double> contextVector,
        bool discourseOnly)
    {
        var features = Features(example.Context, contextVector);
        var heads = 0;
        var loss = 0.0;
        if (!discourseOnly)
        {
            Add("speechActs", () => TrainMulti(_layout.Speech, Enum.GetValues<SpeechAct>().Length, features,
                example.SpeechActs.Select(value => (int)value).ToHashSet(), learningRate,
                MaximumPositiveWeight, contextOnly: true));
            Add("domains", () => TrainMulti(_layout.Domain, Enum.GetValues<DialogueDomain>().Length, features,
                example.Domains.Select(value => (int)value).ToHashSet(), learningRate,
                MaximumPositiveWeight, contextOnly: true));
            Add("goals", () => TrainMulti(_layout.Goal, Enum.GetValues<DialogueGoal>().Length, features,
                example.Goals.Select(value => (int)value).ToHashSet(), learningRate,
                MaximumPositiveWeight, contextOnly: true));
            Add("affect", () => TrainSoftmax(_layout.Affect, Enum.GetValues<UserAffect>().Length, features,
                (int)example.Affect, learningRate, MaximumPositiveWeight, contextOnly: true));
            Add("stance", () => TrainSoftmax(_layout.Stance, Enum.GetValues<DialogueStance>().Length, features,
                (int)example.Stance, learningRate, MaximumPositiveWeight, contextOnly: true));
            Add("policy", () => TrainSoftmax(_layout.Policy, Enum.GetValues<ResponsePolicy>().Length, features,
                (int)example.Policy, learningRate, MaximumPositiveWeight, contextOnly: true));
            Add("content", () => TrainMulti(_layout.Content, Enum.GetValues<ContentFlag>().Length, features,
                example.ContentFlags.Select(value => (int)value).ToHashSet(), learningRate,
                MaximumPositiveWeight, contextOnly: true));
            Add("knowledgeTarget", () => TrainSoftmax(_layout.KnowledgeTarget,
                Enum.GetValues<KnowledgeTarget>().Length, features, (int)example.KnowledgeTarget,
                learningRate, MaximumPositiveWeight, contextOnly: true));
            Add("tool", () =>
            {
                var target = Math.Max(0, Array.IndexOf(_tools, example.ToolSchema));
                return TrainSoftmax(_layout.Tool, _tools.Length, features, target, learningRate,
                    ToolTargetWeight(_tools[target]), contextOnly: true);
            });
        }

        Add("discourseAct", () => TrainSoftmax(_layout.DiscourseAct, Enum.GetValues<DiscourseAct>().Length,
            features, (int)example.Discourse.Act, learningRate, contextOnly: true));
        Add("discourseSubject", () => TrainSoftmax(_layout.DiscourseSubject,
            Enum.GetValues<DialogueParticipant>().Length, features, (int)example.Discourse.Subject,
            learningRate, contextOnly: true));
        Add("discourseTarget", () => TrainSoftmax(_layout.DiscourseTarget,
            Enum.GetValues<DialogueParticipant>().Length, features, (int)example.Discourse.Target,
            learningRate, contextOnly: true));
        Add("factKind", () => TrainSoftmax(_layout.FactKind, Enum.GetValues<DialogueFactKind>().Length + 1,
            features, example.Discourse.FactKind is { } kind ? (int)kind + 1 : 0,
            learningRate, contextOnly: true));
        Add("factPolarity", () => TrainSoftmax(_layout.FactPolarity, 2, features,
            example.Discourse.Negated ? 1 : 0, learningRate,
            example.Discourse.Negated ? MaximumPositiveWeight : 1.0, contextOnly: true));
        Add("antecedent", () => TrainAntecedent(example, learningRate, contextVector, contextOnly: true));
        return loss / Math.Max(1, heads);

        void Add(string head, Func<double> train)
        {
            if (!example.SupervisedHeads.Contains(head) || _frozenHeads.Contains(head))
            {
                return;
            }

            loss += train();
            heads++;
        }
    }

    public void FreezePassingOperationalHeads(StructuredMetrics metrics)
    {
        Freeze("speechActs", metrics.SpeechActMacroF1 >= 0.85);
        Freeze("domains", metrics.DomainMacroF1 >= 0.84);
        Freeze("goals", metrics.GoalMacroF1 >= 0.80);
        Freeze("affect", metrics.AffectAccuracy >= 0.85);
        Freeze("stance", metrics.StanceAccuracy >= 0.85);
        Freeze("policy", metrics.PolicyAccuracy >= 0.90);
        Freeze("content", metrics.ContentMacroF1 >= 0.90);
        Freeze("slots", metrics.SlotSpanF1 >= 0.85);
        Freeze("tool", metrics.ToolAccuracy >= 0.95 && metrics.MutatingToolPrecision >= 0.97);
        Freeze("knowledgeTarget", metrics.KnowledgeTargetAccuracy >= 0.90);
        Freeze("responseCandidate", metrics.ResponseTop1 >= 0.85 && metrics.ResponseTop3 >= 0.95);

        void Freeze(string head, bool passing)
        {
            if (passing)
            {
                _frozenHeads.Add(head);
            }
        }
    }

    public double TrainRanking(
        TrainingExample example,
        double learningRate,
        IReadOnlyList<double>? contextVector = null)
    {
        if (!example.SupervisedHeads.Contains("responseCandidate") || _frozenHeads.Contains("responseCandidate"))
        {
            return 0.0;
        }

        var target = Array.IndexOf(_candidates, example.ResponseCandidateId);
        if (target < 0)
        {
            return 0.0;
        }

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

    public double TrainSlotsOnly(TrainingExample example, double learningRate)
    {
        if (!example.SupervisedHeads.Contains("slots"))
        {
            throw new ArgumentException("The auxiliary slot pass requires slot supervision.", nameof(example));
        }

        return _frozenHeads.Contains("slots")
            ? 0.0
            : TrainSlots(example, learningRate * SlotLearningRateScale);
    }

    private static double ToolTargetWeight(string tool) => tool switch
    {
        "NONE" => NoToolWeight,
        "BUY" or "SELL" => MutatingToolWeight,
        _ => ReadOnlyToolWeight
    };
}

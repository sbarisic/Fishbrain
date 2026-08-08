using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fishbrain;

public sealed partial class Brain
{
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
        brain._corpusHash = FileSha256(dataPath);
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
        var extendedCurriculum = false;
        if (File.Exists(fullCheckpointPath))
        {
            brain = Load(fullCheckpointPath);
            plannedSteps = brain.Config.PlannedSteps;
            if (requestedPlannedSteps is { } requested && requested != plannedSteps)
            {
                if (requested < plannedSteps || brain.CompletedSteps != plannedSteps)
                    throw new InvalidOperationException(
                        $"A {plannedSteps}-step curriculum can be extended only after completing step {plannedSteps}.");
                plannedSteps = requested;
                extendedCurriculum = true;
            }
        }
        else
        {
            plannedSteps = requestedPlannedSteps ?? 80_000;
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
        brain.Config.Validate();
        if (extendedCurriculum)
        {
            brain._bestPerceptionScore = -1.0;
            brain._bestPerceptionStep = 0;
        }
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
            StructuredLabelThresholds = _structuredHeads.SnapshotLabelThresholds(),
            ConfidenceCalibration = _confidenceCalibration,
            LabelSchemas = ModelSchemas.Labels,
            ToolSchemas = DemoGameTools.CreateMerchant().Schemas.OrderBy(schema => schema.Name).ToArray(),
            CandidateCatalog = ResponseCandidates.OrderBy(candidate => candidate.Id).ToArray(),
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
        TrainCurriculum(data, data, checkpointPath, plannedSteps, untilStep, recovery: null, corpusHash: "TEST");

    private void TrainCurriculum(
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
        if (language.Length == 0) throw new InvalidDataException("Teaching requires language samples.");
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
        if (families.Length == 0) throw new InvalidDataException("Teaching requires structured samples.");
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
            throw new InvalidDataException("Teaching requires slot-supervised samples.");
        var slotFamilies = Enumerable.Range(0, slotFamiliesBySource.Max(source => source.Length))
            .SelectMany(index => slotFamiliesBySource.Where(source => index < source.Length).Select(source => source[index]))
            .ToArray();
        var domainPositiveWeights = BalancedPositiveWeights(data.StructuredSamples, "domains",
            Enum.GetValues<DialogueDomain>().Length, example => example.Domains.Select(value => (int)value));
        var rareDomainFamilies = untilStep > HeadPolishStartStep
            ? FocusFamilies(example => example.Domains.Contains(DialogueDomain.Magic) ||
                                       example.Domains.Contains(DialogueDomain.VehicleTravel))
            : families;
        var explicitToolFamilies = untilStep > HeadPolishStartStep
            ? FocusFamilies(example => example.SupervisedHeads.Contains("tool") && example.ToolSchema != "NONE")
            : families;
        var hardNoToolFamilies = untilStep > HeadPolishStartStep
            ? FocusFamilies(example => example.SupervisedHeads.Contains("tool") && example.ToolSchema == "NONE" &&
                                       (example.Domains.Contains(DialogueDomain.TradeEconomy) ||
                                        example.Domains.Contains(DialogueDomain.ItemsInventory) ||
                                        example.Goals.Contains(DialogueGoal.Transaction)))
            : families;

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var intervalStart = timer.Elapsed;
        var intervalStep = _step;
        var lastSavedStep = -1;
        while (_step < untilStep)
        {
            double loss;
            string phase;
            var schedule = _step % 10;
            if (_step >= ResponsePolishStartStep)
            {
                if (schedule <= 7)
                {
                    phase = "RESPONSE_POLISH";
                    var responseStep = HeadPolishStructuredIndex(_step, ResponsePolishStartStep);
                    var family = families[responseStep % families.Length];
                    var example = family[DeterministicIndex(responseStep / families.Length,
                        family.Length, 4241)];
                    var learningRate = CurriculumLearningRate(_step, 0.14) * 8.0;
                    loss = _structuredHeads.TrainResponseOnly(example, learningRate, ContextVector(example.Context));
                    _step = checked(_step + 1);
                }
                else
                {
                    phase = "RESPONSE_RANK";
                    var rankStep = HeadPolishRankingIndex(_step, ResponsePolishStartStep);
                    var family = families[rankStep % families.Length];
                    var example = family[DeterministicIndex(rankStep / families.Length, family.Length, 4243)];
                    var learningRate = CurriculumLearningRate(_step, 0.10) * 8.0;
                    loss = _structuredHeads.TrainRanking(example, learningRate, ContextVector(example.Context));
                    _step = checked(_step + 1);
                }
            }
            else if (_step >= HeadPolishStartStep)
            {
                if (schedule <= 7)
                {
                    phase = "HEAD_POLISH";
                    var structuredStep = HeadPolishStructuredIndex(_step, HeadPolishStartStep);
                    var position = structuredStep % 8;
                    var cycle = structuredStep / 8;
                    var (selectedFamilies, selectedStep, seed) = position switch
                    {
                        0 => (rareDomainFamilies, cycle * 2, 4201),
                        4 => (rareDomainFamilies, cycle * 2 + 1, 4201),
                        1 => (explicitToolFamilies, cycle, 4207),
                        5 => (hardNoToolFamilies, cycle, 4211),
                        2 => (families, cycle * 4, 4217),
                        3 => (families, cycle * 4 + 1, 4217),
                        6 => (families, cycle * 4 + 2, 4217),
                        _ => (families, cycle * 4 + 3, 4217)
                    };
                    var family = selectedFamilies[selectedStep % selectedFamilies.Length];
                    var example = family[DeterministicIndex(selectedStep / selectedFamilies.Length,
                        family.Length, seed)];
                    var learningRate = CurriculumLearningRate(_step, 0.14) * 8.0;
                    var context = ContextVector(example.Context);
                    loss = position switch
                    {
                        0 or 4 => _structuredHeads.TrainDomainsOnly(
                            example, learningRate, context, domainPositiveWeights),
                        1 or 5 => _structuredHeads.TrainToolOnly(example, learningRate, context),
                        _ => _structuredHeads.Train(example, learningRate, context, domainPositiveWeights)
                    };
                    if (position is 0 or 1 or 4 or 5)
                    {
                        var responsePosition = position switch { 0 => 0, 1 => 1, 4 => 2, _ => 3 };
                        var responseStep = cycle * 4 + responsePosition;
                        var responseFamily = families[responseStep % families.Length];
                        var responseExample = responseFamily[DeterministicIndex(
                            responseStep / families.Length, responseFamily.Length, 4231)];
                        loss = (loss + _structuredHeads.TrainResponseOnly(responseExample, learningRate,
                            ContextVector(responseExample.Context))) * 0.5;
                    }
                    var slotFamily = slotFamilies[structuredStep % slotFamilies.Length];
                    var slotExample = slotFamily[DeterministicIndex(structuredStep / slotFamilies.Length,
                        slotFamily.Length, 4217)];
                    loss = (loss + _structuredHeads.TrainSlotsOnly(slotExample, learningRate)) * 0.5;
                    _step = checked(_step + 1);
                }
                else
                {
                    phase = "HEAD_RANK";
                    var rankStep = HeadPolishRankingIndex(_step, HeadPolishStartStep);
                    var family = families[rankStep % families.Length];
                    var example = family[DeterministicIndex(rankStep / families.Length, family.Length, 4229)];
                    var learningRate = CurriculumLearningRate(_step, 0.10) * 8.0;
                    loss = _structuredHeads.TrainRanking(example, learningRate, ContextVector(example.Context));
                    _step = checked(_step + 1);
                }
            }
            else if (schedule <= 6)
            {
                phase = "STRUCTURED";
                var structuredStep = StructuredCurriculumIndex(_step);
                var family = families[structuredStep % families.Length];
                var example = family[DeterministicIndex(structuredStep / families.Length,
                    family.Length, 1009)];
                var learningRate = CurriculumLearningRate(_step, 0.14);
                var context = ContextVector(example.Context);
                loss = _structuredHeads.Train(example, learningRate, context);
                var slotFamily = slotFamilies[structuredStep % slotFamilies.Length];
                var slotExample = slotFamily[DeterministicIndex(structuredStep / slotFamilies.Length,
                    slotFamily.Length, 1877)];
                loss = (loss + _structuredHeads.TrainSlotsOnly(slotExample, learningRate)) * 0.5;
                var contextualLoss = CalculateLoss(ContextTrainingSample(example));
                ApplyGradients(plannedSteps);
                loss = (loss + contextualLoss) * 0.5;
            }
            else if (schedule <= 8)
            {
                phase = "RANKING";
                var rankStep = RankingCurriculumIndex(_step);
                var family = families[rankStep % families.Length];
                var example = family[DeterministicIndex(rankStep / families.Length, family.Length, 3253)];
                var learningRate = CurriculumLearningRate(_step, 0.10);
                loss = _structuredHeads.TrainRanking(example, learningRate, ContextVector(example.Context));
                var contextualLoss = CalculateLoss(ContextTrainingSample(example));
                ApplyGradients(plannedSteps);
                loss = (loss + contextualLoss) * 0.5;
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
            var fullStage = _step == plannedSteps || _step % 20_000 == 0;
            var evaluationExamples = fullStage
                ? validation.StructuredSamples
                : StratifiedMilestoneSample(validation.StructuredSamples, 128, Config.Seed);
            var contextVectors = new double[evaluationExamples.Count][];
            Parallel.For(0, evaluationExamples.Count,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) },
                index => contextVectors[index] = ContextVector(evaluationExamples[index].Context));
            var contextByExample = Enumerable.Range(0, evaluationExamples.Count).ToDictionary(
                index => EvaluationExampleKey(evaluationExamples[index]),
                index => (IReadOnlyList<double>)contextVectors[index], StringComparer.Ordinal);
            _confidenceCalibration = _structuredHeads.Calibrate(evaluationExamples,
                example => contextByExample[EvaluationExampleKey(example)]);
            var metrics = _structuredHeads.Evaluate(evaluationExamples,
                example => contextByExample[EvaluationExampleKey(example)]);
            var realizationLoss = DebugAverageLoss(validation.LanguageSamples.Take(64));
            var productionEligible = double.IsFinite(metrics.Composite) &&
                                     CompositionalHeadModel.MeetsReleaseNeuralThresholds(metrics);
            var bestStructured = fullStage && productionEligible && metrics.Composite > _bestPerceptionScore;
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
            Console.WriteLine($"BEST PRODUCTION {_bestPerceptionScore:F4} AT {_bestPerceptionStep} " +
                              $"GENERATION {_bestRealizationLoss:F4} AT {_bestRealizationStep}");

            Save(checkpointPath);
            if (bestStructured) Save(CheckpointRolePath(checkpointPath, "best-production"));
            if (bestGeneration) Save(CheckpointRolePath(checkpointPath, "best-generation"));
            var now = timer.Elapsed;
            WriteTrainingTelemetry(checkpointPath, corpusHash, validation.StructuredSamples, metrics, realizationLoss,
                _step - intervalStep, now - intervalStart, fullStage);
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
            var bestPath = CheckpointRolePath(checkpointPath, "best-production");
            if (_bestPerceptionStep == 0 || !File.Exists(bestPath))
                throw new InvalidDataException("Training completed without an eligible best production checkpoint.");
            var output = Path.Combine(Path.GetDirectoryName(recovery.CorpusDirectory)!, "models", "model-latest.fbm");
            Load(bestPath).ExportInference(output, corpusHash);
            Console.WriteLine($"EXPORTED BEST PRODUCTION CHECKPOINT {output}");
        }
        if (recovery is not null) PrintMilestoneCommands(recovery);

        TrainingExample[][] FocusFamilies(Func<TrainingExample, bool> predicate)
        {
            var selected = data.StructuredSamples.Where(predicate)
                .GroupBy(example => example.SemanticFamilyId, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.OrderBy(example => example.Input, StringComparer.Ordinal).ToArray())
                .ToArray();
            if (selected.Length == 0) throw new InvalidDataException("A required head-polish focus set is empty.");
            return selected;
        }
    }

    private TrainingSample ContextTrainingSample(TrainingExample example)
    {
        var encoded = _tokenizer.Encode(example.Context);
        var maximum = Math.Max(1, Config.ContextLength - 2);
        if (encoded.Length > maximum) encoded = encoded[^maximum..];
        var tokens = new int[encoded.Length + 2];
        tokens[0] = Tokenizer.Bos;
        Array.Copy(encoded, 0, tokens, 1, encoded.Length);
        tokens[^1] = Tokenizer.Sep;
        var intent = example.SpeechActs.FirstOrDefault() switch
        {
            SpeechAct.Greet => DialogueIntent.Greeting,
            SpeechAct.Farewell => DialogueIntent.Farewell,
            SpeechAct.Thank => DialogueIntent.Gratitude,
            SpeechAct.Apologize => DialogueIntent.Apology,
            SpeechAct.Refuse => DialogueIntent.Refusal,
            SpeechAct.Order => DialogueIntent.Directive,
            SpeechAct.Correct => DialogueIntent.Clarification,
            SpeechAct.Threaten or SpeechAct.Challenge => DialogueIntent.Hostility,
            _ when example.Domains.Contains(DialogueDomain.LocationNavigation) => DialogueIntent.LocationInquiry,
            _ when example.Domains.Contains(DialogueDomain.TradeEconomy) => DialogueIntent.TradeRequest,
            _ => DialogueIntent.Statement
        };
        var target = new TurnPerception(intent, example.Affect, example.Policy != ResponsePolicy.NoResponse);
        return new TrainingSample(tokens, 0, 1, TrainingTask.Perception,
            $"STRUCTURED|{example.Source}|{example.ResponseCandidateId}", example.Source, target,
            example.SemanticFamilyId, PerceptionFields.All);
    }

    private static IReadOnlyList<TrainingExample> StratifiedMilestoneSample(
        IReadOnlyList<TrainingExample> examples, int maximum, int step)
    {
        if (examples.Count <= maximum) return examples;
        var groups = examples.GroupBy(example => example.Source, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new Queue<TrainingExample>(group
                .OrderBy(example => StableTrainingKey(step, example.SemanticFamilyId), StringComparer.Ordinal)))
            .ToArray();
        var result = new List<TrainingExample>(maximum);
        while (result.Count < maximum && groups.Any(group => group.Count > 0))
            foreach (var group in groups)
                if (group.Count > 0 && result.Count < maximum) result.Add(group.Dequeue());
        return result;
    }

    private static string StableTrainingKey(int seed, string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}|{value}")));

    internal static double CurriculumLearningRate(int step, double initialRate) =>
        initialRate * Math.Pow(0.5, step / 20_000.0);

    internal static int StructuredCurriculumIndex(int step)
    {
        var position = step % 10;
        if (step < 0 || position > 6) throw new ArgumentOutOfRangeException(nameof(step));
        return checked(step / 10 * 7 + position);
    }

    internal static int RankingCurriculumIndex(int step)
    {
        var position = step % 10;
        if (step < 0 || position is < 7 or > 8) throw new ArgumentOutOfRangeException(nameof(step));
        return checked(step / 10 * 2 + position - 7);
    }

    internal static int HeadPolishStructuredIndex(int step, int startStep)
    {
        var localStep = step - startStep;
        var position = localStep % 10;
        if (localStep < 0 || position > 7) throw new ArgumentOutOfRangeException(nameof(step));
        return checked(localStep / 10 * 8 + position);
    }

    internal static int HeadPolishRankingIndex(int step, int startStep)
    {
        var localStep = step - startStep;
        var position = localStep % 10;
        if (localStep < 0 || position is < 8 or > 9) throw new ArgumentOutOfRangeException(nameof(step));
        return checked(localStep / 10 * 2 + position - 8);
    }

    internal static double[] BalancedPositiveWeights(
        IReadOnlyList<TrainingExample> examples, string head, int classCount,
        Func<TrainingExample, IEnumerable<int>> labels)
    {
        var supervised = examples.Where(example => example.SupervisedHeads.Contains(head)).ToArray();
        if (supervised.Length == 0 || classCount <= 0)
            throw new ArgumentException("Balanced label weights require supervised examples and classes.");
        var counts = new int[classCount];
        foreach (var label in supervised.SelectMany(labels).Distinct())
        {
            if (label < 0 || label >= classCount) throw new InvalidDataException("A balanced label is out of range.");
        }
        foreach (var example in supervised)
            foreach (var label in labels(example).Distinct()) counts[label]++;
        return counts.Select(count => count == 0 ? 1.0 :
            Math.Clamp(Math.Sqrt((double)supervised.Length / count), 2.0, 16.0)).ToArray();
    }

    private static string EvaluationExampleKey(TrainingExample example) =>
        $"{example.Source}\u001f{example.SemanticFamilyId}\u001f{example.Context}";

    private void WriteTrainingTelemetry(
        string checkpointPath, string corpusHash, IReadOnlyList<TrainingExample> validation,
        StructuredMetrics metrics, double generationLoss,
        int intervalSteps, TimeSpan elapsed, bool fullStage)
    {
        var path = Path.Combine(Program.TelemetryDirectory(checkpointPath), "training.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var checkpointHash = FileSha256(checkpointPath);
        var responseSources = new Dictionary<ResponseSource, int>();
        var tools = DemoGameTools.CreateMerchant();
        foreach (var example in validation
                     .OrderBy(item => item.SemanticFamilyId, StringComparer.Ordinal).Take(32))
        {
            var result = Reply(new ReplyRequest("TRAINING-TELEMETRY", $"STEP-{_step}-{responseSources.Values.Sum()}",
                [new DialogueTurn(DialogueRole.Player, example.Input)], NpcDialogueState.Initial,
                NpcPersona.Default, Config.Seed), tools);
            responseSources[result.Diagnostics.ResponseSource] =
                responseSources.GetValueOrDefault(result.Diagnostics.ResponseSource) + 1;
        }
        var payload = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            milestone = fullStage ? "TRAINING_STAGE" : "TRAINING_CHECKPOINT",
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
}


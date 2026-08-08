using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fishbrain;

public sealed partial class Brain
{
    private static string ComputeCorpusHash(string directory)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var name in new[] { "train.jsonl", "validation.jsonl", "test.jsonl" })
        {
            using var stream = File.OpenRead(Path.Combine(directory, name));
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private ValidationMetrics EvaluateValidation(TrainingData validation)
    {
        SyncScalarWeights();
        var intentSamples = validation.PerceptionSamples
            .Where(sample => sample.TargetFields.HasFlag(PerceptionFields.Intent)).Take(512).ToArray();
        var affectSamples = validation.PerceptionSamples
            .Where(sample => sample.TargetFields.HasFlag(PerceptionFields.Affect)).Take(512).ToArray();
        var expectedSamples = validation.PerceptionSamples
            .Where(sample => sample.TargetFields.HasFlag(PerceptionFields.Expected)).Take(512).ToArray();
        var intentPredicted = intentSamples.Select(PredictPerceptionSample).ToArray();
        var affectPredicted = affectSamples.Select(PredictPerceptionSample).ToArray();
        var expectedPredicted = expectedSamples.Select(PredictPerceptionSample).ToArray();
        var realizationLoss = DebugAverageLoss(validation.LanguageSamples.Take(64));
        return new ValidationMetrics(
            MacroF1(intentSamples.Select(sample => sample.PerceptionTarget!.Intent), intentPredicted.Select(value => value.Intent)),
            MacroF1(affectSamples.Select(sample => sample.PerceptionTarget!.Affect), affectPredicted.Select(value => value.Affect)),
            BinaryF1(expectedSamples.Select(sample => sample.PerceptionTarget!.ResponseExpected), expectedPredicted.Select(value => value.ResponseExpected)),
            SubsetIntentMacroF1(intentSamples, intentPredicted, sample => sample.Family.EndsWith("_DIRECT", StringComparison.Ordinal)),
            SubsetIntentMacroF1(intentSamples, intentPredicted, sample => sample.Family.EndsWith("_HISTORY", StringComparison.Ordinal)),
            realizationLoss);
    }

    private TurnPerception PredictPerceptionSample(TrainingSample sample)
    {
        using var _ = Value.NoGrad();
        var representation = ForwardLastHidden(sample.Tokens, sample.PositionOffset);
        return new TurnPerception(
            (DialogueIntent)ArgMax(Linear(representation, _intentHead)),
            (UserAffect)ArgMax(Linear(representation, _affectHead)),
            ArgMax(Linear(representation, _expectedHead)) == 1);
    }

    private static double SubsetIntentMacroF1(
        IReadOnlyList<TrainingSample> samples,
        IReadOnlyList<TurnPerception> predicted,
        Func<TrainingSample, bool> include)
    {
        var indices = Enumerable.Range(0, samples.Count).Where(index => include(samples[index])).ToArray();
        return indices.Length == 0
            ? double.NaN
            : MacroF1(indices.Select(index => samples[index].PerceptionTarget!.Intent),
                indices.Select(index => predicted[index].Intent));
    }

    private static double MacroF1<T>(IEnumerable<T> expectedValues, IEnumerable<T> predictedValues) where T : struct, Enum
    {
        var pairs = expectedValues.Zip(predictedValues).ToArray();
        if (pairs.Length == 0) return double.NaN;
        return pairs.Select(pair => pair.First).Distinct().Select(label =>
        {
            var tp = pairs.Count(pair => pair.First.Equals(label) && pair.Second.Equals(label));
            var fp = pairs.Count(pair => !pair.First.Equals(label) && pair.Second.Equals(label));
            var fn = pairs.Count(pair => pair.First.Equals(label) && !pair.Second.Equals(label));
            return 2.0 * tp / Math.Max(1, 2 * tp + fp + fn);
        }).Average();
    }

    private static double BinaryF1(IEnumerable<bool> expectedValues, IEnumerable<bool> predictedValues)
    {
        var pairs = expectedValues.Zip(predictedValues).ToArray();
        var tp = pairs.Count(pair => pair.First && pair.Second);
        var fp = pairs.Count(pair => !pair.First && pair.Second);
        var fn = pairs.Count(pair => pair.First && !pair.Second);
        return 2.0 * tp / Math.Max(1, 2 * tp + fp + fn);
    }

    private static string CheckpointRolePath(string checkpointPath, string role)
    {
        var directory = Path.GetDirectoryName(checkpointPath) ?? "";
        var extension = Path.GetExtension(checkpointPath);
        var stem = Path.GetFileNameWithoutExtension(checkpointPath);
        if (stem.EndsWith("-latest", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^"-latest".Length];
        return Path.Combine(directory, $"{stem}-{role}{extension}");
    }

    private sealed record ValidationMetrics(
        double IntentMacroF1,
        double AffectMacroF1,
        double ExpectedF1,
        double DirectIntentMacroF1,
        double HistoryIntentMacroF1,
        double RealizationLoss);

    private static TrainingSample[][] PerceptionBuckets(
        IEnumerable<TrainingSample> samples, Func<TrainingSample, string> key) =>
        samples.GroupBy(key, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.ToArray())
            .ToArray();

    private static TrainingSample BalancedPerception(TrainingSample[][][] dimensions, int index)
    {
        var buckets = dimensions[index % dimensions.Length];
        var dimensionIndex = index / dimensions.Length;
        var bucket = buckets[dimensionIndex % buckets.Length];
        return bucket[DeterministicIndex(dimensionIndex / buckets.Length, bucket.Length, 211)];
    }

    private static int DeterministicIndex(int step, int count, int salt)
    {
        var random = new DeterministicRandom(unchecked(step * 7919 + salt));
        return random.NextInt(count);
    }

    private int[] EpochOrder(int count, int epoch)
    {
        var order = Enumerable.Range(0, count).ToArray();
        var random = new DeterministicRandom(unchecked(Config.Seed + epoch * 7919));
        for (var i = order.Length - 1; i > 0; i--)
        {
            var other = random.NextInt(i + 1);
            (order[i], order[other]) = (order[other], order[i]);
        }
        return order;
    }
}

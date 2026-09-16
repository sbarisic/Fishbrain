using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Fishbrain.Neural;

namespace Fishbrain;

internal static class ContextualTrainingBenchmark
{
    // Each invocation starts from the same durable weights, optimizer, sampler and RNG.
    // Updates affect only this process; the source checkpoint is never overwritten.
    internal static void Run(string corpus, string checkpoint, string reportPath, int updates)
    {
        if (updates is < 1 or > 100) throw new ArgumentException("Benchmark updates must be within 1-100.");
        if (Path.GetFullPath(checkpoint).Equals(Path.GetFullPath(reportPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The benchmark report must not replace the checkpoint.");
        var sampleWorkers = ContextualTraining.ConfiguredSampleWorkers();
        using var source = File.OpenRead(checkpoint);
        var checkpointHash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        var loaded = ContextualCheckpoint.Load(checkpoint, DemoDialogueDomains.Merchant, training: true);
        if (loaded.Header.CorpusHash != ContextualTraining.CorpusHash(corpus)) throw new InvalidDataException("Benchmark corpus differs from checkpoint.");
        var data = TrainingData.Load(Path.Combine(corpus, "train.jsonl"), loaded.Model.Tokenizer).StructuredSamples;
        var results = new List<object>();
        foreach (var phase in Enum.GetValues<ContextualPhase>())
        {
            var trainer = ContextualTrainer.Restore(loaded, sampleWorkers);
            var pool = ContextualTraining.Families(phase is ContextualPhase.JointRealization or ContextualPhase.DecoderPolish
                ? data.Where(ContextualTrainer.ProjectResponse) : data);
            var times = new List<double>();
            var losses = new List<float>();
            long allocated = 0;
            for (var update = -1; update < updates; update++)
            {
                var batch = Enumerable.Range(0, 32).Select(i => ContextualTraining.Select(pool,
                    checked((long)trainer.Step * 32 + i), trainer.Architecture.Config.Seed)).ToArray();
                var before = GC.GetTotalAllocatedBytes(true);
                var timer = Stopwatch.StartNew();
                var loss = trainer.TrainBatch(batch, phase);
                timer.Stop();
                if (update >= 0)
                {
                    times.Add(timer.Elapsed.TotalSeconds);
                    losses.Add(loss);
                    allocated += GC.GetTotalAllocatedBytes(true) - before;
                }
                Console.WriteLine($"BENCHMARK {phase} UPDATE {update + 1} SECONDS {timer.Elapsed.TotalSeconds:F3} LOSS {loss:F6}");
            }
            results.Add(new
            {
                Phase = phase.ToString(),
                Seconds = times,
                Losses = losses,
                MeanSeconds = times.Average(),
                AllocatedBytesPerUpdate = allocated / updates
            });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            Checkpoint = Path.GetFullPath(checkpoint),
            CheckpointSha256 = checkpointHash,
            loaded.Header.CompletedSteps,
            loaded.Header.CorpusHash,
            EffectiveBatchSize = 32,
            WarmupUpdates = 1,
            ProcessorCount = Environment.ProcessorCount,
            SampleWorkers = sampleWorkers,
            Results = results
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

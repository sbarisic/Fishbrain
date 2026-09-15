using System.Text.Json;
using Fishbrain.Neural;

namespace Fishbrain;

internal static class ContextualComparison
{
    private sealed record Row(string Family, double? ContextualDomain, double? LexicalDomain, double? WithoutMemoryDomain,
        double? ContextualDiscourse, double? LexicalDiscourse, double? WithoutMemoryDiscourse,
        double? ContextualFrame, double? WithoutMemoryFrame, double? ContextualPlan, double? WithoutMemoryPlan);

    internal static void Run(string corpus, string checkpoint, string report, int epochs)
    {
        if (epochs is < 1 or > 100) throw new ArgumentException("Lexical baseline epochs must be within 1-100.");
        var loaded = ContextualCheckpoint.Load(checkpoint, DemoDialogueDomains.Merchant);
        if (loaded.Header.CorpusHash != ContextualTraining.CorpusHash(corpus)) throw new InvalidDataException("Comparison corpus does not match the model.");
        var train = TrainingData.Load(Path.Combine(corpus, "train.jsonl"), loaded.Model.Tokenizer).StructuredSamples;
        var validation = TrainingData.Load(Path.Combine(corpus, "validation.jsonl"), loaded.Model.Tokenizer).StructuredSamples;
        var test = TrainingData.Load(Path.Combine(corpus, "test.jsonl"), loaded.Model.Tokenizer).StructuredSamples;
        var lexical = new CompositionalHeadModel(loaded.Model.Domain.Tools.Select(t => t.Schema.Name), ResponseCatalog.Plans.Select(p => p.Id), 42);
        var families = ContextualTraining.Families(train);
        for (long i = 0; i < (long)epochs * train.Count; i++)
        {
            lexical.Train(ContextualTraining.Select(families, i, 42), .03);
            if ((i + 1) % 10000 == 0) Console.WriteLine($"LEXICAL BASELINE ROWS {i + 1}");
        }
        _ = lexical.Calibrate(validation);
        var brain = Brain.CreateContextualForEvaluation(loaded.Model, loaded.Header.CompletedSteps, loaded.Header.ExecutionThresholds);
        var rows = new List<Row>();
        foreach (var example in test)
        {
            var request = example.Request! with { ResponseMode = ResponseMode.DeterministicOnly };
            var full = brain.Reply(request, DemoGameTools.CreateMerchant());
            var without = brain.Reply(request with
            {
                PlayerProfile = PlayerConversationProfile.Empty,
                State = request.State with { SessionFacts = [], TopicSummaries = [] }
            }, DemoGameTools.CreateMerchant());
            var baseline = lexical.Predict(example.Context, [], currentInput: example.Input, utterances: example.Turns);
            rows.Add(new(example.SemanticFamilyId, Domain(full.RawPerception), Domain(baseline), Domain(without.RawPerception),
                Discourse(full.RawPerception), Discourse(baseline), Discourse(without.RawPerception),
                example.Contextual?.Frames is { } f ? Bool(ContextualEvaluation.FramesEqual(f, full.Contextual!.Frames)) : null,
                example.Contextual?.Frames is { } wf ? Bool(ContextualEvaluation.FramesEqual(wf, without.Contextual!.Frames)) : null,
                example.Contextual?.Plan is { } p ? Bool(p.SequenceEqual(full.Contextual!.Acts)) : null,
                example.Contextual?.Plan is { } wp ? Bool(wp.SequenceEqual(without.Contextual!.Acts)) : null));
            double? Domain(StructuredPerception actual) => !example.SupervisedHeads.Contains("domains") ? null : example.Domains.ToHashSet().SetEquals(actual.Domains) ? 1 : 0;
            double? Discourse(StructuredPerception actual) => !example.SupervisedHeads.Contains("discourseAct") ? null : actual.Discourse is { } d &&
                (d.Act, d.Subject, d.Target, d.FactKind, d.Negated, d.FactValueSpan, d.AntecedentUtterance) ==
                (example.Discourse.Act, example.Discourse.Subject, example.Discourse.Target, example.Discourse.FactKind,
                    example.Discourse.Negated, example.Discourse.FactValueSpan, example.Discourse.AntecedentUtterance) ? 1 : 0;
        }
        var results = new Dictionary<string, object>
        {
            ["ContextualMinusLexicalDomainExact"] = Paired(rows, r => r.ContextualDomain, r => r.LexicalDomain),
            ["ContextualMinusLexicalDiscourseExact"] = Paired(rows, r => r.ContextualDiscourse, r => r.LexicalDiscourse),
            ["ContextualMinusMemoryDisabledDomainExact"] = Paired(rows, r => r.ContextualDomain, r => r.WithoutMemoryDomain),
            ["ContextualMinusMemoryDisabledDiscourseExact"] = Paired(rows, r => r.ContextualDiscourse, r => r.WithoutMemoryDiscourse),
            ["ContextualMinusMemoryDisabledFrameExact"] = Paired(rows, r => r.ContextualFrame, r => r.WithoutMemoryFrame),
            ["ContextualMinusMemoryDisabledPlanExact"] = Paired(rows, r => r.ContextualPlan, r => r.WithoutMemoryPlan)
        };
        var json = JsonSerializer.Serialize(new
        {
            Seed = 42,
            LexicalEpochs = epochs,
            TrainingRows = train.Count,
            TestRows = test.Count,
            loaded.Header.CorpusHash,
            loaded.Header.CompletedSteps,
            Method = "Paired semantic-family bootstrap; 2000 draws; equal family weights; percentile 95% interval.",
            MemoryDisabled = "Approved facts, session facts, and topic summaries removed; identical dialogue, persona and agenda retained.",
            LexicalScope = "Hashed lexical heads without transformer features. No frame or planner head exists in this baseline.",
            Results = results
        }, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        File.WriteAllText(report, json);
        Console.WriteLine(json);
        static double Bool(bool value) => value ? 1 : 0;
    }

    private static object Paired(IReadOnlyList<Row> rows, Func<Row, double?> contextual, Func<Row, double?> comparison)
    {
        var families = rows.Where(r => contextual(r).HasValue && comparison(r).HasValue).GroupBy(r => r.Family)
            .Select(g => (Full: g.Average(r => contextual(r)!.Value), Other: g.Average(r => comparison(r)!.Value))).ToArray();
        if (families.Length == 0) return new { Available = false };
        var random = new Random(42);
        var draws = new double[2000];
        for (var i = 0; i < draws.Length; i++)
        {
            double sum = 0;
            for (var j = 0; j < families.Length; j++) { var family = families[random.Next(families.Length)]; sum += family.Full - family.Other; }
            draws[i] = sum / families.Length;
        }
        Array.Sort(draws);
        return new
        {
            Families = families.Length,
            Contextual = families.Average(f => f.Full),
            Comparison = families.Average(f => f.Other),
            Difference = families.Average(f => f.Full - f.Other),
            Lower95 = draws[50],
            Upper95 = draws[1949]
        };
    }
}

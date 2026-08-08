using System.IO.Compression;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fishbrain;

namespace Fishbrain.DataGenerator;

internal sealed record CorpusRow(
    string Input,
    NpcState State,
    TurnPerception Perception,
    ResponseAction Action,
    string? Response,
    string Source,
    string Split,
    string GroupId,
    string Family,
    string SemanticFamilyId,
    string SourceLicense,
    string SourceRevision,
    string SourceChecksum,
    StructuredPerception StructuredPerception,
    string[] SupervisedHeads,
    DialogueTurn[]? Turns = null,
    NpcDialogueState? InitialDialogueState = null,
    NpcPersona? Persona = null,
    string? ResponsePlanId = null,
    string[]? PositiveVariationIds = null,
    string[]? RejectedVariationIds = null,
    string? ToolTarget = null,
    Dictionary<string, string>? ToolArguments = null,
    string? SourceUrl = null,
    string? Attribution = null);

internal static partial class CorpusCompiler
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) }
    };
    private static readonly IReadOnlyDictionary<string, int> RequiredSources = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["PROJECT_CONTRAST"] = 12_000,
        ["PROJECT_FANTASY"] = 8_000,
        ["PROJECT_SCIFI"] = 8_000,
        ["PROJECT_PERSONA_MEMORY"] = 4_000,
        ["PROJECT_TOOL_WORLD"] = 4_000,
        ["TASKMASTER1"] = 2_000,
        ["TASKMASTER2"] = 1_000,
        ["TASKMASTER3"] = 1_000,
        ["MULTIWOZ24"] = 3_000,
        ["ABCD"] = 3_000,
        ["BANKING77"] = 1_000,
        ["NLUPP"] = 1_000,
        ["CLINC150"] = 1_000,
        ["SLURP_TEXT"] = 1_000,
        ["MASSIVE_EN"] = 1_000,
        ["OASST1"] = 1_000,
        ["OASST2"] = 2_000,
        ["GOEMOTIONS"] = 2_000,
        ["CIVIL_COMMENTS"] = 3_000,
        ["HH_RLHF"] = 1_000
    };
    private static readonly HashSet<string> CommercialLicenses = new(StringComparer.OrdinalIgnoreCase)
    {
        "PROJECT-OWNED", "MIT", "APACHE-2.0", "CC-BY-4.0", "CC-BY-3.0", "CC0-1.0"
    };
    private static readonly HashSet<string> KnownToolTargets = DemoGameTools.CreateMerchant().Schemas
        .Select(schema => schema.Name).Append("NONE").ToHashSet(StringComparer.Ordinal);

    public static void Compile(CliOptions options)
    {
        if (options.Count != 60_000) throw new ArgumentException("The corpus must contain exactly 60,000 rows.");
        var manifest = ReadManifest(options.ManifestPath);
        VerifyManifestAndRaw(manifest, options.RawPath);
        var context = new CompilationContext(LoadHeldOutInputs(options.OutputPath));
        var definitions = manifest.Sources.ToDictionary(source => source.Name, StringComparer.Ordinal);
        var rows = new List<CorpusRow>(60_000);
        rows.AddRange(ProjectRows("PROJECT_CONTRAST", 12_000, "CONTRAST", options.Seed));
        rows.AddRange(ProjectRows("PROJECT_FANTASY", 8_000, "FANTASY", options.Seed + 11));
        rows.AddRange(ProjectRows("PROJECT_SCIFI", 8_000, "SCIFI", options.Seed + 23));
        rows.AddRange(ProjectRows("PROJECT_PERSONA_MEMORY", 4_000, "PERSONA", options.Seed + 37));
        rows.AddRange(ProjectRows("PROJECT_TOOL_WORLD", 4_000, "GAME", options.Seed + 43));
        rows.AddRange(LoadTaskmaster(Path.Combine(options.RawPath, "taskmaster1-self-dialogs.json"), 2_000, definitions["TASKMASTER1"], context));
        rows.AddRange(LoadTaskmaster(Path.Combine(options.RawPath, "taskmaster2-food.json"), 1_000, definitions["TASKMASTER2"], context));
        rows.AddRange(LoadTaskmaster(Path.Combine(options.RawPath, "taskmaster3-00.json"), 1_000, definitions["TASKMASTER3"], context));
        rows.AddRange(LoadMultiWoz(Path.Combine(options.RawPath, "multiwoz24.zip"), 3_000, definitions["MULTIWOZ24"], context));
        rows.AddRange(LoadAbcd(Path.Combine(options.RawPath, "abcd-v1.1.json.gz"), 3_000, definitions["ABCD"], context));
        rows.AddRange(LoadBanking(Path.Combine(options.RawPath, "banking77-train.csv"), 1_000, definitions["BANKING77_NLUPP"], context));
        rows.AddRange(LoadNlupp(Path.Combine(options.RawPath, "nlupp.zip"), 1_000, definitions["BANKING77_NLUPP"], context));
        rows.AddRange(LoadClinc(Path.Combine(options.RawPath, "clinc150.json"), 1_000, definitions["CLINC150"], context));
        rows.AddRange(LoadSlurp(Path.Combine(options.RawPath, "slurp-train.jsonl"), 1_000, definitions["SLURP_TEXT"], context));
        rows.AddRange(LoadMassive(Path.Combine(options.RawPath, "massive-1.1.tar.gz"), 1_000, definitions["MASSIVE_EN"], context));
        rows.AddRange(LoadOasst(Path.Combine(options.RawPath, "oasst1.jsonl.gz"), 1_000, definitions["OASST1"], context));
        rows.AddRange(LoadOasst(Path.Combine(options.RawPath, "oasst2-ready.jsonl.gz"), 2_000, definitions["OASST2"], context));
        rows.AddRange(LoadGoEmotions(options.RawPath, 2_000, definitions["GOEMOTIONS"], context));
        rows.AddRange(LoadCivil(Path.Combine(options.RawPath, "civil-comments-selected.jsonl"), 3_000, definitions["CIVIL_COMMENTS"], context));
        rows.AddRange(LoadHhRlhf(Path.Combine(options.RawPath, "hh-helpful-base-train.jsonl.gz"), 1_000, definitions["HH_RLHF"], context));
        if (rows.Count != 60_000) throw new InvalidDataException($"Compilation produced {rows.Count} rows.");

        EnsureUniqueAndConsistent(rows);
        AuditProjectDiversity(rows);
        AssignSplits(rows, options.Seed);
        Directory.CreateDirectory(options.OutputPath);
        foreach (var split in new[] { "train", "validation", "test" })
            AtomicJsonl(Path.Combine(options.OutputPath, split + ".jsonl"), rows.Where(row => row.Split == split));
        AtomicJsonl(Path.Combine(options.OutputPath, "provenance.jsonl"), BuildProvenance(manifest));
        Console.WriteLine("COMPILE OK 60000 RECORDS");
        Report(rows);
    }

    public static void Audit(CliOptions options)
    {
        var manifest = ReadManifest(options.ManifestPath);
        VerifyManifestAndRaw(manifest, options.RawPath);
        var rows = new List<CorpusRow>(60_000);
        foreach (var split in new[] { "train", "validation", "test" })
        {
            var path = Path.Combine(options.InputPath, split + ".jsonl");
            if (!File.Exists(path)) throw new FileNotFoundException($"Missing split '{path}'.");
            foreach (var line in File.ReadLines(path, Utf8))
            {
                var row = JsonSerializer.Deserialize<CorpusRow>(line, Json)
                    ?? throw new InvalidDataException($"Invalid row in {path}.");
                if (row.Split != split) throw new InvalidDataException($"Incorrect split metadata in {path}.");
                rows.Add(row);
            }
        }
        if (rows.Count != 60_000) throw new InvalidDataException($"Corpus contains {rows.Count}, not 60,000 rows.");
        foreach (var quota in RequiredSources)
            if (rows.Count(row => row.Source == quota.Key) != quota.Value)
                throw new InvalidDataException($"Source {quota.Key} does not contain exactly {quota.Value} rows.");
        EnsureUniqueAndConsistent(rows);
        AuditProjectDiversity(rows);
        AuditLeakage(rows);
        AuditBenchmark(rows, options.InputPath);
        AuditProvenance(manifest, options.InputPath);
        Console.WriteLine("AUDIT OK 60000 RECORDS");
        Console.WriteLine($"CORPUS_SHA256 {CorpusHash(options.InputPath)}");
        Report(rows);
    }
}

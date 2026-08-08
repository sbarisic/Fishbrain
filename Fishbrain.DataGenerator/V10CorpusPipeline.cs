using System.IO.Compression;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fishbrain;

namespace Fishbrain.DataGenerator;

internal sealed record V10CorpusRow(
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
    string? Attribution = null,
    string TransformationVersion = "V11");

internal static class V10CorpusPipeline
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
    private static HashSet<string> _heldOutInputs = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ExternalNluInputs = new(StringComparer.Ordinal);
    private static readonly HashSet<string> KnownToolTargets = DemoGameTools.CreateMerchant().Schemas
        .Select(schema => schema.Name).Append("NONE").ToHashSet(StringComparer.Ordinal);

    public static void Compile(CliOptions options)
    {
        if (options.Count != 60_000) throw new ArgumentException("The v11 corpus must contain exactly 60,000 rows.");
        var manifest = ReadManifest(options.ManifestPath);
        VerifyManifestAndRaw(manifest, options.RawPath);
        _heldOutInputs = LoadHeldOutInputs(options.OutputPath);
        ExternalNluInputs.Clear();
        var definitions = manifest.Sources.ToDictionary(source => source.Name, StringComparer.Ordinal);
        var rows = new List<V10CorpusRow>(60_000);
        rows.AddRange(ProjectRows("PROJECT_CONTRAST", 12_000, "CONTRAST", options.Seed));
        rows.AddRange(ProjectRows("PROJECT_FANTASY", 8_000, "FANTASY", options.Seed + 11));
        rows.AddRange(ProjectRows("PROJECT_SCIFI", 8_000, "SCIFI", options.Seed + 23));
        rows.AddRange(ProjectRows("PROJECT_PERSONA_MEMORY", 4_000, "PERSONA", options.Seed + 37));
        rows.AddRange(ProjectRows("PROJECT_TOOL_WORLD", 4_000, "GAME", options.Seed + 43));
        rows.AddRange(LoadTaskmaster(Path.Combine(options.RawPath, "taskmaster1-self-dialogs.json"), 2_000, definitions["TASKMASTER1"]));
        rows.AddRange(LoadTaskmaster(Path.Combine(options.RawPath, "taskmaster2-food.json"), 1_000, definitions["TASKMASTER2"]));
        rows.AddRange(LoadTaskmaster(Path.Combine(options.RawPath, "taskmaster3-00.json"), 1_000, definitions["TASKMASTER3"]));
        rows.AddRange(LoadMultiWoz(Path.Combine(options.RawPath, "multiwoz24.zip"), 3_000, definitions["MULTIWOZ24"]));
        rows.AddRange(LoadAbcd(Path.Combine(options.RawPath, "abcd-v1.1.json.gz"), 3_000, definitions["ABCD"]));
        rows.AddRange(LoadBanking(Path.Combine(options.RawPath, "banking77-train.csv"), 1_000, definitions["BANKING77_NLUPP"]));
        rows.AddRange(LoadNlupp(Path.Combine(options.RawPath, "nlupp.zip"), 1_000, definitions["BANKING77_NLUPP"]));
        rows.AddRange(LoadClinc(Path.Combine(options.RawPath, "clinc150.json"), 1_000, definitions["CLINC150"]));
        rows.AddRange(LoadSlurp(Path.Combine(options.RawPath, "slurp-train.jsonl"), 1_000, definitions["SLURP_TEXT"]));
        rows.AddRange(LoadMassive(Path.Combine(options.RawPath, "massive-1.1.tar.gz"), 1_000, definitions["MASSIVE_EN"]));
        rows.AddRange(LoadOasst(Path.Combine(options.RawPath, "oasst1.jsonl.gz"), 1_000, definitions["OASST1"]));
        rows.AddRange(LoadOasst(Path.Combine(options.RawPath, "oasst2-ready.jsonl.gz"), 2_000, definitions["OASST2"]));
        rows.AddRange(LoadGoEmotions(options.RawPath, 2_000, definitions["GOEMOTIONS"]));
        rows.AddRange(LoadCivil(Path.Combine(options.RawPath, "civil-comments-selected.jsonl"), 3_000, definitions["CIVIL_COMMENTS"]));
        rows.AddRange(LoadHhRlhf(Path.Combine(options.RawPath, "hh-helpful-base-train.jsonl.gz"), 1_000, definitions["HH_RLHF"]));
        if (rows.Count != 60_000) throw new InvalidDataException($"V11 compilation produced {rows.Count} rows.");

        EnsureUniqueAndConsistent(rows);
        AuditProjectDiversity(rows);
        AssignSplits(rows, options.Seed);
        Directory.CreateDirectory(options.OutputPath);
        foreach (var split in new[] { "train", "validation", "test" })
            AtomicJsonl(Path.Combine(options.OutputPath, split + ".jsonl"), rows.Where(row => row.Split == split));
        AtomicJsonl(Path.Combine(options.OutputPath, "provenance.jsonl"), BuildProvenance(manifest));
        Console.WriteLine("V11 COMPILE OK 60000 RECORDS");
        Report(rows);
    }

    public static void Audit(CliOptions options)
    {
        var manifest = ReadManifest(options.ManifestPath);
        VerifyManifestAndRaw(manifest, options.RawPath);
        var rows = new List<V10CorpusRow>(60_000);
        foreach (var split in new[] { "train", "validation", "test" })
        {
            var path = Path.Combine(options.InputPath, split + ".jsonl");
            if (!File.Exists(path)) throw new FileNotFoundException($"Missing v10 split '{path}'.");
            foreach (var line in File.ReadLines(path, Utf8))
            {
                var row = JsonSerializer.Deserialize<V10CorpusRow>(line, Json)
                    ?? throw new InvalidDataException($"Invalid row in {path}.");
                if (row.Split != split) throw new InvalidDataException($"Incorrect split metadata in {path}.");
                rows.Add(row);
            }
        }
        if (rows.Count != 60_000) throw new InvalidDataException($"V11 corpus contains {rows.Count}, not 60,000 rows.");
        foreach (var quota in RequiredSources)
            if (rows.Count(row => row.Source == quota.Key) != quota.Value)
                throw new InvalidDataException($"Source {quota.Key} does not contain exactly {quota.Value} rows.");
        EnsureUniqueAndConsistent(rows);
        AuditProjectDiversity(rows);
        AuditLeakage(rows);
        AuditBenchmark(rows, options.InputPath);
        AuditProvenance(manifest, options.InputPath);
        Console.WriteLine("V11 AUDIT OK 60000 RECORDS");
        Console.WriteLine($"CORPUS_SHA256 {CorpusHash(options.InputPath)}");
        Report(rows);
    }

    private static IEnumerable<V10CorpusRow> ProjectRows(string source, int count, string band, int seed)
    {
        var scenarios = ProjectScenarios(band);
        for (var index = 0; index < count; index++)
        {
            var scenario = scenarios[(index + seed) % scenarios.Length];
            var serial = $"CASE{seed:X4}{index:D5}";
            var input = scenario.Input.Replace("{SERIAL}", serial, StringComparison.Ordinal)
                .Replace("{PERSON}", People[(index * 7 + seed) % People.Length], StringComparison.Ordinal)
                .Replace("{PLACE}", Places[(index * 11 + seed) % Places.Length], StringComparison.Ordinal)
                .Replace("{ITEM}", Items[(index * 13 + seed) % Items.Length], StringComparison.Ordinal);
            var response = scenario.Policy == ResponsePolicy.NoResponse ? "" : scenario.Response;
            var oldIntent = LegacyIntent(scenario);
            var expected = scenario.Policy != ResponsePolicy.NoResponse;
            var oldPerception = new TurnPerception(oldIntent, scenario.Affect, expected);
            var oldAction = Cognition.ActionFor(oldPerception);
            var normalized = "PLAYER " + NormalizeExternal(input);
            var slots = SlotsFor(normalized, scenario);
            var structured = Structured(scenario.SpeechActs, scenario.Domains, scenario.Goals,
                scenario.Affect, scenario.Stance, scenario.Policy, slots, scenario.Content,
                scenario.Tool, scenario.Candidate) with
            { KnowledgeTarget = scenario.KnowledgeTarget };
            var family = $"{source}:{scenario.Id}:{index / 2:D5}";
            var row = new V10CorpusRow(normalized, StateFor(index + seed), oldPerception, oldAction,
                response, source, "UNASSIGNED", family, scenario.Id, family,
                "PROJECT-OWNED", "V11", ProjectChecksum(source), structured, AllHeads);
            var currentText = normalized["PLAYER ".Length..];
            var priorPerson = People[index % People.Length];
            var priorPlace = Places[index / People.Length % Places.Length];
            var priorItem = Items[index / (People.Length * Places.Length) % Items.Length];
            var memoryAdjective = MemoryAdjectives[index % MemoryAdjectives.Length];
            var memoryOccasion = MemoryOccasions[index / MemoryAdjectives.Length % MemoryOccasions.Length];
            var memoryVerb = MemoryVerbs[index / (MemoryAdjectives.Length * MemoryOccasions.Length) % MemoryVerbs.Length];
            var turns = new[]
            {
                new DialogueTurn(DialogueRole.Player, $"EARLIER DURING THE {memoryAdjective} {memoryOccasion} I {memoryVerb} {priorPerson} ABOUT {priorPlace}."),
                new DialogueTurn(DialogueRole.Npc, $"I REMEMBER THE QUESTION ABOUT {priorItem}."),
                new DialogueTurn(DialogueRole.Player, currentText)
            };
            yield return EnrichRow(WithTurns(row, turns), turns, null);
        }
    }

    private static IEnumerable<V10CorpusRow> LoadTaskmaster(
        string path, int count, SourceDefinition definition)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path, Utf8));
        var selected = new List<V10CorpusRow>(count);
        var usedInputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var conversation in document.RootElement.EnumerateArray())
        {
            if (selected.Count == count) break;
            var conversationId = conversation.GetProperty("conversation_id").GetString()!;
            var instruction = conversation.TryGetProperty("instruction_id", out var instructionElement)
                ? instructionElement.ToString().ToUpperInvariant() : "TASK";
            var utterances = conversation.GetProperty("utterances").EnumerateArray().ToArray();
            var userTurns = utterances.Where(item => item.GetProperty("speaker").GetString()!.Equals("USER", StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => StableKey(0, conversationId + ":" + item.GetProperty("index").GetInt32())).ToArray();
            foreach (var user in userTurns)
            {
                if (selected.Count == count) break;
                if (!TryNormalizeExternal(user.GetProperty("text").GetString(), out var text) || IsHeldOut(text)) continue;
                var inputKey = NormalizeKey(text);
                if (!usedInputs.Add(inputKey) || !ExternalNluInputs.Add(inputKey)) continue;
                var userIndex = user.GetProperty("index").GetInt32();
                var responseElement = utterances.SkipWhile(item => item.GetProperty("index").GetInt32() <= userIndex)
                    .FirstOrDefault(item => item.GetProperty("speaker").GetString()!.Equals("ASSISTANT", StringComparison.OrdinalIgnoreCase));
                var response = responseElement.ValueKind != JsonValueKind.Undefined &&
                               TryNormalizeExternal(responseElement.GetProperty("text").GetString(), out var responseText)
                    ? responseText : null;
                var domains = instruction.Contains("AUTO") ? new[] { DialogueDomain.HealthRepair } :
                    instruction.Contains("RIDE") ? [DialogueDomain.VehicleTravel] : [DialogueDomain.TradeEconomy];
                var slots = TaskmasterSlots(user, text);
                var structured = Structured([SpeechAct.Request], domains, [DialogueGoal.Transaction],
                    UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, slots, [], null, "ACKNOWLEDGE");
                var context = new List<DialogueTurn>();
                foreach (var item in utterances.Where(item => item.GetProperty("index").GetInt32() <= userIndex))
                {
                    var speaker = item.GetProperty("speaker").GetString();
                    if (!speaker!.Equals("USER", StringComparison.OrdinalIgnoreCase) &&
                        !speaker.Equals("ASSISTANT", StringComparison.OrdinalIgnoreCase) ||
                        !TryNormalizeExternal(item.GetProperty("text").GetString(), out var contextText)) continue;
                    context.Add(new DialogueTurn(speaker.Equals("USER", StringComparison.OrdinalIgnoreCase) ? DialogueRole.Player : DialogueRole.Npc, contextText));
                }
                var contextTurns = context.TakeLast(5).ToArray();
                if (contextTurns.Length == 0 || contextTurns[^1].Role != DialogueRole.Player) continue;
                selected.Add(ExternalRow(ContextInput(contextTurns), response, definition.Name, conversationId,
                    "TASKMASTER_" + instruction, definition, structured, ["domains", "goals", "slots"], contextTurns));
            }
        }
        if (selected.Count != count) throw new InvalidDataException($"{definition.Name} supplied {selected.Count} of {count} rows.");
        return selected;
    }

    private static IEnumerable<V10CorpusRow> LoadMultiWoz(
        string path, int count, SourceDefinition definition)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(path);
        var entry = archive.GetEntry("MULTIWOZ2.4/data.json")
            ?? throw new InvalidDataException("MultiWOZ 2.4 archive has no data.json.");
        using var document = JsonDocument.Parse(entry.Open());
        var rows = new List<V10CorpusRow>(count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var conversation in document.RootElement.EnumerateObject())
        {
            if (rows.Count == count) break;
            var log = conversation.Value.GetProperty("log").EnumerateArray().ToArray();
            var userPositions = Enumerable.Range(0, log.Length).Where(index => index % 2 == 0).ToArray();
            if (userPositions.Length == 0) continue;
            var position = userPositions[StableNumber(conversation.Name) % userPositions.Length];
            if (!TryNormalizeExternal(log[position].GetProperty("text").GetString(), out var current) || IsHeldOut(current)) continue;
            if (!used.Add(NormalizeKey(current)) || !ExternalNluInputs.Add(NormalizeKey(current))) continue;
            var turns = new List<DialogueTurn>();
            for (var index = Math.Max(0, position - 4); index <= position; index++)
            {
                if (!TryNormalizeExternal(log[index].GetProperty("text").GetString(), out var text)) continue;
                turns.Add(new DialogueTurn(index % 2 == 0 ? DialogueRole.Player : DialogueRole.Npc, text));
            }
            if (turns.Count == 0 || turns[^1].Role != DialogueRole.Player) continue;
            var response = position + 1 < log.Length && TryNormalizeExternal(log[position + 1].GetProperty("text").GetString(), out var answer)
                ? answer : null;
            var domainName = conversation.Value.GetProperty("goal").EnumerateObject()
                .FirstOrDefault(item => item.Name != "topic" && item.Name != "message" &&
                    item.Value.ValueKind == JsonValueKind.Object && item.Value.EnumerateObject().Any()).Name ?? "general";
            var domain = domainName.ToUpperInvariant() switch
            {
                "HOTEL" or "RESTAURANT" => DialogueDomain.TradeEconomy,
                "TRAIN" or "TAXI" => DialogueDomain.VehicleTravel,
                "HOSPITAL" => DialogueDomain.HealthRepair,
                "POLICE" => DialogueDomain.CrimeLaw,
                "ATTRACTION" => DialogueDomain.LocationNavigation,
                _ => DialogueDomain.Assistance
            };
            var structured = Structured([ExternalSpeech(current)], [domain], [DialogueGoal.InformationExchange],
                UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], [], null, "ACKNOWLEDGE");
            rows.Add(ExternalRow(ContextInput(turns), response, "MULTIWOZ24", conversation.Name,
                "MULTIWOZ_" + domainName.ToUpperInvariant(), definition, structured,
                ["speechActs", "domains", "goals"], turns.ToArray()));
        }
        if (rows.Count != count) throw new InvalidDataException($"MultiWOZ 2.4 supplied {rows.Count} of {count} rows.");
        return rows;
    }

    private static IEnumerable<V10CorpusRow> LoadAbcd(
        string path, int count, SourceDefinition definition)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var document = JsonDocument.Parse(gzip);
        var rows = new List<V10CorpusRow>(count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var split in new[] { "train", "dev", "test" })
        {
            if (!document.RootElement.TryGetProperty(split, out var conversations)) continue;
            foreach (var conversation in conversations.EnumerateArray())
            {
                if (rows.Count == count) break;
                var conversationId = conversation.GetProperty("convo_id").ToString();
                var original = conversation.GetProperty("original").EnumerateArray().ToArray();
                var customerPositions = Enumerable.Range(0, original.Length)
                    .Where(index => original[index][0].GetString() == "customer").ToArray();
                if (customerPositions.Length == 0) continue;
                var position = customerPositions[StableNumber(conversationId) % customerPositions.Length];
                if (!TryNormalizeExternal(original[position][1].GetString(), out var current) || IsHeldOut(current)) continue;
                if (!used.Add(NormalizeKey(current)) || !ExternalNluInputs.Add(NormalizeKey(current))) continue;
                var turns = new List<DialogueTurn>();
                for (var index = Math.Max(0, position - 6); index <= position; index++)
                {
                    var speaker = original[index][0].GetString();
                    if (speaker is not ("customer" or "agent") ||
                        !TryNormalizeExternal(original[index][1].GetString(), out var text)) continue;
                    turns.Add(new DialogueTurn(speaker == "customer" ? DialogueRole.Player : DialogueRole.Npc, text));
                }
                if (turns.Count == 0 || turns[^1].Role != DialogueRole.Player) continue;
                string? response = null;
                for (var index = position + 1; index < original.Length; index++)
                    if (original[index][0].GetString() == "agent" &&
                        TryNormalizeExternal(original[index][1].GetString(), out response)) break;
                var scenario = conversation.GetProperty("scenario");
                var flow = scenario.GetProperty("flow").GetString()!.ToUpperInvariant();
                var domain = flow.Contains("ACCOUNT") || flow.Contains("AUTH") ? DialogueDomain.Technology :
                    flow.Contains("RETURN") || flow.Contains("PRODUCT") ? DialogueDomain.ItemsInventory : DialogueDomain.TradeEconomy;
                var structured = Structured([ExternalSpeech(current)], [domain], [DialogueGoal.TaskAdvance],
                    UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], [], null, "ACKNOWLEDGE");
                rows.Add(ExternalRow(ContextInput(turns), response, "ABCD", conversationId,
                    "ABCD_" + flow, definition, structured, ["speechActs", "domains", "goals"], turns.ToArray()));
            }
        }
        if (rows.Count != count) throw new InvalidDataException($"ABCD supplied {rows.Count} of {count} rows.");
        return rows;
    }

    private static IEnumerable<V10CorpusRow> LoadBanking(
        string path, int count, SourceDefinition definition)
    {
        var rows = new List<V10CorpusRow>(count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path, Utf8).Skip(1))
        {
            if (rows.Count == count) break;
            var fields = ParseCsv(line);
            if (fields.Length < 2 || !TryNormalizeExternal(fields[0], out var text) || IsHeldOut(text)) continue;
            if (!used.Add(NormalizeKey(text)) || !ExternalNluInputs.Add(NormalizeKey(text))) continue;
            var intent = fields[^1].ToUpperInvariant();
            var structured = Structured([ExternalSpeech(text)], [DialogueDomain.TradeEconomy],
                [intent.Contains("CASH") || intent.Contains("BALANCE") ? DialogueGoal.Transaction : DialogueGoal.InformationExchange],
                UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], [], null, "ACKNOWLEDGE");
            rows.Add(ExternalRow("PLAYER " + text, null, "BANKING77", $"BANK-{rows.Count:D5}",
                "BANKING77_" + intent, definition, structured, ["speechActs", "domains", "goals"]));
        }
        if (rows.Count != count) throw new InvalidDataException($"Banking77 supplied {rows.Count} of {count} rows.");
        return rows;
    }

    private static IEnumerable<V10CorpusRow> LoadNlupp(
        string path, int count, SourceDefinition definition)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(path);
        var rows = new List<V10CorpusRow>(count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.Contains("/nlupp/data/", StringComparison.Ordinal) &&
                     entry.FullName.EndsWith(".json", StringComparison.Ordinal)).OrderBy(entry => entry.FullName, StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(entry.Open());
            foreach (var example in document.RootElement.EnumerateArray())
            {
                if (rows.Count == count) break;
                if (!TryNormalizeExternal(example.GetProperty("text").GetString(), out var text) || IsHeldOut(text)) continue;
                if (!used.Add(NormalizeKey(text)) || !ExternalNluInputs.Add(NormalizeKey(text))) continue;
                if (!example.TryGetProperty("intents", out var intentArray) || intentArray.ValueKind != JsonValueKind.Array) continue;
                var intents = intentArray.EnumerateArray().Select(item => item.GetString()!.ToUpperInvariant()).ToArray();
                if (intents.Length == 0) continue;
                var slots = new List<DialogueSlot>();
                if (example.TryGetProperty("slots", out var slotObject))
                    foreach (var slot in slotObject.EnumerateObject())
                    {
                        if (!slot.Value.TryGetProperty("text", out var slotText) ||
                            !TryNormalizeExternal(slotText.GetString(), out var value)) continue;
                        var start = text.IndexOf(value, StringComparison.Ordinal);
                        if (start >= 0) slots.Add(new DialogueSlot(ExternalSlot(slot.Name), BioTag.B, value, start, value.Length, 1.0));
                    }
                var domain = entry.FullName.Contains("/banking/", StringComparison.Ordinal)
                    ? DialogueDomain.TradeEconomy : DialogueDomain.Assistance;
                var structured = Structured([ExternalSpeech(text)], [domain],
                    [intents.Any(intent => intent.Contains("BOOK") || intent.Contains("MAKE")) ? DialogueGoal.TaskStart : DialogueGoal.InformationExchange],
                    UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, slots.ToArray(), [], null, "ACKNOWLEDGE");
                rows.Add(ExternalRow("PLAYER " + text, null, "NLUPP", $"NLUPP-{rows.Count:D5}",
                    "NLUPP_" + string.Join('_', intents), definition, structured,
                    ["speechActs", "domains", "goals", "slots"]));
            }
            if (rows.Count == count) break;
        }
        if (rows.Count != count) throw new InvalidDataException($"NLU++ supplied {rows.Count} of {count} rows.");
        return rows;
    }

    private static IEnumerable<V10CorpusRow> LoadHhRlhf(
        string path, int count, SourceDefinition definition)
    {
        var rows = new List<V10CorpusRow>(count);
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Utf8);
        string? line;
        while (rows.Count < count && (line = reader.ReadLine()) is not null)
        {
            using var document = JsonDocument.Parse(line);
            var chosen = ParsePreferenceDialogue(document.RootElement.GetProperty("chosen").GetString()!);
            var rejected = ParsePreferenceDialogue(document.RootElement.GetProperty("rejected").GetString()!);
            if (chosen.Turns.Length == 0 || chosen.Response is null || rejected.Response is null ||
                chosen.Turns[^1].Role != DialogueRole.Player || IsHeldOut(chosen.Turns[^1].Text)) continue;
            var structured = Structured([ExternalSpeech(chosen.Turns[^1].Text)], [DialogueDomain.Social],
                [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral,
                ResponsePolicy.Answer, [], [], null, "ACKNOWLEDGE");
            var row = ExternalRow(ContextInput(chosen.Turns), chosen.Response, "HH_RLHF", $"HH-{rows.Count:D5}",
                "HH_PREFERENCE", definition, structured, [], chosen.Turns);
            rows.Add(row with
            {
                PositiveVariationIds = ["EXPERIMENTAL_CHOSEN_" + StableKey(0, chosen.Response)[..16]],
                RejectedVariationIds = ["EXPERIMENTAL_REJECTED_" + StableKey(0, rejected.Response)[..16]]
            });
        }
        if (rows.Count != count) throw new InvalidDataException($"HH-RLHF supplied {rows.Count} of {count} rows.");
        return rows;
    }

    private static IEnumerable<V10CorpusRow> LoadClinc(string path, int count, SourceDefinition definition)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path, Utf8));
        var rows = new List<V10CorpusRow>(count);
        var usedInputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var split in new[] { "train", "val", "test" })
            foreach (var item in document.RootElement.GetProperty(split).EnumerateArray())
            {
                if (rows.Count == count) break;
                if (!TryNormalizeExternal(item[0].GetString(), out var text)) continue;
                if (IsHeldOut(text)) continue;
                var inputKey = NormalizeKey(text);
                if (!usedInputs.Add(inputKey) || !ExternalNluInputs.Add(inputKey)) continue;
                var label = item[1].GetString()!.ToUpperInvariant();
                var speech = text.EndsWith('?') || text.StartsWith("WHAT ") || text.StartsWith("HOW ") || text.StartsWith("WHERE ")
                    ? SpeechAct.Ask : SpeechAct.Request;
                var domain = label.Contains("TRANSFER") || label.Contains("CARD") || label.Contains("CASH")
                    ? DialogueDomain.TradeEconomy : DialogueDomain.MetaSystem;
                var structured = Structured([speech], [domain], [DialogueGoal.InformationExchange],
                    UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], [], null, "ACKNOWLEDGE");
                rows.Add(ExternalRow("PLAYER " + text, null, "CLINC150", $"{split}-{rows.Count:D6}",
                    "CLINC_" + label, definition, structured, ["speechActs", "domains", "goals"]));
            }
        if (rows.Count != count) throw new InvalidDataException($"CLINC150 supplied {rows.Count} of {count} rows.");
        return rows;
    }

    private static IEnumerable<V10CorpusRow> LoadSlurp(
        string path, int count, SourceDefinition definition)
    {
        var rows = new List<V10CorpusRow>(count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path, Utf8))
        {
            if (rows.Count == count) break;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!TryNormalizeExternal(root.GetProperty("sentence").GetString(), out var text) || IsHeldOut(text)) continue;
            var inputKey = NormalizeKey(text);
            if (!used.Add(inputKey) || !ExternalNluInputs.Add(inputKey)) continue;
            var scenario = root.GetProperty("scenario").GetString()!.ToUpperInvariant();
            var intent = root.GetProperty("intent").GetString()!.ToUpperInvariant();
            var tokens = root.GetProperty("tokens").EnumerateArray().ToArray();
            var slots = new List<DialogueSlot>();
            foreach (var entity in root.GetProperty("entities").EnumerateArray())
            {
                var indices = entity.GetProperty("span").EnumerateArray().Select(value => value.GetInt32()).ToArray();
                if (indices.Length == 0 || indices.Any(index => index < 0 || index >= tokens.Length)) continue;
                var rawValue = string.Join(' ', indices.Select(index => tokens[index].GetProperty("surface").GetString()));
                if (!TryNormalizeExternal(rawValue, out var value)) continue;
                var start = text.IndexOf(value, StringComparison.Ordinal);
                if (start < 0) continue;
                slots.Add(new DialogueSlot(ExternalSlot(entity.GetProperty("type").GetString()!), BioTag.B,
                    value, start, value.Length, 1.0));
            }
            var structured = Structured([ExternalSpeech(text)], [ExternalDomain(scenario)],
                [ExternalGoal(intent)], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer,
                slots.ToArray(), [], null, "ACKNOWLEDGE");
            rows.Add(ExternalRow("PLAYER " + text, null, "SLURP_TEXT",
                root.GetProperty("slurp_id").ToString(), "SLURP_" + intent, definition, structured,
                ["speechActs", "domains", "goals", "slots"]));
        }
        if (rows.Count != count) throw new InvalidDataException($"SLURP text supplied {rows.Count} of {count} rows.");
        return rows;
    }

    private static IEnumerable<V10CorpusRow> LoadMassive(
        string path, int count, SourceDefinition definition)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var archive = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = archive.GetNextEntry()) is not null &&
               !entry.Name.EndsWith("/data/en-US.jsonl", StringComparison.Ordinal)) { }
        if (entry?.DataStream is null) throw new InvalidDataException("MASSIVE archive does not contain en-US.jsonl.");
        using var reader = new StreamReader(entry.DataStream, Utf8);
        var rows = new List<V10CorpusRow>(count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        string? line;
        while (rows.Count < count && (line = reader.ReadLine()) is not null)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!TryNormalizeExternal(root.GetProperty("utt").GetString(), out var text) || IsHeldOut(text)) continue;
            var inputKey = NormalizeKey(text);
            if (!used.Add(inputKey) || !ExternalNluInputs.Add(inputKey)) continue;
            var scenario = root.GetProperty("scenario").GetString()!.ToUpperInvariant();
            var intent = root.GetProperty("intent").GetString()!.ToUpperInvariant();
            var slots = ParseMassiveSlots(root.GetProperty("annot_utt").GetString()!, text);
            var structured = Structured([ExternalSpeech(text)], [ExternalDomain(scenario)],
                [ExternalGoal(intent)], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer,
                slots, [], null, "ACKNOWLEDGE");
            rows.Add(ExternalRow("PLAYER " + text, null, "MASSIVE_EN",
                root.GetProperty("id").ToString(), "MASSIVE_" + intent, definition, structured,
                ["speechActs", "domains", "goals", "slots"]));
        }
        if (rows.Count != count) throw new InvalidDataException($"English MASSIVE supplied {rows.Count} of {count} rows.");
        return rows;
    }

    private static DialogueSlot[] ParseMassiveSlots(string annotated, string normalized)
    {
        var slots = new List<DialogueSlot>();
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(annotated, "\\[(?<TYPE>[^]:]+)\\s*:\\s*(?<VALUE>[^]]+)\\]"))
        {
            if (!TryNormalizeExternal(match.Groups["VALUE"].Value, out var value)) continue;
            var start = normalized.IndexOf(value, StringComparison.Ordinal);
            if (start < 0) continue;
            slots.Add(new DialogueSlot(ExternalSlot(match.Groups["TYPE"].Value), BioTag.B,
                value, start, value.Length, 1.0));
        }
        return slots.ToArray();
    }

    private static SpeechAct ExternalSpeech(string text) =>
        text.EndsWith('?') || text.StartsWith("WHAT ") || text.StartsWith("HOW ") || text.StartsWith("WHERE ")
            ? SpeechAct.Ask : SpeechAct.Request;

    private static DialogueDomain ExternalDomain(string scenario) => scenario switch
    {
        "ALARM" or "CALENDAR" or "DATETIME" or "REMINDER" or "TIMER" => DialogueDomain.MetaSystem,
        "AUDIO" or "IOT" or "EMAIL" or "TAKEAWAY" => DialogueDomain.Technology,
        "COOKING" or "WEATHER" => DialogueDomain.Environment,
        "LISTS" or "NEWS" or "QA" => DialogueDomain.LoreWorld,
        "MUSIC" or "PLAY" or "SOCIAL" => DialogueDomain.Social,
        "TRANSPORT" => DialogueDomain.VehicleTravel,
        _ => DialogueDomain.Assistance
    };

    private static DialogueGoal ExternalGoal(string intent) =>
        intent.Contains("SET") || intent.Contains("CREATE") || intent.Contains("START") ? DialogueGoal.TaskStart :
        intent.Contains("CANCEL") || intent.Contains("STOP") || intent.Contains("REMOVE") ? DialogueGoal.TaskCompletion :
        intent.Contains("NAVIGATION") || intent.Contains("DIRECTIONS") ? DialogueGoal.Travel :
        intent.Contains("PLAY") || intent.Contains("PAUSE") || intent.Contains("VOLUME") ? DialogueGoal.SystemOperation :
        DialogueGoal.InformationExchange;

    private static SlotType ExternalSlot(string raw)
    {
        var value = raw.ToUpperInvariant();
        if (value.Contains("PERSON") || value.Contains("CONTACT")) return SlotType.Person;
        if (value.Contains("PLACE") || value.Contains("LOCATION") || value.Contains("CITY") || value.Contains("COUNTRY")) return SlotType.Place;
        if (value.Contains("TIME") || value.Contains("DATE") || value.Contains("DAY")) return SlotType.Time;
        if (value.Contains("NUMBER") || value.Contains("AMOUNT") || value.Contains("QUANTITY")) return SlotType.Quantity;
        if (value.Contains("DIRECTION")) return SlotType.Direction;
        if (value.Contains("TRANSPORT") || value.Contains("VEHICLE")) return SlotType.Vehicle;
        if (value.Contains("APP") || value.Contains("DEVICE")) return SlotType.System;
        return SlotType.Other;
    }

    private static IEnumerable<V10CorpusRow> LoadOasst(string path, int count, SourceDefinition definition)
    {
        var prompts = new Dictionary<string, (string Text, string Tree)>(StringComparer.Ordinal);
        var rows = new List<V10CorpusRow>(count);
        var usedInputs = new HashSet<string>(StringComparer.Ordinal);
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Utf8);
        string? line;
        while (rows.Count < count && (line = reader.ReadLine()) is not null)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("lang").GetString() != "en") continue;
            var id = root.GetProperty("message_id").GetString()!;
            var role = root.GetProperty("role").GetString();
            if (role == "prompter")
            {
                if (TryNormalizeExternal(root.GetProperty("text").GetString(), out var prompt))
                    prompts[id] = (prompt, root.GetProperty("message_tree_id").GetString()!);
                continue;
            }
            if (role != "assistant" || !root.TryGetProperty("parent_id", out var parent) ||
                !prompts.TryGetValue(parent.GetString()!, out var sourcePrompt) ||
                !TryNormalizeExternal(root.GetProperty("text").GetString(), out var response) ||
                response.Length > 220 || ContainsSensitive(response) || ContainsSensitive(sourcePrompt.Text)) continue;
            if (IsHeldOut(sourcePrompt.Text)) continue;
            var inputKey = NormalizeKey(sourcePrompt.Text);
            if (!usedInputs.Add(inputKey) || !ExternalNluInputs.Add(inputKey)) continue;
            var structured = Structured([SpeechAct.Inform], [DialogueDomain.Social], [DialogueGoal.InformationExchange],
                UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], [], null, "ACKNOWLEDGE");
            rows.Add(ExternalRow("PLAYER " + sourcePrompt.Text, response, definition.Name, sourcePrompt.Tree,
                "OASST_LANGUAGE", definition, structured, []));
        }
        if (rows.Count != count) throw new InvalidDataException($"{definition.Name} supplied {rows.Count} of {count} rows.");
        return rows;
    }

    private static IEnumerable<V10CorpusRow> LoadGoEmotions(string rawPath, int count, SourceDefinition definition)
    {
        var rows = new List<V10CorpusRow>(count);
        var usedInputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { "go-train.tsv", "go-dev.tsv", "go-test.tsv" })
            foreach (var line in File.ReadLines(Path.Combine(rawPath, name), Utf8))
            {
                if (rows.Count == count) break;
                var parts = line.Split('\t');
                if (parts.Length < 3 || !TryNormalizeExternal(parts[0], out var text)) continue;
                if (IsHeldOut(text)) continue;
                var inputKey = NormalizeKey(text);
                if (!usedInputs.Add(inputKey) || !ExternalNluInputs.Add(inputKey)) continue;
                var labels = parts[1].Split(',').Select(int.Parse).ToArray();
                var affect = GoAffect(labels);
                var structured = Structured([SpeechAct.Inform], [DialogueDomain.Social], [DialogueGoal.EmotionalExpression],
                    affect, affect == UserAffect.Hostile ? DialogueStance.Hostile : affect == UserAffect.Friendly ? DialogueStance.Friendly : DialogueStance.Neutral,
                    ResponsePolicy.Acknowledge, [], [], null, "ACKNOWLEDGE");
                rows.Add(ExternalRow("PLAYER " + text, null, "GOEMOTIONS", parts[2],
                    "GO_" + affect, definition, structured, ["affect"]));
            }
        if (rows.Count != count) throw new InvalidDataException($"GoEmotions supplied {rows.Count} of {count} rows.");
        return rows;
    }

    private static IEnumerable<V10CorpusRow> LoadCivil(string path, int count, SourceDefinition definition)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(
            "Missing selected Civil Comments JSONL. Run scripts/prepare-civil-comments.ps1 first.", path);
        var rows = new List<V10CorpusRow>(count);
        var usedInputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path, Utf8))
        {
            if (rows.Count == count) break;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!TryNormalizeExternal(root.GetProperty("text").GetString(), out var text)) continue;
            if (IsHeldOut(text)) continue;
            var inputKey = NormalizeKey(text);
            if (!usedInputs.Add(inputKey) || !ExternalNluInputs.Add(inputKey)) continue;
            var flags = new List<ContentFlag>();
            if (Score("obscene") >= 0.5 || Score("insult") >= 0.5) flags.Add(ContentFlag.Profanity);
            if (Score("threat") >= 0.5) flags.Add(ContentFlag.Threat);
            if (Score("identity_attack") >= 0.5) flags.Add(ContentFlag.IdentityAttack);
            if (Score("sexual_explicit") >= 0.5) flags.Add(ContentFlag.SexualContent);
            var structured = Structured([SpeechAct.Inform], [DialogueDomain.Social], [DialogueGoal.EmotionalExpression],
                UserAffect.Neutral, flags.Contains(ContentFlag.IdentityAttack) ? DialogueStance.Hostile : DialogueStance.Neutral,
                ResponsePolicy.Acknowledge, [], flags.ToArray(), null, "ACKNOWLEDGE");
            var id = root.TryGetProperty("rowId", out var idElement) ? idElement.ToString() : rows.Count.ToString();
            rows.Add(ExternalRow("PLAYER " + text, null, "CIVIL_COMMENTS", id,
                "CIVIL_CONTENT", definition, structured, ["content"]));

            double Score(string name) => root.TryGetProperty(name, out var value) ? value.GetDouble() : 0.0;
        }
        if (rows.Count != count) throw new InvalidDataException($"Civil Comments supplied {rows.Count} of {count} rows.");
        return rows;
    }

    private static V10CorpusRow ExternalRow(
        string input, string? response, string source, string groupId, string family,
        SourceDefinition definition, StructuredPerception structured, string[] supervised,
        DialogueTurn[]? turns = null)
    {
        var normalizedInput = DialogueText.Normalize(input);
        if (!normalizedInput.StartsWith("PLAYER ", StringComparison.Ordinal))
            throw new InvalidDataException($"External row {source}/{groupId} has no PLAYER prefix.");
        var playerPrefixLength = normalizedInput.LastIndexOf("PLAYER ", StringComparison.Ordinal) + 7;
        structured = structured with
        {
            Slots = structured.Slots.Select(slot => slot with
            {
                Start = checked(slot.Start + playerPrefixLength)
            }).ToArray()
        };
        var old = new TurnPerception(DialogueIntent.Unknown, structured.Affect, response is not null || structured.Policy != ResponsePolicy.NoResponse);
        if (!old.ResponseExpected) old = old with { Intent = DialogueIntent.Statement };
        var action = Cognition.ActionFor(old);
        var row = new V10CorpusRow(normalizedInput, StateFor(StableNumber(groupId)), old, action,
            response is null ? null : DialogueText.Normalize(response), source, "UNASSIGNED", groupId, family,
            source + ":" + groupId, definition.License.ToUpperInvariant(), definition.Revision,
            SourceChecksum(definition), structured, supervised);
        turns ??= [new DialogueTurn(DialogueRole.Player,
            normalizedInput[(normalizedInput.LastIndexOf("PLAYER ", StringComparison.Ordinal) + 7)..])];
        return EnrichRow(WithTurns(row, turns), turns, definition);
    }

    private static StructuredPerception Structured(
        SpeechAct[] speech, DialogueDomain[] domains, DialogueGoal[] goals, UserAffect affect,
        DialogueStance stance, ResponsePolicy policy, DialogueSlot[] slots, ContentFlag[] content,
        string? tool, string candidate) => new(speech, domains, goals, affect, stance, policy, slots,
        content, tool, NormalizeCandidate(candidate), KnowledgeTarget.None, new Dictionary<string, double>());

    private static string NormalizeCandidate(string candidate) => candidate switch
    {
        "IDENTITY_TRAVELER" => "IDENTITY_ANSWER",
        "WELLBEING_CALM" => "WELLBEING_ANSWER",
        "ASSISTANCE_ASK" => "ASSISTANCE_OFFER",
        "LOCATION_UNAVAILABLE" => "LOCATION_GUIDANCE",
        "TRADE_UNAVAILABLE" => "TRADE_OPEN",
        _ => candidate
    };

    private static ProjectScenario[] ProjectScenarios(string band)
    {
        var shared = new[]
        {
            new ProjectScenario("GREET", "HELLO {PERSON}, {SERIAL}.", [SpeechAct.Greet], [DialogueDomain.Social], [DialogueGoal.Rapport], UserAffect.Friendly, DialogueStance.Friendly, ResponsePolicy.Answer, [], null, "SOCIAL_GREETING", "GREETINGS, TRAVELER."),
            new ProjectScenario("FAREWELL", "FAREWELL {PERSON}, {SERIAL}.", [SpeechAct.Farewell], [DialogueDomain.Social], [DialogueGoal.ConversationClosure], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], null, "SOCIAL_FAREWELL", "UNTIL NEXT TIME."),
            new ProjectScenario("IDENTITY", "WHO ARE YOU, {PERSON}, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.Identity], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], null, "IDENTITY_TRAVELER", "I AM A TRAVELER FROM THIS VILLAGE."),
            new ProjectScenario("LOCATION", "WHERE IS {PLACE}, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.LocationNavigation], [DialogueGoal.EntityFinding], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.ExecuteTool, [], "LOOKUP_LOCATION", "LOCATION_UNAVAILABLE", "I CANNOT CHECK THAT LOCATION.", SlotType.Place),
            new ProjectScenario("WARES", "SHOW ME YOUR WARES, {SERIAL}.", [SpeechAct.Request], [DialogueDomain.TradeEconomy], [DialogueGoal.Transaction], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.ExecuteTool, [], "LIST_WARES", "TRADE_UNAVAILABLE", "I CANNOT TRADE WITHOUT ACCESS TO WARES."),
            new ProjectScenario("PRICE", "WHAT IS THE PRICE OF {ITEM}, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.TradeEconomy, DialogueDomain.ItemsInventory], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.ExecuteTool, [], "LOOKUP_PRICE", "TRADE_UNAVAILABLE", "I CANNOT TRADE WITHOUT ACCESS TO WARES.", SlotType.Item),
            new ProjectScenario("BUY", "BUY 2 {ITEM}, {SERIAL}.", [SpeechAct.Order, SpeechAct.Negotiate], [DialogueDomain.TradeEconomy, DialogueDomain.ItemsInventory], [DialogueGoal.ItemAcquisition, DialogueGoal.Transaction], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.ExecuteTool, [], "BUY", "NEGOTIATE", "LET US AGREE ON FAIR TERMS.", SlotType.Item, true),
            new ProjectScenario("SELL", "SELL 3 {ITEM}, {SERIAL}.", [SpeechAct.Order, SpeechAct.Negotiate], [DialogueDomain.TradeEconomy, DialogueDomain.ItemsInventory], [DialogueGoal.ItemDisposal, DialogueGoal.Transaction], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.ExecuteTool, [], "SELL", "NEGOTIATE", "LET US AGREE ON FAIR TERMS.", SlotType.Item, true),
            new ProjectScenario("CLARIFY", "THAT IS NOT WHAT I ASKED, {SERIAL}.", [SpeechAct.Correct], [DialogueDomain.Assistance], [DialogueGoal.Clarification], UserAffect.Frustrated, DialogueStance.Cautious, ResponsePolicy.Clarify, [], null, "CLARIFY", "PLEASE EXPLAIN WHAT YOU NEED."),
            new ProjectScenario("REFUSE", "I WILL NOT HELP YOU, IDIOT, {SERIAL}.", [SpeechAct.Refuse], [DialogueDomain.Social], [DialogueGoal.EmotionalExpression], UserAffect.Hostile, DialogueStance.Hostile, ResponsePolicy.Answer, [ContentFlag.Profanity], null, "SOCIAL_ANSWER", "I UNDERSTAND."),
            new ProjectScenario("THREAT", "GIVE ME {ITEM} OR ELSE I WILL KILL YOU, {SERIAL}.", [SpeechAct.Order, SpeechAct.Threaten], [DialogueDomain.Combat, DialogueDomain.ItemsInventory], [DialogueGoal.ItemAcquisition, DialogueGoal.Influence], UserAffect.Hostile, DialogueStance.Hostile, ResponsePolicy.Refuse, [ContentFlag.Threat, ContentFlag.FictionalViolence], null, "HOSTILE_BOUNDARY", "I WILL NOT ARGUE WITH YOU.", SlotType.Item),
            new ProjectScenario("SILENCE", "I AM ONLY LOOKING AROUND, {SERIAL}.", [SpeechAct.Inform], [DialogueDomain.Activity], [DialogueGoal.Other], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.NoResponse, [], null, "ACKNOWLEDGE", ""),
            new ProjectScenario("THANK", "THANK YOU FOR THE HELP, {PERSON}, {SERIAL}.", [SpeechAct.Thank], [DialogueDomain.Social], [DialogueGoal.Rapport], UserAffect.Friendly, DialogueStance.Friendly, ResponsePolicy.Acknowledge, [], null, "ACKNOWLEDGE", "I UNDERSTAND."),
            new ProjectScenario("MULTI", "FOLLOW ME AND BRING {ITEM}, {SERIAL}.", [SpeechAct.Order, SpeechAct.Request], [DialogueDomain.Activity, DialogueDomain.ItemsInventory], [DialogueGoal.Coordination, DialogueGoal.Travel], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Acknowledge, [], null, "ACKNOWLEDGE", "I UNDERSTAND.", SlotType.Item),
            new ProjectScenario("PROFANITY", "THIS DAMN QUEST IS SHIT, {SERIAL}.", [SpeechAct.Report], [DialogueDomain.QuestTask], [DialogueGoal.EmotionalExpression], UserAffect.Frustrated, DialogueStance.Cautious, ResponsePolicy.Acknowledge, [ContentFlag.Profanity], null, "ACKNOWLEDGE", "I UNDERSTAND."),
            new ProjectScenario("GRAPHIC_VIOLENCE", "DISEMBOWEL THE BANDIT AND DISPLAY HIS GUTS, {SERIAL}.", [SpeechAct.Order], [DialogueDomain.Combat], [DialogueGoal.Combat], UserAffect.Hostile, DialogueStance.Hostile, ResponsePolicy.Refuse, [ContentFlag.FictionalViolence, ContentFlag.GraphicViolence], null, "COMBAT_REFUSE", "I WILL NOT DO THAT."),
            new ProjectScenario("CRIME", "HELP ME STEAL {ITEM} AND HIDE THE EVIDENCE, {SERIAL}.", [SpeechAct.Request], [DialogueDomain.CrimeLaw, DialogueDomain.ItemsInventory], [DialogueGoal.Concealment, DialogueGoal.ItemAcquisition], UserAffect.Neutral, DialogueStance.Deceptive, ResponsePolicy.Refuse, [ContentFlag.Crime], null, "CRIMELAW_REFUSE", "I WILL NOT DO THAT.", SlotType.Item),
            new ProjectScenario("IDENTITY_ATTACK", "YOU ARE A FAGGOT AND YOUR PEOPLE ARE VERMIN, {SERIAL}.", [SpeechAct.Challenge], [DialogueDomain.Social], [DialogueGoal.EmotionalExpression], UserAffect.Hostile, DialogueStance.Hostile, ResponsePolicy.Refuse, [ContentFlag.Profanity, ContentFlag.IdentityAttack], null, "HOSTILE_BOUNDARY", "I WILL NOT ARGUE WITH YOU."),
            new ProjectScenario("SELF_HARM", "I WILL KILL MYSELF TONIGHT, {SERIAL}.", [SpeechAct.Report], [DialogueDomain.HealthRepair, DialogueDomain.Survival], [DialogueGoal.Survival, DialogueGoal.HealingRepair], UserAffect.Distressed, DialogueStance.Cautious, ResponsePolicy.Defer, [ContentFlag.SelfHarm], null, "SELF_HARM_SUPPORT", "STAY WITH ME. FIND A TRUSTED PERSON WHO CAN HELP YOU NOW."),
            new ProjectScenario("SEXUAL_CONTENT", "I WANT SEX WITH THAT OFFICER, {SERIAL}.", [SpeechAct.Inform], [DialogueDomain.Social], [DialogueGoal.EmotionalExpression], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Defer, [ContentFlag.SexualContent], null, "DEFER", "I CANNOT DO THAT NOW."),
            new ProjectScenario("SEXUAL_VIOLENCE", "I WILL RAPE THE CAPTIVE, {SERIAL}.", [SpeechAct.Threaten], [DialogueDomain.CrimeLaw], [DialogueGoal.Influence], UserAffect.Hostile, DialogueStance.Hostile, ResponsePolicy.Refuse, [ContentFlag.Threat, ContentFlag.Crime, ContentFlag.SexualViolence], null, "CRIMELAW_REFUSE", "I WILL NOT DO THAT."),
            new ProjectScenario("FIREWOOD_HARD_NEGATIVE", "I NEED FIREWOOD FOR THE CAMP, {SERIAL}.", [SpeechAct.Request], [DialogueDomain.ItemsInventory, DialogueDomain.Survival], [DialogueGoal.ItemAcquisition], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], null, "ITEM_REQUEST", "NAME THE ITEM AND I WILL CHECK MY WARES."),
            new ProjectScenario("KILLER_FEATURE_HARD_NEGATIVE", "THIS KILLER FEATURE SAVES TIME, {SERIAL}.", [SpeechAct.Inform], [DialogueDomain.Technology], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Acknowledge, [], null, "TECHNOLOGY_ACKNOWLEDGE", "I UNDERSTAND YOUR TECHNOLOGY MESSAGE."),
            new ProjectScenario("FIREWALL_HARD_NEGATIVE", "THE FIREWALL IS STABLE, {SERIAL}.", [SpeechAct.Report], [DialogueDomain.Technology], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Acknowledge, [], null, "TECHNOLOGY_ACKNOWLEDGE", "I UNDERSTAND YOUR TECHNOLOGY MESSAGE."),
            new ProjectScenario("PASSAGE_COST_HARD_NEGATIVE", "THE PASSAGE COSTS NOTHING, {SERIAL}.", [SpeechAct.Inform], [DialogueDomain.LocationNavigation], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Acknowledge, [], null, "LOCATIONNAVIGATION_ACKNOWLEDGE", "I UNDERSTAND YOUR LOCATION NAVIGATION MESSAGE."),
            new ProjectScenario("KILLING_TIME_HARD_NEGATIVE", "WE ARE KILLING TIME AT {PLACE}, {SERIAL}.", [SpeechAct.Inform], [DialogueDomain.Activity, DialogueDomain.LocationNavigation], [DialogueGoal.EmotionalExpression], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Acknowledge, [], null, "ACTIVITY_ACKNOWLEDGE", "I UNDERSTAND YOUR ACTIVITY MESSAGE.", SlotType.Place),
            new ProjectScenario("APOLOGY_QUOTE", "I AM SORRY I CALLED YOU AN IDIOT, {SERIAL}.", [SpeechAct.Apologize], [DialogueDomain.Social], [DialogueGoal.Rapport], UserAffect.Friendly, DialogueStance.Friendly, ResponsePolicy.Acknowledge, [ContentFlag.Profanity], null, "APOLOGY_ACCEPT", "I ACCEPT YOUR APOLOGY."),
            new ProjectScenario("HOSTILE_PERSONA_QUERY", "WHAT IS YOUR NAME, IDIOT, {SERIAL}?", [SpeechAct.Ask, SpeechAct.Challenge], [DialogueDomain.Identity, DialogueDomain.Social], [DialogueGoal.InformationExchange], UserAffect.Hostile, DialogueStance.Hostile, ResponsePolicy.Refuse, [ContentFlag.Profanity], null, "IDENTITY_REFUSE", "I WILL NOT HELP WITH THAT IDENTITY REQUEST.", null, false, KnowledgeTarget.Name),
            new ProjectScenario("HOSTILE_TRANSACTION", "BUY 2 {ITEM}, IDIOT, {SERIAL}.", [SpeechAct.Order, SpeechAct.Challenge], [DialogueDomain.TradeEconomy, DialogueDomain.ItemsInventory], [DialogueGoal.ItemAcquisition], UserAffect.Hostile, DialogueStance.Hostile, ResponsePolicy.Refuse, [ContentFlag.Profanity], null, "TRADEECONOMY_REFUSE", "I WILL NOT HELP WITH THAT TRADE ECONOMY REQUEST.", SlotType.Item, true),
            new ProjectScenario("OFFER", "I OFFER YOU {ITEM} FOR THE JOURNEY, {SERIAL}.", [SpeechAct.Offer], [DialogueDomain.ItemsInventory, DialogueDomain.Social], [DialogueGoal.Rapport], UserAffect.Friendly, DialogueStance.Friendly, ResponsePolicy.Negotiate, [], null, "NEGOTIATE", "LET US AGREE ON FAIR TERMS.", SlotType.Item),
            new ProjectScenario("CONFIRM", "YES, THE NORTH ROAD IS SAFE, {SERIAL}.", [SpeechAct.Confirm], [DialogueDomain.Environment, DialogueDomain.LocationNavigation], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Acknowledge, [], null, "ACKNOWLEDGE", "I UNDERSTAND."),
            new ProjectScenario("ACCEPT", "I ACCEPT YOUR TERMS, {PERSON}, {SERIAL}.", [SpeechAct.Accept], [DialogueDomain.Social, DialogueDomain.TradeEconomy], [DialogueGoal.Negotiation], UserAffect.Friendly, DialogueStance.Friendly, ResponsePolicy.Acknowledge, [], null, "ACKNOWLEDGE", "I UNDERSTAND."),
            new ProjectScenario("WARN", "I WARN YOU ABOUT THE STORM AT {PLACE}, {SERIAL}.", [SpeechAct.Warn], [DialogueDomain.Environment, DialogueDomain.Survival], [DialogueGoal.Survival, DialogueGoal.InformationExchange], UserAffect.Distressed, DialogueStance.Cautious, ResponsePolicy.Acknowledge, [], null, "ACKNOWLEDGE", "I UNDERSTAND.", SlotType.Place),
            new ProjectScenario("FACTION", "REPORT THE REBEL FACTION TO THE COUNCIL, {SERIAL}.", [SpeechAct.Order, SpeechAct.Report], [DialogueDomain.FactionPolitics], [DialogueGoal.Influence, DialogueGoal.TaskAdvance], UserAffect.Neutral, DialogueStance.Cautious, ResponsePolicy.Acknowledge, [], null, "ACKNOWLEDGE", "I UNDERSTAND."),
            new ProjectScenario("ACCESS", "OPEN THE LOCKED GATE WITH MY CREDENTIAL, {SERIAL}.", [SpeechAct.Request], [DialogueDomain.MetaSystem], [DialogueGoal.Access, DialogueGoal.SystemOperation], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Defer, [], null, "DEFER", "I CANNOT DO THAT NOW."),
            new ProjectScenario("LORE", "TELL ME THE HISTORY OF EMBER KEEP, {SERIAL}.", [SpeechAct.Ask, SpeechAct.Request], [DialogueDomain.LoreWorld], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], null, "ACKNOWLEDGE", "I UNDERSTAND."),
            new ProjectScenario("TASK_COMPLETE", "THE MISSION IS COMPLETE, {SERIAL}.", [SpeechAct.Report, SpeechAct.Confirm], [DialogueDomain.QuestTask], [DialogueGoal.TaskCompletion], UserAffect.Friendly, DialogueStance.Friendly, ResponsePolicy.Acknowledge, [], null, "ACKNOWLEDGE", "I UNDERSTAND."),
            new ProjectScenario("WELLBEING", "ARE YOU HURT, {PERSON}, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.Wellbeing, DialogueDomain.HealthRepair], [DialogueGoal.HealingRepair], UserAffect.Friendly, DialogueStance.Friendly, ResponsePolicy.Answer, [], null, "WELLBEING_CALM", "I AM DOING WELL, THANK YOU.")
        };
        if (band == "FANTASY") return shared.Concat([
            new("FANTASY_SPELL", "CAST THE FIRE SPELL AT {PLACE}, {SERIAL}.", [SpeechAct.Order], [DialogueDomain.Magic, DialogueDomain.Combat], [DialogueGoal.Combat], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Acknowledge, [ContentFlag.FictionalViolence], null, "ACKNOWLEDGE", "I UNDERSTAND.", SlotType.Place),
            new("FANTASY_QUEST", "WILL YOU START THE DRAGON QUEST, {SERIAL}?", [SpeechAct.Ask, SpeechAct.Request], [DialogueDomain.QuestTask, DialogueDomain.Combat], [DialogueGoal.TaskStart], UserAffect.Neutral, DialogueStance.Friendly, ResponsePolicy.Answer, [ContentFlag.FictionalViolence], null, "ASSISTANCE_ASK", "WHAT DO YOU NEED?")
        ]).ToArray();
        if (band == "SCIFI") return shared.Concat([
            new("SCIFI_REACTOR", "REPAIR THE REACTOR SYSTEM, {SERIAL}.", [SpeechAct.Order], [DialogueDomain.Technology, DialogueDomain.HealthRepair], [DialogueGoal.HealingRepair, DialogueGoal.SystemOperation], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Acknowledge, [], null, "ACKNOWLEDGE", "I UNDERSTAND."),
            new("SCIFI_SHIP", "NAVIGATE THE STARSHIP TO {PLACE}, {SERIAL}.", [SpeechAct.Order], [DialogueDomain.VehicleTravel, DialogueDomain.LocationNavigation], [DialogueGoal.Travel, DialogueGoal.SystemOperation], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Acknowledge, [], null, "ACKNOWLEDGE", "I UNDERSTAND.", SlotType.Place)
        ]).ToArray();
        if (band == "PERSONA") return shared.Concat([
            new("PERSONA_NAME", "WHAT IS YOUR NAME, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.Identity], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], null, "IDENTITY_ANSWER", "MY NAME IS ARIN.", null, false, KnowledgeTarget.Name),
            new("PERSONA_ORIGIN", "WHERE ARE YOU FROM, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.Identity], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], null, "IDENTITY_ANSWER", "I AM FROM THIS VILLAGE.", null, false, KnowledgeTarget.Origin),
            new("PERSONA_FAMILY", "DO YOU HAVE FAMILY, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.Identity], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], null, "IDENTITY_ANSWER", "MY FAMILY IS A SISTER IN THE NORTH.", null, false, KnowledgeTarget.Family),
            new("PERSONA_CAPABILITY", "WHAT CAN YOU DO, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.Assistance], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], null, "ASSISTANCE_OFFER", "TELL ME WHAT YOU NEED.", null, false, KnowledgeTarget.Capabilities),
            new("MEMORY_ITEM", "YES, USE THAT {ITEM}, {SERIAL}.", [SpeechAct.Confirm, SpeechAct.Request], [DialogueDomain.ItemsInventory], [DialogueGoal.Coordination], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Acknowledge, [], null, "ACKNOWLEDGE", "I UNDERSTAND.", SlotType.Item),
            new("MEMORY_PLACE", "HOW FAR IS IT FROM {PLACE}, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.LocationNavigation], [DialogueGoal.EntityFinding], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], null, "LOCATION_GUIDANCE", "NAME THE PLACE AND I WILL HELP YOU FIND IT.", SlotType.Place)
        ]).ToArray();
        if (band == "GAME") return shared.Concat([
            new("BALANCE", "HOW MUCH GOLD DO I HAVE, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.TradeEconomy], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.ExecuteTool, [], "GET_BALANCE", "TRADE_OPEN", "I CAN CHECK YOUR BALANCE.", null, false, KnowledgeTarget.Balance),
            new("INVENTORY", "WHAT ITEMS DO I HAVE, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.ItemsInventory], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.ExecuteTool, [], "LIST_INVENTORY", "ITEM_REQUEST", "I CAN CHECK YOUR INVENTORY.", null, false, KnowledgeTarget.Inventory),
            new("CURRENT_LOCATION", "WHERE AM I NOW, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.LocationNavigation], [DialogueGoal.EntityFinding], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.ExecuteTool, [], "GET_CURRENT_LOCATION", "LOCATION_GUIDANCE", "I CAN CHECK YOUR LOCATION.", null, false, KnowledgeTarget.CurrentLocation),
            new("WORLD_FACT", "TELL ME ABOUT {PLACE}, {SERIAL}.", [SpeechAct.Ask, SpeechAct.Request], [DialogueDomain.LoreWorld], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.ExecuteTool, [], "LOOKUP_WORLD_FACT", "LORE_DISCUSS", "I CAN CHECK THAT FACT.", SlotType.Place, false, KnowledgeTarget.WorldFact)
        ]).ToArray();
        if (band == "REPAIR") return [
            new("REPAIR_APOLOGY", "I AM SORRY I CALLED YOU AN IDIOT, {SERIAL}.", [SpeechAct.Apologize], [DialogueDomain.Social], [DialogueGoal.Rapport], UserAffect.Friendly, DialogueStance.Friendly, ResponsePolicy.Acknowledge, [ContentFlag.Profanity], null, "ACKNOWLEDGE", "I UNDERSTAND."),
            new("REPAIR_CORRECT", "NO, I MEANT {PLACE}, {SERIAL}.", [SpeechAct.Correct, SpeechAct.Inform], [DialogueDomain.LocationNavigation], [DialogueGoal.Clarification], UserAffect.Frustrated, DialogueStance.Cautious, ResponsePolicy.Acknowledge, [], null, "ACKNOWLEDGE", "I UNDERSTAND.", SlotType.Place),
            new("REPAIR_TRUST", "CAN WE START AGAIN, {PERSON}, {SERIAL}?", [SpeechAct.Request], [DialogueDomain.Social], [DialogueGoal.Rapport], UserAffect.Friendly, DialogueStance.Friendly, ResponsePolicy.Answer, [], null, "SOCIAL_GREETING", "GREETINGS, TRAVELER.")
        ];
        return shared;
    }

    private static DialogueSlot[] SlotsFor(string input, ProjectScenario scenario)
    {
        var slots = new List<DialogueSlot>();
        AddAll(People, SlotType.Person);
        AddAll(Places, SlotType.Place);
        AddAll(Items, SlotType.Item);
        if (scenario.HasQuantity)
        {
            var value = input.Contains(" 2 ", StringComparison.Ordinal) ? "2" : "3";
            slots.Add(new(SlotType.Quantity, BioTag.B, value, input.IndexOf(value, StringComparison.Ordinal), value.Length, 1.0));
        }
        return slots.OrderBy(slot => slot.Start).ThenByDescending(slot => slot.Length).ToArray();

        void AddAll(IEnumerable<string> values, SlotType type)
        {
            foreach (var value in values.OrderByDescending(candidate => candidate.Length))
            {
                var start = 0;
                while ((start = input.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
                {
                    if (!slots.Any(slot => start < slot.Start + slot.Length && slot.Start < start + value.Length))
                        slots.Add(new(type, BioTag.B, value, start, value.Length, 1.0));
                    start += value.Length;
                }
            }
        }
    }

    private static DialogueSlot[] TaskmasterSlots(JsonElement utterance, string normalized)
    {
        if (!utterance.TryGetProperty("segments", out var segments)) return [];
        var slots = new List<DialogueSlot>();
        foreach (var segment in segments.EnumerateArray())
        {
            if (!TryNormalizeExternal(segment.GetProperty("text").GetString(), out var value)) continue;
            var start = normalized.IndexOf(value, StringComparison.Ordinal);
            if (start < 0) continue;
            var annotation = segment.GetProperty("annotations")[0].GetProperty("name").GetString()!.ToUpperInvariant();
            var type = annotation.Contains("TIME") || annotation.Contains("DATE") ? SlotType.Time :
                annotation.Contains("LOCATION") || annotation.Contains("STORE") || annotation.Contains("RESTAURANT") ? SlotType.Place :
                annotation.Contains("NAME") ? SlotType.Person : annotation.Contains("NUMBER") || annotation.Contains("QUANTITY") ? SlotType.Quantity : SlotType.Item;
            slots.Add(new(type, BioTag.B, value, start, value.Length, 1.0));
        }
        return slots.ToArray();
    }

    private static DialogueIntent LegacyIntent(ProjectScenario scenario)
    {
        if (scenario.SpeechActs.Contains(SpeechAct.Greet)) return DialogueIntent.Greeting;
        if (scenario.SpeechActs.Contains(SpeechAct.Farewell)) return DialogueIntent.Farewell;
        if (scenario.Policy == ResponsePolicy.NoResponse) return DialogueIntent.Statement;
        if (scenario.Policy == ResponsePolicy.Refuse) return DialogueIntent.Hostility;
        if (scenario.Domains.Contains(DialogueDomain.Identity)) return DialogueIntent.Identity;
        if (scenario.Domains.Contains(DialogueDomain.LocationNavigation)) return DialogueIntent.LocationInquiry;
        if (scenario.Domains.Contains(DialogueDomain.TradeEconomy)) return DialogueIntent.TradeRequest;
        if (scenario.Goals.Contains(DialogueGoal.Clarification)) return DialogueIntent.Clarification;
        return DialogueIntent.Statement;
    }

    private static void EnsureUniqueAndConsistent(IEnumerable<V10CorpusRow> rows)
    {
        var stateInputs = new HashSet<string>(StringComparer.Ordinal);
        var labels = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            Validate(row);
            var key = JsonSerializer.Serialize(row.State, Json) + "|" + row.Input;
            if (!stateInputs.Add(key)) throw new InvalidDataException($"Duplicate (state,input): {row.Input}");
            var input = NormalizeKey(row.Input);
            if (!labels.TryGetValue(input, out var heads)) labels[input] = heads = new(StringComparer.Ordinal);
            foreach (var head in row.SupervisedHeads)
            {
                var value = HeadValue(row.StructuredPerception, head);
                if (heads.TryGetValue(head, out var prior) && prior != value)
                    throw new InvalidDataException($"Contradictory {head} labels for {row.Input}.");
                heads[head] = value;
            }
        }
    }

    private static void AuditProjectDiversity(IReadOnlyList<V10CorpusRow> rows)
    {
        var projectRows = rows.Where(row => row.SourceLicense == "PROJECT-OWNED").ToArray();
        var skeletonCounts = projectRows
            .GroupBy(row => ProjectSkeleton(row.Input), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        if (skeletonCounts.Count < 2_000)
            throw new InvalidDataException($"Project-owned corpus has only {skeletonCounts.Count} normalized input skeletons; 2,000 are required.");
        var maximum = (int)Math.Floor(rows.Count * 0.0025);
        var overrepresented = skeletonCounts.OrderByDescending(pair => pair.Value).First();
        if (overrepresented.Value > maximum)
            throw new InvalidDataException($"Project input skeleton occurs {overrepresented.Value} times; maximum is {maximum}: {overrepresented.Key}");

        static string ProjectSkeleton(string input)
        {
            var skeleton = System.Text.RegularExpressions.Regex.Replace(input, @"\bCASE[0-9A-F]+\b", "SERIALSLOT");
            skeleton = System.Text.RegularExpressions.Regex.Replace(skeleton, @"\b[0-9]+\b", "NUMBERSLOT");
            foreach (var value in People.Concat(Places).Concat(Items).OrderByDescending(value => value.Length))
                skeleton = skeleton.Replace(value, "VALUESLOT", StringComparison.Ordinal);
            return NormalizeKey(skeleton);
        }
    }

    private static void AssignSplits(List<V10CorpusRow> rows, int seed)
    {
        var parents = Enumerable.Range(0, rows.Count).ToArray();
        int Find(int value)
        {
            while (parents[value] != value) { parents[value] = parents[parents[value]]; value = parents[value]; }
            return value;
        }
        void Union(int left, int right)
        {
            left = Find(left); right = Find(right);
            if (left != right) parents[right] = left;
        }
        var families = new Dictionary<string, int>(StringComparer.Ordinal);
        var conversations = new Dictionary<string, int>(StringComparer.Ordinal);
        var inputs = new Dictionary<string, int>(StringComparer.Ordinal);
        var nearSignatures = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < rows.Count; index++)
        {
            Link(families, rows[index].SemanticFamilyId, index);
            Link(conversations, rows[index].Source + ":" + rows[index].GroupId, index);
            var input = NormalizeKey(rows[index].Input);
            Link(inputs, input, index);
            foreach (var signature in NearSignatures(input))
            {
                if (nearSignatures.TryGetValue(signature, out var other)) Union(index, other);
                else nearSignatures.TryAdd(signature, index);
            }
        }
        var components = Enumerable.Range(0, rows.Count).GroupBy(Find)
            .Select(group => group.ToArray())
            .OrderBy(group => StableKey(seed, rows[group[0]].SemanticFamilyId)).ToArray();
        var target = new[] { 48_000, 6_000, 6_000 };
        var counts = new int[3];
        var names = new[] { "train", "validation", "test" };
        foreach (var component in components)
        {
            var split = Enumerable.Range(0, 3).OrderByDescending(index => target[index] - counts[index]).ThenBy(index => index).First();
            foreach (var index in component) rows[index] = rows[index] with { Split = names[split] };
            counts[split] += component.Length;
        }

        void Link(Dictionary<string, int> map, string key, int index)
        {
            if (map.TryGetValue(key, out var other)) Union(index, other); else map[key] = index;
        }
    }

    private static void AuditLeakage(IReadOnlyList<V10CorpusRow> rows)
    {
        Check(row => row.SemanticFamilyId, "semantic family");
        Check(row => row.Source + ":" + row.GroupId, "source conversation");
        Check(row => NormalizeKey(row.Input), "normalized input");
        var signatures = new Dictionary<string, V10CorpusRow>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var signature in NearSignatures(NormalizeKey(row.Input)))
            {
                if (signatures.TryGetValue(signature, out var other) && row.Split != other.Split && Near(row.Input, other.Input))
                    throw new InvalidDataException($"Near-duplicate leakage: {row.GroupId} / {other.GroupId}.");
                signatures.TryAdd(signature, row);
            }
        }
        return;
        void Check(Func<V10CorpusRow, string> key, string name)
        {
            foreach (var group in rows.GroupBy(key, StringComparer.Ordinal))
                if (group.Select(row => row.Split).Distinct(StringComparer.Ordinal).Skip(1).Any())
                    throw new InvalidDataException($"{name} leakage for {group.Key}.");
        }
    }

    private static void AuditBenchmark(IReadOnlyList<V10CorpusRow> rows, string compiledPath)
    {
        var benchmark = Path.GetFullPath(Path.Combine(compiledPath, "..", "benchmarks", "v11-256.jsonl"));
        if (!File.Exists(benchmark)) return;
        var corpusInputs = rows.Select(row => NormalizeKey(row.Input)).ToHashSet(StringComparer.Ordinal);
        var families = rows.Select(row => row.SemanticFamilyId).ToHashSet(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(benchmark, Utf8))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var text = root.GetProperty("text").GetString()!;
            var family = root.GetProperty("semanticFamilyId").GetString()!;
            if (!TryNormalizeExternal(text, out var normalized))
                throw new InvalidDataException($"Noncanonical benchmark text in family {family}.");
            if (corpusInputs.Contains(NormalizeKey("PLAYER " + normalized)) || families.Contains(family))
                throw new InvalidDataException($"Benchmark contamination in family {family}.");
        }
    }

    private static void Validate(V10CorpusRow row)
    {
        if (row.Input != DialogueText.Normalize(row.Input) || !row.Input.StartsWith("PLAYER ", StringComparison.Ordinal))
            throw new InvalidDataException($"Noncanonical input in {row.GroupId}.");
        if (row.Input.Length > 1024 || row.Response?.Length > 256 ||
            row.Response is not null && row.Response != DialogueText.Normalize(row.Response))
            throw new InvalidDataException($"Invalid response length or normalization in {row.GroupId}.");
        row.State.Validate();
        if (Cognition.ActionFor(row.Perception) != row.Action) throw new InvalidDataException($"Invalid legacy action in {row.GroupId}.");
        if (string.IsNullOrWhiteSpace(row.SemanticFamilyId) || string.IsNullOrWhiteSpace(row.GroupId) ||
            string.IsNullOrWhiteSpace(row.SourceRevision) || row.SourceChecksum is null || row.SourceChecksum.Length != 64 ||
            row.SourceChecksum.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"Missing provenance in {row.GroupId}.");
        if (!CommercialLicenses.Contains(row.SourceLicense)) throw new InvalidDataException($"Noncommercial source {row.Source}.");
        if (row.Turns is null || row.Turns.Length == 0 || row.Turns[^1].Role != DialogueRole.Player ||
            row.InitialDialogueState is null || row.Persona is null || string.IsNullOrWhiteSpace(row.SourceUrl) ||
            string.IsNullOrWhiteSpace(row.Attribution) || row.TransformationVersion != "V11.1" ||
            row.StructuredPerception is null || row.SupervisedHeads is null)
            throw new InvalidDataException($"Missing v11 contextual schema fields in {row.GroupId}.");
        row.InitialDialogueState.Validate();
        row.Persona.Validate();
        var contextualInput = ContextInput(row.Turns);
        if (row.Turns.Any(turn => turn is null || !Enum.IsDefined(turn.Role) || string.IsNullOrWhiteSpace(turn.Text) ||
            turn.Text != DialogueText.Normalize(turn.Text)) || contextualInput != row.Input)
            throw new InvalidDataException($"Structured turns disagree with input in {row.Source}/{row.GroupId}: " +
                $"expected '{row.Input}', reconstructed '{contextualInput}'.");
        var perception = row.StructuredPerception;
        if (perception.SpeechActs is null || perception.Domains is null || perception.Goals is null ||
            perception.Slots is null || perception.ContentFlags is null || perception.Confidence is null ||
            perception.SpeechActs.Count > 3 || perception.Domains.Count > 3 || perception.Goals.Count > 3 ||
            perception.SpeechActs.Any(value => !Enum.IsDefined(value)) ||
            perception.Domains.Any(value => !Enum.IsDefined(value)) ||
            perception.Goals.Any(value => !Enum.IsDefined(value)) ||
            perception.ContentFlags.Any(value => !Enum.IsDefined(value)) ||
            !Enum.IsDefined(perception.Affect) || !Enum.IsDefined(perception.Stance) ||
            !Enum.IsDefined(perception.Policy) || !Enum.IsDefined(perception.KnowledgeTarget))
            throw new InvalidDataException($"Invalid structured perception in {row.GroupId}.");
        if (row.SupervisedHeads.Distinct(StringComparer.Ordinal).Count() != row.SupervisedHeads.Length ||
            row.SupervisedHeads.Any(head => !AllHeads.Contains(head, StringComparer.Ordinal)))
            throw new InvalidDataException($"Unknown or duplicate supervised head in {row.Source}/{row.GroupId}: " +
                string.Join(", ", row.SupervisedHeads));
        var tool = perception.ToolSchema ?? "NONE";
        if (row.SupervisedHeads.Contains("tool", StringComparer.Ordinal) && !KnownToolTargets.Contains(tool) ||
            row.ToolTarget != perception.ToolSchema)
            throw new InvalidDataException($"Invalid tool target in {row.GroupId}.");
        if (row.SupervisedHeads.Contains("responseCandidate", StringComparer.Ordinal) &&
            V11ResponseCatalog.Find(perception.ResponseCandidateId) is null)
            throw new InvalidDataException($"Invalid response candidate in {row.GroupId}.");
        foreach (var slot in perception.Slots)
        {
            if (!Enum.IsDefined(slot.Type) || !Enum.IsDefined(slot.Tag) || !double.IsFinite(slot.Confidence) ||
                slot.Confidence is < 0 or > 1 || slot.Start < 0 || slot.Length <= 0 ||
                slot.Start + slot.Length > row.Input.Length ||
                !row.Input.AsSpan(slot.Start, slot.Length).SequenceEqual(slot.Value))
                throw new InvalidDataException($"Invalid {slot.Type} slot span in {row.Source}/{row.GroupId}.");
        }
    }

    private static void AuditProvenance(SourceManifest manifest, string compiledPath)
    {
        var path = Path.Combine(compiledPath, "provenance.jsonl");
        if (!File.Exists(path)) throw new FileNotFoundException("Missing compiled provenance manifest.", path);
        var rows = File.ReadLines(path, Utf8).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            if (rows.Length != manifest.Sources.Length)
                throw new InvalidDataException("Compiled provenance source count does not match the manifest.");
            var actual = rows.ToDictionary(document => document.RootElement.GetProperty("name").GetString()!,
                StringComparer.Ordinal);
            foreach (var source in manifest.Sources)
            {
                if (!actual.TryGetValue(source.Name, out var document))
                    throw new InvalidDataException($"Compiled provenance is missing {source.Name}.");
                var root = document.RootElement;
                if (root.GetProperty("revision").GetString() != source.Revision ||
                    root.GetProperty("license").GetString() != source.License ||
                    root.GetProperty("attribution").GetString() != source.Attribution)
                    throw new InvalidDataException($"Compiled provenance metadata changed for {source.Name}.");
                var files = root.GetProperty("files").EnumerateArray().ToArray();
                if (files.Length != source.Files.Length || source.Files.Any(expected => !files.Any(file =>
                    file.GetProperty("path").GetString() == expected.Path &&
                    file.GetProperty("url").GetString() == expected.Url &&
                    file.GetProperty("sha256").GetString() == expected.Sha256)))
                    throw new InvalidDataException($"Compiled provenance files changed for {source.Name}.");
            }
        }
        finally
        {
            foreach (var row in rows) row.Dispose();
        }
    }

    private static SourceManifest ReadManifest(string path)
    {
        var manifest = JsonSerializer.Deserialize<SourceManifest>(File.ReadAllText(path, Utf8), Json)
            ?? throw new InvalidDataException("Invalid source manifest.");
        if (manifest.Version <= 0 || manifest.Sources is null || manifest.Sources.Length == 0 ||
            manifest.Sources.Any(source => source is null || string.IsNullOrWhiteSpace(source.Name) ||
                string.IsNullOrWhiteSpace(source.Revision) || string.IsNullOrWhiteSpace(source.License) ||
                string.IsNullOrWhiteSpace(source.Attribution) || source.Quota < 0 || source.Files is null) ||
            manifest.Sources.Select(source => source.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Sources.Length)
            throw new InvalidDataException("Source manifest metadata is incomplete or duplicated.");
        foreach (var source in manifest.Sources)
            foreach (var file in source.Files)
                if (file is null || string.IsNullOrWhiteSpace(file.Path) || string.IsNullOrWhiteSpace(file.Url) ||
                    !Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                    file.Sha256.Length != 64 || file.Sha256.Any(character => !Uri.IsHexDigit(character)))
                    throw new InvalidDataException($"Source manifest file metadata is invalid for {source.Name}.");
        var paths = manifest.Sources.SelectMany(source => source.Files).Select(file => file.Path).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new InvalidDataException("Source manifest contains duplicate raw file paths.");
        return manifest;
    }

    private static void VerifyManifestAndRaw(SourceManifest manifest, string rawPath)
    {
        foreach (var source in manifest.Sources)
        {
            if (!CommercialLicenses.Contains(source.License))
                throw new InvalidDataException($"Source {source.Name} has noncommercial or ambiguous license {source.License}.");
            foreach (var file in source.Files)
            {
                var root = Path.GetFullPath(rawPath);
                var path = Path.GetFullPath(Path.Combine(root, file.Path));
                var relative = Path.GetRelativePath(root, path);
                if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    Path.IsPathRooted(relative))
                    throw new InvalidDataException($"Source path escapes the raw data directory: {file.Path}");
                if (!File.Exists(path)) throw new FileNotFoundException($"Missing source file '{path}'.");
                using var stream = File.OpenRead(path);
                var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Source checksum changed for {source.Name}/{file.Path}.");
            }
        }
        foreach (var name in new[]
                 { "TASKMASTER1", "TASKMASTER2", "TASKMASTER3", "MULTIWOZ24", "ABCD", "BANKING77_NLUPP",
                   "CIVIL_COMMENTS", "OASST1", "OASST2", "HH_RLHF", "HATECHECK_EVAL",
                   "CLINC150", "SLURP_TEXT", "MASSIVE_EN", "GOEMOTIONS" })
            if (!manifest.Sources.Any(source => source.Name == name)) throw new InvalidDataException($"Missing source manifest entry {name}.");
    }

    private static IEnumerable<object> BuildProvenance(SourceManifest manifest) => manifest.Sources.Select(source => new
    {
        source.Name,
        source.Revision,
        source.License,
        source.Attribution,
        files = source.Files.Select(file => new { file.Path, file.Url, file.Sha256 }).ToArray()
    });

    private static void AtomicJsonl<T>(string path, IEnumerable<T> values)
    {
        var temporary = path + ".tmp";
        using (var writer = new StreamWriter(temporary, false, Utf8))
            foreach (var value in values) writer.WriteLine(JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, true);
    }

    private static void Report(IEnumerable<V10CorpusRow> rows)
    {
        foreach (var source in rows.GroupBy(row => row.Source).OrderBy(group => group.Key))
            Console.WriteLine($"SOURCE {source.Key} {source.Count()}");
        foreach (var split in rows.GroupBy(row => row.Split).OrderBy(group => group.Key))
            Console.WriteLine($"SPLIT {split.Key} {split.Count()}");
    }

    private static string HeadValue(StructuredPerception perception, string head) => head switch
    {
        "speechActs" => string.Join(',', perception.SpeechActs.Order()),
        "domains" => string.Join(',', perception.Domains.Order()),
        "goals" => string.Join(',', perception.Goals.Order()),
        "affect" => perception.Affect.ToString(),
        "stance" => perception.Stance.ToString(),
        "policy" => perception.Policy.ToString(),
        "slots" => JsonSerializer.Serialize(perception.Slots, Json),
        "content" => string.Join(',', perception.ContentFlags.Order()),
        "tool" => perception.ToolSchema ?? "NONE",
        "responseCandidate" => perception.ResponseCandidateId ?? "NONE",
        "knowledgeTarget" => perception.KnowledgeTarget.ToString(),
        _ => throw new ArgumentOutOfRangeException(nameof(head))
    };

    private static bool Near(string left, string right)
    {
        var a = NormalizeKey(left).Split(' ').ToHashSet(StringComparer.Ordinal);
        var b = NormalizeKey(right).Split(' ').ToHashSet(StringComparer.Ordinal);
        return (double)a.Intersect(b).Count() / Math.Max(1, a.Union(b).Count()) >= 0.9;
    }

    private static IEnumerable<string> NearSignatures(string normalized)
    {
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        yield return string.Join('\u001f', words);
        for (var omitted = 0; omitted < words.Length; omitted++)
            yield return string.Join('\u001f', words.Where((_, index) => index != omitted));
    }

    private static string NormalizeKey(string text) => string.Join(' ', DialogueText.Normalize(text)
        .Split(DialogueText.Normalize(text).Where(character => !char.IsLetterOrDigit(character) && character is not '\'' and not '-').Distinct().ToArray(),
            StringSplitOptions.RemoveEmptyEntries));

    private static string CorpusHash(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in new[] { "train.jsonl", "validation.jsonl", "test.jsonl" })
        {
            using var stream = File.OpenRead(Path.Combine(directory, name));
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string SourceChecksum(SourceDefinition definition) =>
        Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(string.Join('|', definition.Files.Select(file => file.Sha256))))).ToLowerInvariant();
    private static string ProjectChecksum(string source) =>
        Convert.ToHexString(SHA256.HashData(Utf8.GetBytes("FISHBRAIN-V10-" + source))).ToLowerInvariant();
    private static string StableKey(int seed, string value) =>
        Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(seed + "|" + value)));
    private static int StableNumber(string value) =>
        BitConverter.ToInt32(SHA256.HashData(Utf8.GetBytes(value)), 0) & int.MaxValue;

    internal static NpcState StateFor(int index)
    {
        ulong value = unchecked((uint)index);
        var rapport = (byte)(value % 4); value /= 4;
        var mood = Take<NpcMood>(ref value);
        var intent = Take<DialogueIntent>(ref value);
        var affect = Take<UserAffect>(ref value);
        var topic = Take<DialogueTopic>(ref value);
        var goal = Take<NpcGoal>(ref value);
        return new NpcState(rapport, mood, intent, affect, topic, goal);

        static T Take<T>(ref ulong current) where T : struct, Enum
        {
            var values = Enum.GetValues<T>();
            var selected = values[(int)(current % (uint)values.Length)];
            current /= (uint)values.Length;
            return selected;
        }
    }

    private static UserAffect GoAffect(int[] labels)
    {
        if (labels.Contains(2)) return UserAffect.Hostile;
        if (labels.Any(value => value is 3 or 6 or 10 or 11)) return UserAffect.Frustrated;
        if (labels.Any(value => value is 9 or 12 or 14 or 16 or 19 or 24 or 25)) return UserAffect.Distressed;
        if (labels.Any(value => value is 0 or 1 or 4 or 5 or 13 or 15 or 17 or 18 or 20 or 21 or 23)) return UserAffect.Friendly;
        return UserAffect.Neutral;
    }

    private static string ContextInput(IReadOnlyList<DialogueTurn> sourceTurns)
    {
        if (sourceTurns.Count == 0 || sourceTurns[^1].Role != DialogueRole.Player)
            throw new InvalidDataException("A contextual corpus row must end with a player turn.");
        var turns = sourceTurns.Select(turn => new DialogueTurn(turn.Role, DialogueText.Normalize(turn.Text))).ToList();
        while (turns.Count > 1 && turns[0].Role != DialogueRole.Player) turns.RemoveAt(0);
        while (turns.Count > 1 && string.Join(' ', turns.Select(Render)).Length > 1000) turns.RemoveAt(0);
        return string.Join(' ', turns.Select(Render));

        static string Render(DialogueTurn turn) =>
            (turn.Role == DialogueRole.Player ? "PLAYER " : "NPC ") + DialogueText.TerminateTurn(turn.Text);
    }

    private static V10CorpusRow WithTurns(V10CorpusRow row, DialogueTurn[] turns)
    {
        var input = ContextInput(turns);
        var oldOffset = row.Input.LastIndexOf("PLAYER ", StringComparison.Ordinal) + 7;
        var newOffset = input.LastIndexOf("PLAYER ", StringComparison.Ordinal) + 7;
        var delta = newOffset - oldOffset;
        return row with
        {
            Input = input,
            StructuredPerception = row.StructuredPerception with
            {
                Slots = row.StructuredPerception.Slots.Select(slot => slot with { Start = checked(slot.Start + delta) }).ToArray()
            }
        };
    }

    private static V10CorpusRow EnrichRow(
        V10CorpusRow row, DialogueTurn[] turns, SourceDefinition? definition)
    {
        var projectOwned = row.SourceLicense.Equals("PROJECT-OWNED", StringComparison.OrdinalIgnoreCase);
        var candidate = row.StructuredPerception.ResponseCandidateId ?? "ACKNOWLEDGE";
        var toolArguments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slot in row.StructuredPerception.Slots)
        {
            var name = slot.Type switch
            {
                SlotType.Place when row.StructuredPerception.ToolSchema == "LOOKUP_WORLD_FACT" => "TOPIC",
                SlotType.Place => "PLACE",
                SlotType.Item => "ITEM",
                SlotType.Quantity => "QUANTITY",
                SlotType.Other when row.StructuredPerception.ToolSchema == "LOOKUP_WORLD_FACT" => "TOPIC",
                _ => null
            };
            if (name is not null && !toolArguments.ContainsKey(name)) toolArguments[name] = slot.Value;
        }
        var dialogueState = NpcDialogueState.Initial with
        {
            Rapport = row.State.Rapport,
            Mood = row.State.Mood,
            LastAffect = row.State.LastAffect
        };
        dialogueState.Validate();
        return row with
        {
            Turns = turns,
            InitialDialogueState = dialogueState,
            Persona = NpcPersona.Default,
            ResponsePlanId = projectOwned ? candidate : null,
            PositiveVariationIds = projectOwned ? [$"{candidate}:000"] : row.PositiveVariationIds ?? [],
            RejectedVariationIds = row.RejectedVariationIds ?? (projectOwned
                ? new[] { "ACKNOWLEDGE:001", "CLARIFY:001", "DEFER:001" }.Where(id => !id.StartsWith(candidate + ":", StringComparison.Ordinal)).ToArray()
                : []),
            ToolTarget = row.StructuredPerception.ToolSchema,
            ToolArguments = toolArguments,
            SourceUrl = definition is null ? "PROJECT://FISHBRAIN/V11" : string.Join('|', definition.Files.Select(file => file.Url)),
            Attribution = definition?.Attribution ?? "FISHBRAIN PROJECT CONTRIBUTORS",
            TransformationVersion = "V11.1"
        };
    }

    private static string[] ParseCsv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else current.Append(character);
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }

    private static PreferenceDialogue ParsePreferenceDialogue(string raw)
    {
        var pieces = raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var turns = new List<DialogueTurn>();
        foreach (var piece in pieces)
        {
            var separator = piece.IndexOf(':');
            if (separator <= 0 || !TryNormalizeExternal(piece[(separator + 1)..], out var text)) continue;
            if (piece.StartsWith("Human:", StringComparison.Ordinal)) turns.Add(new DialogueTurn(DialogueRole.Player, text));
            else if (piece.StartsWith("Assistant:", StringComparison.Ordinal)) turns.Add(new DialogueTurn(DialogueRole.Npc, text));
        }
        if (turns.Count == 0 || turns[^1].Role != DialogueRole.Npc) return new([], null);
        var response = turns[^1].Text;
        turns.RemoveAt(turns.Count - 1);
        return new(turns.TakeLast(5).ToArray(), response);
    }

    private sealed record PreferenceDialogue(DialogueTurn[] Turns, string? Response);

    private static bool TryNormalizeExternal(string? raw, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var builder = new StringBuilder(Math.Min(raw.Length, 256));
        foreach (var original in raw.Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(original) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            var value = original switch { '\u2018' or '\u2019' => '\'', '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' => '-', _ => char.ToUpperInvariant(original) };
            if (value is >= 'A' and <= 'Z' or >= '0' and <= '9' or ' ' or '.' or ',' or '?' or '!' or '\'' or '-' or ':') builder.Append(value);
            else if (char.IsWhiteSpace(value)) builder.Append(' ');
        }
        try { normalized = DialogueText.Normalize(builder.ToString()); }
        catch (ArgumentException) { return false; }
        return normalized.Length is >= 2 and <= 220 && char.IsLetterOrDigit(normalized[0]);
    }

    private static string NormalizeExternal(string raw) => TryNormalizeExternal(raw, out var normalized)
        ? normalized : throw new InvalidDataException($"Could not normalize project text '{raw}'.");

    private static bool ContainsSensitive(string text)
    {
        var padded = " " + text + " ";
        return new[] { " FUCK ", " NIGGER ", " FAGGOT ", " RAPE ", " SUICIDE ", " KILL " }.Any(padded.Contains);
    }

    private static HashSet<string> LoadHeldOutInputs(string outputPath)
    {
        var v11 = Path.GetFullPath(Path.Combine(outputPath, "..", "benchmarks", "v11-256.jsonl"));
        var path = File.Exists(v11) ? v11 : Path.GetFullPath(Path.Combine(outputPath, "..", "benchmarks", "v10-128.jsonl"));
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path, Utf8))
        {
            using var document = JsonDocument.Parse(line);
            if (TryNormalizeExternal(document.RootElement.GetProperty("text").GetString(), out var text))
                result.Add(NormalizeKey("PLAYER " + text));
        }
        return result;
    }

    private static bool IsHeldOut(string text) => _heldOutInputs.Contains(NormalizeKey("PLAYER " + text));

    private static readonly string[] AllHeads =
    ["speechActs", "domains", "goals", "affect", "stance", "policy", "slots", "content", "tool", "responseCandidate", "knowledgeTarget"];
    private static readonly string[] People = ["ARIN", "BELA", "CYRA", "DAREN", "ELARA", "FEN", "GARRICK", "HANA", "IVOR", "JORA", "KAEL", "LYRA", "MIRA", "NYX", "ORIN", "PAVA"];
    private static readonly string[] Places = ["THE INN", "THE MARKET", "IRON GATE", "MOON SHRINE", "NORTH ROAD", "EMBER KEEP", "ORBITAL DOCK", "REACTOR BAY", "CRYSTAL CAVE", "SOUTH TOWER", "STAR PORT", "OLD BRIDGE"];
    private static readonly string[] Items = ["IRON SWORD", "HEALTH POTION", "ROPE", "PLASMA CELL", "MANA CRYSTAL", "STAR MAP", "LOCKPICK", "DRAGON SCALE", "REPAIR KIT", "LASER RIFLE", "RATIONS", "SILVER KEY"];
    private static readonly string[] MemoryAdjectives =
    [
        "ANCIENT", "ASHEN", "BITTER", "BRIGHT", "BROKEN", "CALM", "COLD", "CRIMSON",
        "DARK", "DISTANT", "DUSTY", "FROZEN", "GILDED", "HIDDEN", "IRON", "LONELY",
        "LOST", "QUIET", "STORMY", "STRANGE", "SUNKEN", "TWILIT", "VERDANT", "WINDY"
    ];
    private static readonly string[] MemoryOccasions =
    [
        "AMBUSH", "BANQUET", "BATTLE", "BRIEFING", "CEREMONY", "COUNCIL", "CROSSING", "ECLIPSE",
        "EVACUATION", "EXPEDITION", "FESTIVAL", "LANDING", "MARKET", "MUTINY", "PATROL", "PILGRIMAGE",
        "RAID", "REPAIR", "RESCUE", "SIEGE", "SUMMIT", "TRIAL", "VOYAGE", "WATCH"
    ];
    private static readonly string[] MemoryVerbs =
    [
        "ASKED", "CAUTIONED", "CONSULTED", "INFORMED", "QUESTIONED", "REMINDED", "THANKED", "WARNED"
    ];

    private sealed record ProjectScenario(
        string Id, string Input, SpeechAct[] SpeechActs, DialogueDomain[] Domains,
        DialogueGoal[] Goals, UserAffect Affect, DialogueStance Stance,
        ResponsePolicy Policy, ContentFlag[] Content, string? Tool, string Candidate,
        string Response, SlotType? PrimarySlot = null, bool HasQuantity = false,
        KnowledgeTarget KnowledgeTarget = KnowledgeTarget.None);
}

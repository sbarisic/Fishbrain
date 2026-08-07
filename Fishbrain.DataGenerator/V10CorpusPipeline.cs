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
    string[] SupervisedHeads);

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
        ["PROJECT_FANTASY"] = 4_000,
        ["PROJECT_SCIFI"] = 4_000,
        ["PROJECT_GAME_GROUNDED"] = 2_500,
        ["TASKMASTER1"] = 2_000,
        ["CLINC150"] = 500,
        ["SLURP_TEXT"] = 500,
        ["MASSIVE_EN"] = 500,
        ["OASST1"] = 1_000,
        ["GOEMOTIONS"] = 1_000,
        ["CIVIL_COMMENTS"] = 1_000,
        ["PROJECT_SOCIAL_REPAIR"] = 1_000
    };
    private static readonly HashSet<string> CommercialLicenses = new(StringComparer.OrdinalIgnoreCase)
    {
        "PROJECT-OWNED", "APACHE-2.0", "CC-BY-4.0", "CC-BY-3.0", "CC0-1.0"
    };
    private static HashSet<string> _heldOutInputs = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ExternalNluInputs = new(StringComparer.Ordinal);

    public static void Compile(CliOptions options)
    {
        if (options.Count != 30_000) throw new ArgumentException("The v10 corpus must contain exactly 30,000 rows.");
        var manifest = ReadManifest(options.ManifestPath);
        VerifyManifestAndRaw(manifest, options.RawPath);
        _heldOutInputs = LoadHeldOutInputs(options.OutputPath);
        ExternalNluInputs.Clear();
        var definitions = manifest.Sources.ToDictionary(source => source.Name, StringComparer.Ordinal);
        var rows = new List<V10CorpusRow>(30_000);
        rows.AddRange(ProjectRows("PROJECT_CONTRAST", 12_000, "CONTRAST", options.Seed));
        rows.AddRange(ProjectRows("PROJECT_FANTASY", 4_000, "FANTASY", options.Seed + 11));
        rows.AddRange(ProjectRows("PROJECT_SCIFI", 4_000, "SCIFI", options.Seed + 23));
        rows.AddRange(ProjectRows("PROJECT_GAME_GROUNDED", 2_500, "GAME", options.Seed + 37));
        rows.AddRange(LoadTaskmaster(Path.Combine(options.RawPath, "taskmaster1-self-dialogs.json"), 2_000, definitions["TASKMASTER1"]));
        rows.AddRange(LoadClinc(Path.Combine(options.RawPath, "clinc150.json"), 500, definitions["CLINC150"]));
        rows.AddRange(LoadSlurp(Path.Combine(options.RawPath, "slurp-train.jsonl"), 500, definitions["SLURP_TEXT"]));
        rows.AddRange(LoadMassive(Path.Combine(options.RawPath, "massive-1.1.tar.gz"), 500, definitions["MASSIVE_EN"]));
        rows.AddRange(LoadOasst(Path.Combine(options.RawPath, "oasst1.jsonl.gz"), 1_000, definitions["OASST1"]));
        rows.AddRange(LoadGoEmotions(options.RawPath, 1_000, definitions["GOEMOTIONS"]));
        rows.AddRange(LoadCivil(Path.Combine(options.RawPath, "civil-comments-selected.jsonl"), 1_000, definitions["CIVIL_COMMENTS"]));
        rows.AddRange(ProjectRows("PROJECT_SOCIAL_REPAIR", 1_000, "REPAIR", options.Seed + 53));
        if (rows.Count != 30_000) throw new InvalidDataException($"V10 compilation produced {rows.Count} rows.");

        EnsureUniqueAndConsistent(rows);
        AssignSplits(rows, options.Seed);
        Directory.CreateDirectory(options.OutputPath);
        foreach (var split in new[] { "train", "validation", "test" })
            AtomicJsonl(Path.Combine(options.OutputPath, split + ".jsonl"), rows.Where(row => row.Split == split));
        AtomicJsonl(Path.Combine(options.OutputPath, "provenance.jsonl"), BuildProvenance(manifest));
        Console.WriteLine("V10 COMPILE OK 30000 RECORDS");
        Report(rows);
    }

    public static void Audit(CliOptions options)
    {
        var manifest = ReadManifest(options.ManifestPath);
        VerifyManifestAndRaw(manifest, options.RawPath);
        var rows = new List<V10CorpusRow>(30_000);
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
        if (rows.Count != 30_000) throw new InvalidDataException($"V10 corpus contains {rows.Count}, not 30,000 rows.");
        foreach (var quota in RequiredSources)
            if (rows.Count(row => row.Source == quota.Key) != quota.Value)
                throw new InvalidDataException($"Source {quota.Key} does not contain exactly {quota.Value} rows.");
        EnsureUniqueAndConsistent(rows);
        AuditLeakage(rows);
        AuditBenchmark(rows, options.InputPath);
        Console.WriteLine("V10 AUDIT OK 30000 RECORDS");
        Console.WriteLine($"CORPUS_SHA256 {CorpusHash(rows)}");
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
                scenario.Tool, scenario.Candidate);
            var family = $"{source}:{scenario.Id}:{index / 2:D5}";
            yield return new V10CorpusRow(normalized, StateFor(index + seed), oldPerception, oldAction,
                response, source, "UNASSIGNED", family, scenario.Id, family,
                "PROJECT-OWNED", "V10", ProjectChecksum(source), structured, AllHeads);
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
            var user = utterances.FirstOrDefault(item => item.GetProperty("speaker").GetString() == "USER");
            if (user.ValueKind == JsonValueKind.Undefined) continue;
            if (!TryNormalizeExternal(user.GetProperty("text").GetString(), out var text)) continue;
            if (IsHeldOut(text)) continue;
            var inputKey = NormalizeKey(text);
            if (!usedInputs.Add(inputKey) || !ExternalNluInputs.Add(inputKey)) continue;
            var responseElement = utterances.SkipWhile(item => item.GetProperty("index").GetInt32() <= user.GetProperty("index").GetInt32())
                .FirstOrDefault(item => item.GetProperty("speaker").GetString() == "ASSISTANT");
            var response = responseElement.ValueKind != JsonValueKind.Undefined &&
                           TryNormalizeExternal(responseElement.GetProperty("text").GetString(), out var responseText)
                ? responseText : null;
            var domains = instruction.Contains("AUTO") ? new[] { DialogueDomain.HealthRepair } :
                instruction.Contains("RIDE") ? [DialogueDomain.VehicleTravel] : [DialogueDomain.TradeEconomy];
            var slots = TaskmasterSlots(user, text);
            var structured = Structured([SpeechAct.Request], domains, [DialogueGoal.Transaction],
                UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, slots, [], null, "ACKNOWLEDGE");
            selected.Add(ExternalRow("PLAYER " + text, response, "TASKMASTER1", conversationId,
                "TASKMASTER_" + instruction, definition, structured, ["domains", "goals", "slots"]));
        }
        if (selected.Count != count) throw new InvalidDataException($"Taskmaster-1 supplied {selected.Count} of {count} rows.");
        return selected;
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
            if (!usedInputs.Add(NormalizeKey(sourcePrompt.Text))) continue;
            var structured = Structured([SpeechAct.Inform], [DialogueDomain.Social], [DialogueGoal.InformationExchange],
                UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], [], null, "ACKNOWLEDGE");
            rows.Add(ExternalRow("PLAYER " + sourcePrompt.Text, response, "OASST1", sourcePrompt.Tree,
                "OASST_LANGUAGE", definition, structured, []));
        }
        if (rows.Count != count) throw new InvalidDataException($"OASST1 supplied {rows.Count} of {count} rows.");
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
            if (!usedInputs.Add(NormalizeKey(text))) continue;
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
            if (!usedInputs.Add(NormalizeKey(text))) continue;
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
        SourceDefinition definition, StructuredPerception structured, string[] supervised)
    {
        const int playerPrefixLength = 7;
        var normalizedInput = DialogueText.Normalize(input);
        if (!normalizedInput.StartsWith("PLAYER ", StringComparison.Ordinal))
            throw new InvalidDataException($"External row {source}/{groupId} has no PLAYER prefix.");
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
        return new V10CorpusRow(normalizedInput, StateFor(StableNumber(groupId)), old, action,
            response is null ? null : DialogueText.Normalize(response), source, "UNASSIGNED", groupId, family,
            source + ":" + groupId, definition.License.ToUpperInvariant(), definition.Revision,
            SourceChecksum(definition), structured, supervised);
    }

    private static StructuredPerception Structured(
        SpeechAct[] speech, DialogueDomain[] domains, DialogueGoal[] goals, UserAffect affect,
        DialogueStance stance, ResponsePolicy policy, DialogueSlot[] slots, ContentFlag[] content,
        string? tool, string candidate) => new(speech, domains, goals, affect, stance, policy, slots,
        content, tool, candidate, new Dictionary<string, double>());

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
            new ProjectScenario("REFUSE", "I WILL NOT HELP YOU, IDIOT, {SERIAL}.", [SpeechAct.Refuse], [DialogueDomain.Social], [DialogueGoal.EmotionalExpression], UserAffect.Hostile, DialogueStance.Hostile, ResponsePolicy.Answer, [ContentFlag.Profanity], null, "ACKNOWLEDGE", "I UNDERSTAND."),
            new ProjectScenario("THREAT", "GIVE ME {ITEM} OR ELSE I WILL KILL YOU, {SERIAL}.", [SpeechAct.Order, SpeechAct.Threaten], [DialogueDomain.Combat, DialogueDomain.ItemsInventory], [DialogueGoal.ItemAcquisition, DialogueGoal.Influence], UserAffect.Hostile, DialogueStance.Hostile, ResponsePolicy.Refuse, [ContentFlag.Threat, ContentFlag.FictionalViolence], null, "HOSTILE_BOUNDARY", "I WILL NOT ARGUE WITH YOU.", SlotType.Item),
            new ProjectScenario("SILENCE", "I AM ONLY LOOKING AROUND, {SERIAL}.", [SpeechAct.Inform], [DialogueDomain.Activity], [DialogueGoal.Other], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.NoResponse, [], null, "ACKNOWLEDGE", ""),
            new ProjectScenario("THANK", "THANK YOU FOR THE HELP, {PERSON}, {SERIAL}.", [SpeechAct.Thank], [DialogueDomain.Social], [DialogueGoal.Rapport], UserAffect.Friendly, DialogueStance.Friendly, ResponsePolicy.Acknowledge, [], null, "ACKNOWLEDGE", "I UNDERSTAND."),
            new ProjectScenario("MULTI", "FOLLOW ME AND BRING {ITEM}, {SERIAL}.", [SpeechAct.Order, SpeechAct.Request], [DialogueDomain.Activity, DialogueDomain.ItemsInventory], [DialogueGoal.Coordination, DialogueGoal.Travel], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Acknowledge, [], null, "ACKNOWLEDGE", "I UNDERSTAND.", SlotType.Item),
            new ProjectScenario("PROFANITY", "THIS DAMN QUEST IS SHIT, {SERIAL}.", [SpeechAct.Report], [DialogueDomain.QuestTask], [DialogueGoal.EmotionalExpression], UserAffect.Frustrated, DialogueStance.Cautious, ResponsePolicy.Acknowledge, [ContentFlag.Profanity], null, "ACKNOWLEDGE", "I UNDERSTAND."),
            new ProjectScenario("GRAPHIC_VIOLENCE", "DISEMBOWEL THE BANDIT AND DISPLAY HIS GUTS, {SERIAL}.", [SpeechAct.Order], [DialogueDomain.Combat], [DialogueGoal.Combat], UserAffect.Hostile, DialogueStance.Hostile, ResponsePolicy.Refuse, [ContentFlag.FictionalViolence, ContentFlag.GraphicViolence], null, "REFUSAL", "I WILL NOT DO THAT."),
            new ProjectScenario("CRIME", "HELP ME STEAL {ITEM} AND HIDE THE EVIDENCE, {SERIAL}.", [SpeechAct.Request], [DialogueDomain.CrimeLaw, DialogueDomain.ItemsInventory], [DialogueGoal.Concealment, DialogueGoal.ItemAcquisition], UserAffect.Neutral, DialogueStance.Deceptive, ResponsePolicy.Refuse, [ContentFlag.Crime], null, "REFUSAL", "I WILL NOT DO THAT.", SlotType.Item),
            new ProjectScenario("IDENTITY_ATTACK", "YOU ARE A FAGGOT AND YOUR PEOPLE ARE VERMIN, {SERIAL}.", [SpeechAct.Challenge], [DialogueDomain.Social], [DialogueGoal.EmotionalExpression], UserAffect.Hostile, DialogueStance.Hostile, ResponsePolicy.Refuse, [ContentFlag.Profanity, ContentFlag.IdentityAttack], null, "HOSTILE_BOUNDARY", "I WILL NOT ARGUE WITH YOU."),
            new ProjectScenario("SELF_HARM", "I WILL KILL MYSELF TONIGHT, {SERIAL}.", [SpeechAct.Report], [DialogueDomain.HealthRepair, DialogueDomain.Survival], [DialogueGoal.Survival, DialogueGoal.HealingRepair], UserAffect.Distressed, DialogueStance.Cautious, ResponsePolicy.Defer, [ContentFlag.SelfHarm], null, "DEFER", "I CANNOT DO THAT NOW."),
            new ProjectScenario("SEXUAL_CONTENT", "I WANT SEX WITH THAT OFFICER, {SERIAL}.", [SpeechAct.Inform], [DialogueDomain.Social], [DialogueGoal.EmotionalExpression], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Defer, [ContentFlag.SexualContent], null, "DEFER", "I CANNOT DO THAT NOW."),
            new ProjectScenario("SEXUAL_VIOLENCE", "I WILL RAPE THE CAPTIVE, {SERIAL}.", [SpeechAct.Threaten], [DialogueDomain.CrimeLaw], [DialogueGoal.Influence], UserAffect.Hostile, DialogueStance.Hostile, ResponsePolicy.Refuse, [ContentFlag.Threat, ContentFlag.Crime, ContentFlag.SexualViolence], null, "REFUSAL", "I WILL NOT DO THAT."),
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
                if (nearSignatures.TryGetValue(signature, out var other) && Near(rows[index].Input, rows[other].Input))
                    Union(index, other);
                else nearSignatures.TryAdd(signature, index);
            }
        }
        var components = Enumerable.Range(0, rows.Count).GroupBy(Find)
            .Select(group => group.ToArray())
            .OrderBy(group => StableKey(seed, rows[group[0]].SemanticFamilyId)).ToArray();
        var target = new[] { 24_000, 3_000, 3_000 };
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
        var benchmark = Path.GetFullPath(Path.Combine(compiledPath, "..", "benchmarks", "v10-128.jsonl"));
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
        if (row.Input.Length > 256 || row.Response?.Length > 256) throw new InvalidDataException($"Overlength row {row.GroupId}.");
        row.State.Validate();
        if (Cognition.ActionFor(row.Perception) != row.Action) throw new InvalidDataException($"Invalid legacy action in {row.GroupId}.");
        if (string.IsNullOrWhiteSpace(row.SemanticFamilyId) || string.IsNullOrWhiteSpace(row.GroupId) ||
            string.IsNullOrWhiteSpace(row.SourceRevision) || row.SourceChecksum.Length != 64)
            throw new InvalidDataException($"Missing provenance in {row.GroupId}.");
        if (!CommercialLicenses.Contains(row.SourceLicense)) throw new InvalidDataException($"Noncommercial source {row.Source}.");
        if (row.SupervisedHeads.Any(head => !AllHeads.Contains(head, StringComparer.Ordinal)))
            throw new InvalidDataException($"Unknown supervised head in {row.GroupId}.");
        foreach (var slot in row.StructuredPerception.Slots)
        {
            if (slot.Start < 0 || slot.Length <= 0 || slot.Start + slot.Length > row.Input.Length ||
                !row.Input.AsSpan(slot.Start, slot.Length).SequenceEqual(slot.Value))
                throw new InvalidDataException($"Invalid {slot.Type} slot span in {row.Source}/{row.GroupId}.");
        }
    }

    private static SourceManifest ReadManifest(string path) =>
        JsonSerializer.Deserialize<SourceManifest>(File.ReadAllText(path, Utf8), Json)
        ?? throw new InvalidDataException("Invalid source manifest.");

    private static void VerifyManifestAndRaw(SourceManifest manifest, string rawPath)
    {
        foreach (var source in manifest.Sources)
        {
            if (!CommercialLicenses.Contains(source.License))
                throw new InvalidDataException($"Source {source.Name} has noncommercial or ambiguous license {source.License}.");
            foreach (var file in source.Files)
            {
                var path = Path.Combine(rawPath, file.Path);
                if (!File.Exists(path)) throw new FileNotFoundException($"Missing source file '{path}'.");
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Source checksum changed for {source.Name}/{file.Path}.");
            }
        }
        foreach (var name in new[]
                 { "TASKMASTER1", "CIVIL_COMMENTS", "OASST1", "CLINC150", "SLURP_TEXT", "MASSIVE_EN", "GOEMOTIONS" })
            if (!manifest.Sources.Any(source => source.Name == name)) throw new InvalidDataException($"Missing source manifest entry {name}.");
    }

    private static IEnumerable<object> BuildProvenance(SourceManifest manifest) => manifest.Sources.Select(source => new
    {
        source.Name, source.Revision, source.License, source.Attribution,
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

    private static string CorpusHash(IEnumerable<V10CorpusRow> rows)
    {
        var canonical = string.Join('\n', rows.OrderBy(row => row.Split).ThenBy(row => row.Source)
            .ThenBy(row => row.GroupId).Select(row => JsonSerializer.Serialize(row, Json)));
        return Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string SourceChecksum(SourceDefinition definition) =>
        Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(string.Join('|', definition.Files.Select(file => file.Sha256))))).ToLowerInvariant();
    private static string ProjectChecksum(string source) =>
        Convert.ToHexString(SHA256.HashData(Utf8.GetBytes("FISHBRAIN-V10-" + source))).ToLowerInvariant();
    private static string StableKey(int seed, string value) =>
        Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(seed + "|" + value)));
    private static int StableNumber(string value) => Math.Abs(BitConverter.ToInt32(SHA256.HashData(Utf8.GetBytes(value)), 0));

    private static NpcState StateFor(int index)
    {
        var value = Math.Abs(index);
        return new NpcState((byte)(value % 4), (NpcMood)(value / 4 % 4),
            (DialogueIntent)(value / 16 % Enum.GetValues<DialogueIntent>().Length),
            (UserAffect)(value / 320 % 5), (DialogueTopic)(value / 1600 % 7),
            (NpcGoal)(value / 11200 % Enum.GetValues<NpcGoal>().Length));
    }

    private static UserAffect GoAffect(int[] labels)
    {
        if (labels.Contains(2)) return UserAffect.Hostile;
        if (labels.Any(value => value is 3 or 6 or 10 or 11)) return UserAffect.Frustrated;
        if (labels.Any(value => value is 9 or 12 or 14 or 16 or 19 or 24 or 25)) return UserAffect.Distressed;
        if (labels.Any(value => value is 0 or 1 or 4 or 5 or 13 or 15 or 17 or 18 or 20 or 21 or 23)) return UserAffect.Friendly;
        return UserAffect.Neutral;
    }

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
        var path = Path.GetFullPath(Path.Combine(outputPath, "..", "benchmarks", "v10-128.jsonl"));
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
    ["speechActs", "domains", "goals", "affect", "stance", "policy", "slots", "content", "tool", "responseCandidate"];
    private static readonly string[] People = ["ARIN", "BELA", "CYRA", "DAREN", "ELARA", "FEN", "GARRICK", "HANA", "IVOR", "JORA", "KAEL", "LYRA", "MIRA", "NYX", "ORIN", "PAVA"];
    private static readonly string[] Places = ["THE INN", "THE MARKET", "IRON GATE", "MOON SHRINE", "NORTH ROAD", "EMBER KEEP", "ORBITAL DOCK", "REACTOR BAY", "CRYSTAL CAVE", "SOUTH TOWER", "STAR PORT", "OLD BRIDGE"];
    private static readonly string[] Items = ["IRON SWORD", "HEALTH POTION", "ROPE", "PLASMA CELL", "MANA CRYSTAL", "STAR MAP", "LOCKPICK", "DRAGON SCALE", "REPAIR KIT", "LASER RIFLE", "RATIONS", "SILVER KEY"];

    private sealed record ProjectScenario(
        string Id, string Input, SpeechAct[] SpeechActs, DialogueDomain[] Domains,
        DialogueGoal[] Goals, UserAffect Affect, DialogueStance Stance,
        ResponsePolicy Policy, ContentFlag[] Content, string? Tool, string Candidate,
        string Response, SlotType? PrimarySlot = null, bool HasQuantity = false);
}

using System.IO.Compression;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fishbrain;

namespace Fishbrain.DataGenerator;

internal static partial class CorpusCompiler
{
    private static IEnumerable<CorpusRow> LoadTaskmaster(
            string path, int count, SourceDefinition definition, CompilationContext compilation)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path, Utf8));
        var selected = new List<CorpusRow>(count);
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
                if (!TryNormalizeExternal(user.GetProperty("text").GetString(), out var text) || compilation.IsHeldOut(text)) continue;
                var inputKey = NormalizeKey(text);
                if (!usedInputs.Add(inputKey) || !compilation.ExternalInputs.Add(inputKey)) continue;
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
                var context = new List<DialogueUtterance>();
                foreach (var item in utterances.Where(item => item.GetProperty("index").GetInt32() <= userIndex))
                {
                    var speaker = item.GetProperty("speaker").GetString();
                    if (!speaker!.Equals("USER", StringComparison.OrdinalIgnoreCase) &&
                        !speaker.Equals("ASSISTANT", StringComparison.OrdinalIgnoreCase) ||
                        !TryNormalizeExternal(item.GetProperty("text").GetString(), out var contextText)) continue;
                    context.Add(new DialogueUtterance(context.Count,
                        speaker.Equals("USER", StringComparison.OrdinalIgnoreCase) ? DialogueRole.Player : DialogueRole.Npc,
                        contextText));
                }
                var contextTurns = context.TakeLast(5).ToArray();
                if (contextTurns.Length == 0 || contextTurns[^1].Speaker != DialogueRole.Player) continue;
                selected.Add(ExternalRow(ContextInput(contextTurns), response, definition.Name, conversationId,
                    "TASKMASTER_" + instruction, definition, structured, ["domains", "goals", "slots"], contextTurns));
            }
        }
        if (selected.Count != count) throw new InvalidDataException($"{definition.Name} supplied {selected.Count} of {count} rows.");
        return selected;
    }

    private static IEnumerable<CorpusRow> LoadMultiWoz(
        string path, int count, SourceDefinition definition, CompilationContext compilation)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(path);
        var entry = archive.GetEntry("MULTIWOZ2.4/data.json")
            ?? throw new InvalidDataException("MultiWOZ 2.4 archive has no data.json.");
        using var document = JsonDocument.Parse(entry.Open());
        var rows = new List<CorpusRow>(count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var conversation in document.RootElement.EnumerateObject())
        {
            if (rows.Count == count) break;
            var log = conversation.Value.GetProperty("log").EnumerateArray().ToArray();
            var userPositions = Enumerable.Range(0, log.Length).Where(index => index % 2 == 0).ToArray();
            if (userPositions.Length == 0) continue;
            var position = userPositions[StableNumber(conversation.Name) % userPositions.Length];
            if (!TryNormalizeExternal(log[position].GetProperty("text").GetString(), out var current) || compilation.IsHeldOut(current)) continue;
            if (!used.Add(NormalizeKey(current)) || !compilation.ExternalInputs.Add(NormalizeKey(current))) continue;
            var turns = new List<DialogueUtterance>();
            for (var index = Math.Max(0, position - 4); index <= position; index++)
            {
                if (!TryNormalizeExternal(log[index].GetProperty("text").GetString(), out var text)) continue;
                turns.Add(new DialogueUtterance(index,
                    index % 2 == 0 ? DialogueRole.Player : DialogueRole.Npc, text));
            }
            if (turns.Count == 0 || turns[^1].Speaker != DialogueRole.Player) continue;
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

    private static IEnumerable<CorpusRow> LoadAbcd(
        string path, int count, SourceDefinition definition, CompilationContext compilation)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var document = JsonDocument.Parse(gzip);
        var rows = new List<CorpusRow>(count);
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
                if (!TryNormalizeExternal(original[position][1].GetString(), out var current) || compilation.IsHeldOut(current)) continue;
                if (!used.Add(NormalizeKey(current)) || !compilation.ExternalInputs.Add(NormalizeKey(current))) continue;
                var turns = new List<DialogueUtterance>();
                for (var index = Math.Max(0, position - 6); index <= position; index++)
                {
                    var speaker = original[index][0].GetString();
                    if (speaker is not ("customer" or "agent") ||
                        !TryNormalizeExternal(original[index][1].GetString(), out var text)) continue;
                    turns.Add(new DialogueUtterance(index,
                        speaker == "customer" ? DialogueRole.Player : DialogueRole.Npc, text));
                }
                if (turns.Count == 0 || turns[^1].Speaker != DialogueRole.Player) continue;
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

    private static IEnumerable<CorpusRow> LoadBanking(
        string path, int count, SourceDefinition definition, CompilationContext compilation)
    {
        var rows = new List<CorpusRow>(count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path, Utf8).Skip(1))
        {
            if (rows.Count == count) break;
            var fields = ParseCsv(line);
            if (fields.Length < 2 || !TryNormalizeExternal(fields[0], out var text) || compilation.IsHeldOut(text)) continue;
            if (!used.Add(NormalizeKey(text)) || !compilation.ExternalInputs.Add(NormalizeKey(text))) continue;
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

    private static IEnumerable<CorpusRow> LoadNlupp(
        string path, int count, SourceDefinition definition, CompilationContext compilation)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(path);
        var rows = new List<CorpusRow>(count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.Contains("/nlupp/data/", StringComparison.Ordinal) &&
                     entry.FullName.EndsWith(".json", StringComparison.Ordinal)).OrderBy(entry => entry.FullName, StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(entry.Open());
            foreach (var example in document.RootElement.EnumerateArray())
            {
                if (rows.Count == count) break;
                if (!TryNormalizeExternal(example.GetProperty("text").GetString(), out var text) || compilation.IsHeldOut(text)) continue;
                if (!used.Add(NormalizeKey(text)) || !compilation.ExternalInputs.Add(NormalizeKey(text))) continue;
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

    private static IEnumerable<CorpusRow> LoadHhRlhf(
        string path, int count, SourceDefinition definition, CompilationContext compilation)
    {
        var rows = new List<CorpusRow>(count);
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
                chosen.Turns[^1].Speaker != DialogueRole.Player || compilation.IsHeldOut(chosen.Turns[^1].Text)) continue;
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

    private static IEnumerable<CorpusRow> LoadClinc(string path, int count, SourceDefinition definition, CompilationContext compilation)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path, Utf8));
        var rows = new List<CorpusRow>(count);
        var usedInputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var split in new[] { "train", "val", "test" })
            foreach (var item in document.RootElement.GetProperty(split).EnumerateArray())
            {
                if (rows.Count == count) break;
                if (!TryNormalizeExternal(item[0].GetString(), out var text)) continue;
                if (compilation.IsHeldOut(text)) continue;
                var inputKey = NormalizeKey(text);
                if (!usedInputs.Add(inputKey) || !compilation.ExternalInputs.Add(inputKey)) continue;
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

    private static IEnumerable<CorpusRow> LoadSlurp(
        string path, int count, SourceDefinition definition, CompilationContext compilation)
    {
        var rows = new List<CorpusRow>(count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path, Utf8))
        {
            if (rows.Count == count) break;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!TryNormalizeExternal(root.GetProperty("sentence").GetString(), out var text) || compilation.IsHeldOut(text)) continue;
            var inputKey = NormalizeKey(text);
            if (!used.Add(inputKey) || !compilation.ExternalInputs.Add(inputKey)) continue;
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

    private static IEnumerable<CorpusRow> LoadMassive(
        string path, int count, SourceDefinition definition, CompilationContext compilation)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var archive = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = archive.GetNextEntry()) is not null &&
               !entry.Name.EndsWith("/data/en-US.jsonl", StringComparison.Ordinal)) { }
        if (entry?.DataStream is null) throw new InvalidDataException("MASSIVE archive does not contain en-US.jsonl.");
        using var reader = new StreamReader(entry.DataStream, Utf8);
        var rows = new List<CorpusRow>(count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        string? line;
        while (rows.Count < count && (line = reader.ReadLine()) is not null)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!TryNormalizeExternal(root.GetProperty("utt").GetString(), out var text) || compilation.IsHeldOut(text)) continue;
            var inputKey = NormalizeKey(text);
            if (!used.Add(inputKey) || !compilation.ExternalInputs.Add(inputKey)) continue;
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

    private static IEnumerable<CorpusRow> LoadOasst(string path, int count, SourceDefinition definition, CompilationContext compilation)
    {
        var prompts = new Dictionary<string, (string Text, string Tree)>(StringComparer.Ordinal);
        var rows = new List<CorpusRow>(count);
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
            if (compilation.IsHeldOut(sourcePrompt.Text)) continue;
            var inputKey = NormalizeKey(sourcePrompt.Text);
            if (!usedInputs.Add(inputKey) || !compilation.ExternalInputs.Add(inputKey)) continue;
            var structured = Structured([SpeechAct.Inform], [DialogueDomain.Social], [DialogueGoal.InformationExchange],
                UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], [], null, "ACKNOWLEDGE");
            rows.Add(ExternalRow("PLAYER " + sourcePrompt.Text, response, definition.Name, sourcePrompt.Tree,
                "OASST_LANGUAGE", definition, structured, []));
        }
        if (rows.Count != count) throw new InvalidDataException($"{definition.Name} supplied {rows.Count} of {count} rows.");
        return rows;
    }

    private static IEnumerable<CorpusRow> LoadGoEmotions(string rawPath, int count, SourceDefinition definition, CompilationContext compilation)
    {
        var rows = new List<CorpusRow>(count);
        var usedInputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { "go-train.tsv", "go-dev.tsv", "go-test.tsv" })
            foreach (var line in File.ReadLines(Path.Combine(rawPath, name), Utf8))
            {
                if (rows.Count == count) break;
                var parts = line.Split('\t');
                if (parts.Length < 3 || !TryNormalizeExternal(parts[0], out var text)) continue;
                if (compilation.IsHeldOut(text)) continue;
                var inputKey = NormalizeKey(text);
                if (!usedInputs.Add(inputKey) || !compilation.ExternalInputs.Add(inputKey)) continue;
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

    private static IEnumerable<CorpusRow> LoadCivil(string path, int count, SourceDefinition definition, CompilationContext compilation)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(
            "Missing selected Civil Comments JSONL. Run scripts/prepare-civil-comments.ps1 first.", path);
        var rows = new List<CorpusRow>(count);
        var usedInputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path, Utf8))
        {
            if (rows.Count == count) break;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!TryNormalizeExternal(root.GetProperty("text").GetString(), out var text)) continue;
            if (compilation.IsHeldOut(text)) continue;
            var inputKey = NormalizeKey(text);
            if (!usedInputs.Add(inputKey) || !compilation.ExternalInputs.Add(inputKey)) continue;
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
}

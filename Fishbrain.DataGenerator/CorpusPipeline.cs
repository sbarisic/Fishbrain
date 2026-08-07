using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fishbrain;

namespace Fishbrain.DataGenerator;

internal sealed record SourceManifest(int Version, SourceDefinition[] Sources);
internal sealed record SourceDefinition(string Name, string Revision, string License, string Attribution, int Quota, SourceFile[] Files);
internal sealed record SourceFile(string Path, string Url, string Sha256);

internal sealed record TeachingRow(
    string Input,
    NpcState State,
    TurnPerception Perception,
    ResponseAction Action,
    string? Response,
    string Source,
    string Split,
    string GroupId,
    string? Family = null);

internal sealed record Candidate(
    string Input,
    NpcState State,
    TurnPerception Perception,
    string? Response,
    string Source,
    string GroupId,
    string? Family = null);

internal static class CorpusPipeline
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) }
    };

    public static async Task FetchAsync(CliOptions options)
    {
        var manifest = ReadManifest(options.ManifestPath);
        Directory.CreateDirectory(options.RawPath);
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Fishbrain-DataGenerator/4");

        foreach (var source in manifest.Sources)
        foreach (var file in source.Files)
        {
            var destination = Path.GetFullPath(Path.Combine(options.RawPath, file.Path));
            if (File.Exists(destination) && Hash(destination).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"VERIFIED {source.Name} {file.Path}");
                continue;
            }

            var temporary = destination + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var input = await client.GetStreamAsync(file.Url))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await input.CopyToAsync(output);
                var actual = Hash(temporary);
                if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SHA-256 mismatch for {source.Name}/{file.Path}: expected {file.Sha256}, got {actual}.");
                File.Move(temporary, destination, true);
                Console.WriteLine($"FETCHED {source.Name} {file.Path}");
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        Console.WriteLine($"RAW {Path.GetFullPath(options.RawPath)}");
    }

    public static void Compile(CliOptions options)
    {
        var manifest = ReadManifest(options.ManifestPath);
        VerifyRaw(manifest, options.RawPath);
        var quotas = ScaleQuotas(options.Count, manifest);
        var review = new List<object>();
        var rows = new List<TeachingRow>(options.Count);

        rows.AddRange(BuildSynthetic(quotas["SYNTHETIC"], options.Seed));
        AddExternal(rows, LoadOasst(Path.Combine(options.RawPath, "oasst1.jsonl.gz"), review), quotas["OASST1"], options.Seed);
        AddExternal(rows, LoadClinc(Path.Combine(options.RawPath, "clinc150.json"), review), quotas["CLINC150"], options.Seed);
        AddExternal(rows, LoadGoEmotions(options.RawPath, review), quotas["GOEMOTIONS"], options.Seed);

        if (rows.Count != options.Count)
            throw new InvalidDataException($"Compilation produced {rows.Count} of {options.Count} requested records.");

        var ordered = rows.OrderBy(row => StableKey(options.Seed, row.Source, row.GroupId, row.Input), StringComparer.Ordinal).ToArray();
        Directory.CreateDirectory(options.OutputPath);
        foreach (var split in new[] { "train", "validation", "test" })
            AtomicJsonl(Path.Combine(options.OutputPath, split + ".jsonl"), ordered.Where(row => row.Split == split));
        AtomicJsonl(Path.Combine(options.OutputPath, "review.jsonl"), review.Take(5000));

        Console.WriteLine($"REQUESTED {options.Count}");
        Console.WriteLine($"WROTE {ordered.Length}");
        foreach (var group in ordered.GroupBy(x => x.Source).OrderBy(x => x.Key)) Console.WriteLine($"SOURCE {group.Key} {group.Count()}");
        foreach (var group in ordered.GroupBy(x => x.Split).OrderBy(x => x.Key)) Console.WriteLine($"SPLIT {group.Key} {group.Count()}");
        Console.WriteLine($"REVIEW {Math.Min(review.Count, 5000)}");
        Console.WriteLine($"SEED {options.Seed}");
        Console.WriteLine($"OUTPUT {Path.GetFullPath(options.OutputPath)}");
    }

    public static void Audit(CliOptions options)
    {
        var manifest = ReadManifest(options.ManifestPath);
        if (Directory.Exists(options.RawPath)) VerifyRaw(manifest, options.RawPath);
        var rows = new List<TeachingRow>();
        foreach (var split in new[] { "train", "validation", "test" })
        {
            var path = Path.Combine(options.InputPath, split + ".jsonl");
            if (!File.Exists(path)) throw new FileNotFoundException($"Missing compiled split '{path}'.");
            var splitRows = ReadJsonl<TeachingRow>(path).ToArray();
            if (splitRows.Any(x => x.Split != split)) throw new InvalidDataException($"Incorrect split metadata in {path}.");
            rows.AddRange(splitRows);
        }

        var expectedSplits = SplitCounts(rows.Count);
        foreach (var (name, expected) in expectedSplits)
            if (rows.Count(x => x.Split == name) != expected) throw new InvalidDataException($"Split {name} does not contain {expected} records.");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var groupSplits = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            Validate(row);
            var key = JsonSerializer.Serialize(row.State, Json) + "\n" + row.Input;
            if (!keys.Add(key)) throw new InvalidDataException($"Duplicate (state,input): {row.Input}");
            var group = row.Source + ":" + row.GroupId;
            if (groupSplits.TryGetValue(group, out var prior) && prior != row.Split)
                throw new InvalidDataException($"Group leakage for {group}.");
            groupSplits[group] = row.Split;
        }

        Console.WriteLine($"AUDIT OK {rows.Count} RECORDS");
        Console.WriteLine("SUPERVISION SYNTHETIC INTENT,AFFECT,EXPECTED,LANGUAGE");
        Console.WriteLine("SUPERVISION OASST1 INTENT,AFFECT,EXPECTED,LANGUAGE");
        Console.WriteLine("SUPERVISION CLINC150 INTENT");
        Console.WriteLine("SUPERVISION GOEMOTIONS AFFECT");
        ReportGroups(rows, "SPLIT", row => row.Split);
        ReportGroups(rows, "SOURCE", row => row.Source);
        ReportGroups(rows, "INTENT", row => row.Perception.Intent.ToString().ToUpperInvariant());
        ReportGroups(rows, "AFFECT", row => row.Perception.Affect.ToString().ToUpperInvariant());
        ReportGroups(rows, "EXPECTED", row => row.Perception.ResponseExpected ? "TRUE" : "FALSE");
        ReportGroups(rows, "TURN_FORM", TurnForm);
        ReportGroups(rows.Where(row => !string.IsNullOrWhiteSpace(row.Family)), "FAMILY", row => row.Family!);
    }

    private static void ReportGroups(
        IEnumerable<TeachingRow> rows, string dimension, Func<TeachingRow, string> key)
    {
        foreach (var group in rows.GroupBy(key, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var example = group.OrderBy(row => row.Input, StringComparer.Ordinal).First().Input;
            Console.WriteLine($"{dimension} {group.Key} COUNT {group.Count()} EXAMPLE {JsonSerializer.Serialize(example)}");
        }
    }

    private static string TurnForm(TeachingRow row)
    {
        return row.Family?.EndsWith("_HISTORY", StringComparison.Ordinal) == true
            ? "HISTORY"
            : "DIRECT";
    }

    internal static IReadOnlyList<TeachingRow> BuildSynthetic(int count, int seed)
    {
        var splits = SplitCounts(count);
        var result = new List<TeachingRow>(count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var global = 0;
        foreach (var (split, splitCount) in splits)
        {
            for (var local = 0; local < splitCount; local++, global++)
            {
                var groupNumber = global / 2;
                var variant = local % 2;
                var intent = Templates.SyntheticIntents[(groupNumber + seed) % Templates.SyntheticIntents.Length];
                var inputText = Address(Templates.InputFor(intent, groupNumber + variant), groupNumber * 2 + variant);
                var affect = (UserAffect)((groupNumber * 2 + variant + seed) % 5);
                inputText = ExpressAffect(inputText, affect);
                var expected = !(intent == DialogueIntent.Activity && (groupNumber + variant) % 5 == 0);
                var perception = new TurnPerception(intent, affect, expected);
                var stateIndex = global + seed;
                var state = StateFor(stateIndex);
                var currentInput = "PLAYER " + inputText;
                TurnPerception? priorPerception = null;

                if ((groupNumber + variant) % 3 == 0)
                {
                    var priorIntent = Templates.SyntheticIntents[(groupNumber + 5) % Templates.SyntheticIntents.Length];
                    var priorText = Address(Templates.InputFor(priorIntent, groupNumber + 1), groupNumber + 11);
                    priorPerception = new TurnPerception(priorIntent, (UserAffect)((groupNumber + 1) % 5), true);
                    var priorDecision = new TurnDecision(Cognition.ActionFor(priorPerception));
                    var priorState = Cognition.Apply(state, priorPerception, priorDecision).State;
                    var priorResponse = Templates.ResponseFor(priorIntent, groupNumber);
                    currentInput = $"PLAYER {priorText} NPC {priorResponse} PLAYER {inputText}";
                    state = priorState;
                }

                var collisionAttempts = 0;
                while (!used.Add(StateInputKey(state, currentInput)))
                {
                    if (++collisionAttempts > 1000) throw new InvalidDataException($"Could not make a unique synthetic row for {currentInput}.");
                    stateIndex += Math.Max(1, count);
                    var candidate = StateFor(stateIndex);
                    state = priorPerception is null
                        ? candidate
                        : Cognition.Apply(candidate, priorPerception, new TurnDecision(Cognition.ActionFor(priorPerception))).State;
                }

                var response = expected ? Templates.ResponseFor(intent, groupNumber + variant) : "";
                var id = $"contrast-{groupNumber:D5}";
                var family = $"{intent.ToString().ToUpperInvariant()}_{(priorPerception is null ? "DIRECT" : "HISTORY")}";
                result.Add(Make(currentInput, state, perception, response, "SYNTHETIC", split, id, family));
            }
        }

        AddGolden(result, seed);
        EnsureUnique(result);
        return result;
    }

    private static void AddGolden(List<TeachingRow> rows, int seed)
    {
        var goldens = new (string Input, DialogueIntent Intent, UserAffect Affect, bool Expected)[]
        {
            ("PLAYER HELLO, HOW ARE YOU?", DialogueIntent.Wellbeing, UserAffect.Friendly, true),
            ("PLAYER THAT IS NOT WHAT I ASKED.", DialogueIntent.Clarification, UserAffect.Frustrated, true),
            ("PLAYER WHAT?", DialogueIntent.Clarification, UserAffect.Neutral, true),
            ("PLAYER THANK YOU, IDIOT.", DialogueIntent.Gratitude, UserAffect.Hostile, true),
            ("PLAYER I WAS NOT THANKING YOU.", DialogueIntent.Clarification, UserAffect.Frustrated, true),
            ("PLAYER I AM JUST LOOKING AROUND.", DialogueIntent.Activity, UserAffect.Neutral, false),
        };
        for (var index = 0; index < Math.Min(goldens.Length, rows.Count); index++)
        {
            var old = rows[index];
            var golden = goldens[index];
            var perception = new TurnPerception(golden.Intent, golden.Affect, golden.Expected);
            rows[index] = Make(golden.Input, StateFor(seed + index), perception,
                golden.Expected ? Templates.ResponseFor(golden.Intent, index) : "", "SYNTHETIC", old.Split, old.GroupId, "GOLDEN");
        }
    }

    private static IEnumerable<Candidate> LoadOasst(string path, List<object> review)
    {
        var messages = new Dictionary<string, (string Text, string Tree)>(StringComparer.Ordinal);
        var accepted = 0;
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Utf8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var id = String(root, "message_id");
            var role = String(root, "role");
            var text = String(root, "text");
            var tree = String(root, "message_tree_id");
            var language = String(root, "lang");
            if (id is null || text is null || tree is null || language != "en") continue;
            if (role == "prompter")
            {
                messages[id] = (text, tree);
                continue;
            }
            if (role != "assistant") continue;
            var parent = String(root, "parent_id");
            if (parent is null || !messages.TryGetValue(parent, out var prompt) || prompt.Tree != tree) continue;
            if (root.TryGetProperty("rank", out var rank) && rank.ValueKind == JsonValueKind.Number && rank.GetInt32() != 0) continue;
            if (!TryExternal(prompt.Text, out var input) || !TryExternal(text, out var response) || !IsShortSocial(input, response)) continue;
            var perception = Templates.Annotate(input, importedConversation: true);
            if (perception.Intent == DialogueIntent.Unknown)
            {
                review.Add(new { source = "OASST1", groupId = tree, reason = "AMBIGUOUS_INTENT", input });
                continue;
            }
            var baseIndex = accepted++ * 6;
            for (var variant = 0; variant < 6; variant++)
                yield return new Candidate("PLAYER " + input, StateFor(baseIndex + variant), perception, response, "OASST1", tree);
        }
    }

    private static IEnumerable<Candidate> LoadClinc(string path, List<object> review)
    {
        var mapping = new Dictionary<string, DialogueIntent>(StringComparer.Ordinal)
        {
            ["greeting"] = DialogueIntent.Greeting, ["goodbye"] = DialogueIntent.Farewell,
            ["thank_you"] = DialogueIntent.Gratitude, ["are_you_a_bot"] = DialogueIntent.Identity,
            ["what_can_i_ask_you"] = DialogueIntent.Assistance, ["how_old_are_you"] = DialogueIntent.Identity,
            ["where_are_you_from"] = DialogueIntent.Identity, ["who_do_you_work_for"] = DialogueIntent.Identity,
            ["user_name"] = DialogueIntent.Identity
        };
        using var document = JsonDocument.Parse(File.ReadAllText(path, Utf8));
        var index = 0;
        foreach (var split in new[] { "train", "val", "test" })
        foreach (var item in document.RootElement.GetProperty(split).EnumerateArray())
        {
            var text = item[0].GetString();
            var label = item[1].GetString();
            if (text is null || label is null || !mapping.TryGetValue(label, out var intent) || !TryExternal(text, out var normalized)) continue;
            var affect = Templates.Annotate(normalized, true).Affect;
            var perception = new TurnPerception(intent, affect, true);
            yield return new Candidate("PLAYER " + normalized, StateFor(index), perception, null, "CLINC150",
                $"{split}-{index++:D6}", "CLINC150_" + label.ToUpperInvariant());
        }
    }

    private static IEnumerable<Candidate> LoadGoEmotions(string rawPath, List<object> review)
    {
        var index = 0;
        foreach (var name in new[] { "go-train.tsv", "go-dev.tsv", "go-test.tsv" })
        foreach (var line in File.ReadLines(Path.Combine(rawPath, name), Utf8))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3 || !TryExternal(parts[0], out var normalized)) continue;
            var labels = parts[1].Split(',').Select(int.Parse).ToArray();
            var affect = GoAffect(labels);
            var annotated = Templates.Annotate(normalized);
            var expected = normalized.EndsWith('?') || annotated.Intent is DialogueIntent.Greeting or DialogueIntent.Farewell or DialogueIntent.Clarification;
            var perception = new TurnPerception(annotated.Intent, affect, expected);
            var id = parts[2].Length == 0 ? $"go-{index:D7}" : parts[2];
            yield return new Candidate("PLAYER " + normalized, StateFor(index++), perception, null, "GOEMOTIONS", id,
                "GOEMOTIONS_" + affect.ToString().ToUpperInvariant());
        }
    }

    private static UserAffect GoAffect(int[] labels)
    {
        if (labels.Contains(2)) return UserAffect.Hostile;
        if (labels.Any(x => x is 3 or 6 or 10 or 11)) return UserAffect.Frustrated;
        if (labels.Any(x => x is 9 or 12 or 14 or 16 or 19 or 24 or 25)) return UserAffect.Distressed;
        if (labels.Any(x => x is 0 or 1 or 4 or 5 or 13 or 15 or 17 or 18 or 20 or 21 or 23)) return UserAffect.Friendly;
        return UserAffect.Neutral;
    }

    private static void AddExternal(List<TeachingRow> rows, IEnumerable<Candidate> source, int quota, int seed)
    {
        var candidates = source.GroupBy(x => x.GroupId + "\u001f" + x.Input + "\u001f" + StateInputKey(x.State, ""), StringComparer.Ordinal).Select(x => x.First()).ToArray();
        var sourceName = candidates.FirstOrDefault()?.Source ?? "Source";
        if (candidates.Length < quota) throw new InvalidDataException($"{sourceName} supplied {candidates.Length} of {quota} records.");
        var groups = candidates.GroupBy(x => x.GroupId, StringComparer.Ordinal)
            .OrderBy(x => StableKey(seed, sourceName, x.Key), StringComparer.Ordinal).ToArray();
        var splits = SplitCounts(quota);
        var groupOffset = 0;
        foreach (var (split, count) in splits)
        {
            var remaining = count;
            while (remaining > 0 && groupOffset < groups.Length)
            {
                var group = groups[groupOffset++].OrderBy(x => StableKey(seed, x.GroupId, x.Input), StringComparer.Ordinal).ToArray();
                foreach (var candidate in group.Take(remaining))
                    rows.Add(Make(candidate.Input, candidate.State, candidate.Perception, candidate.Response, candidate.Source,
                        split, candidate.GroupId, candidate.Family));
                remaining -= Math.Min(remaining, group.Length);
            }
            if (remaining != 0) throw new InvalidDataException($"{sourceName} could not fill the {split} split; {remaining} records short.");
        }
    }

    private static TeachingRow Make(string input, NpcState state, TurnPerception perception, string? response, string source, string split, string groupId, string? family = null)
        => new(DialogueText.Normalize(input), state, perception, Cognition.ActionFor(perception), response is null ? null : DialogueText.Normalize(response), source, split, groupId, family);

    private static NpcState StateFor(int index)
    {
        var value = Math.Abs(index);
        var rapport = (byte)(value % 4); value /= 4;
        var mood = (NpcMood)(value % 4); value /= 4;
        var intent = (DialogueIntent)(value % 15); value /= 15;
        var affect = (UserAffect)(value % 5); value /= 5;
        var topic = (DialogueTopic)(value % 7); value /= 7;
        var goal = (NpcGoal)(value % 7);
        return new NpcState(rapport, mood, intent, affect, topic, goal);
    }

    private static bool TryExternal(string raw, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 1000) return false;
        var lower = raw.ToLowerInvariant();
        if (lower.Contains("http") || lower.Contains("www.") || lower.Contains("```", StringComparison.Ordinal) ||
            lower.Contains("<html") || lower.Contains("[name]") || lower.Contains("[removed]") ||
            ContainsUnsafe(lower)) return false;
        try { normalized = DialogueText.Normalize(raw); }
        catch { return false; }
        return normalized.Length is > 1 and <= 249 && char.IsLetterOrDigit(normalized[0]) &&
               normalized != "PLAYER" && normalized != "NPC";
    }

    private static bool ContainsUnsafe(string text)
    {
        string[] unsafeFragments =
        [
            "kill yourself", "suicide", "porn", "erotic", "sexual", "nude", "rape", "murder",
            "pipe bomb", "weapon", "cocaine", "heroin", "meth", "nazi", "racial slur",
            "fuck", "shit", "bitch", "retard"
        ];
        return unsafeFragments.Any(text.Contains);
    }

    private static bool IsShortSocial(string input, string response)
    {
        if (response.Length > 256 || input.Length > 256) return false;
        var forbidden = new[] { "PROGRAM", "CODE", "PYTHON", "JAVASCRIPT", "MEDICAL", "LAWYER", "LEGAL", "DIAGNOS", "PRESIDENT", "LATEST", "CURRENT NEWS", "EQUATION", "CALCULATE" };
        if (forbidden.Any(x => input.Contains(x) || response.Contains(x))) return false;
        var annotation = Templates.Annotate(input, true);
        if (annotation.Intent == DialogueIntent.Assistance)
        {
            string[] social = ["HELP ME", "TALK", "CHAT", "FRIEND", "FEEL", "LONELY", "ADVICE", "LISTEN", "RESPOND", "WHAT CAN YOU DO"];
            if (!social.Any(input.Contains)) return false;
        }
        return true;
    }

    private static void Validate(TeachingRow row)
    {
        if (DialogueText.Normalize(row.Input) != row.Input || !row.Input.StartsWith("PLAYER ", StringComparison.Ordinal))
            throw new InvalidDataException($"Noncanonical input in {row.GroupId}.");
        row.State.Validate();
        if (Cognition.ActionFor(row.Perception) != row.Action) throw new InvalidDataException($"Wrong action in {row.GroupId}.");
        if (row.Response is not null && DialogueText.Normalize(row.Response) != row.Response) throw new InvalidDataException($"Noncanonical response in {row.GroupId}.");
        if (row.Action == ResponseAction.NoResponse && row.Response is not null && row.Response != "") throw new InvalidDataException($"No-response row {row.GroupId} must have an empty response.");
        if (row.Response?.Length > 256) throw new InvalidDataException($"Overlength response in {row.GroupId}.");
        if (row.Input.Length > 256) throw new InvalidDataException($"Overlength input in {row.GroupId}.");
        if (string.IsNullOrWhiteSpace(row.Source) || string.IsNullOrWhiteSpace(row.Split) || string.IsNullOrWhiteSpace(row.GroupId))
            throw new InvalidDataException("Missing provenance metadata.");
    }

    private static Dictionary<string, int> ScaleQuotas(int count, SourceManifest manifest)
    {
        var definitions = new List<(string Name, int Weight)> { ("SYNTHETIC", 6000) };
        definitions.AddRange(manifest.Sources.Select(x => (x.Name, x.Quota)));
        var result = definitions.ToDictionary(x => x.Name, x => count * x.Weight / 10_000, StringComparer.Ordinal);
        var remaining = count - result.Values.Sum();
        foreach (var item in definitions.OrderByDescending(x => (long)count * x.Weight % 10_000).ThenBy(x => x.Name).Take(remaining)) result[item.Name]++;
        return result;
    }

    private static IReadOnlyList<KeyValuePair<string, int>> SplitCounts(int count)
    {
        var train = count * 8 / 10;
        var validation = count / 10;
        return new[]
        {
            new KeyValuePair<string, int>("train", train),
            new KeyValuePair<string, int>("validation", validation),
            new KeyValuePair<string, int>("test", count - train - validation)
        };
    }

    private static void EnsureUnique(IEnumerable<TeachingRow> rows)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = JsonSerializer.Serialize(row.State, Json) + "\n" + row.Input;
            if (!keys.Add(key)) throw new InvalidDataException($"Synthetic duplicate: {row.Input}");
        }
    }

    private static string StateInputKey(NpcState state, string input) =>
        $"{state.Rapport}|{(int)state.Mood}|{(int)state.LastIntent}|{(int)state.LastAffect}|{(int)state.ActiveTopic}|{(int)state.ActiveGoal}|{input}";

    private static string Address(string text, int index)
    {
        string[] addresses =
        [
            "FRIEND", "TRAVELER", "STRANGER", "WARRIOR", "MAGE", "RANGER", "HERO", "VISITOR",
            "NEIGHBOR", "MERCHANT", "GUARD", "SAILOR", "HUNTER", "HEALER", "SMITH", "FARMER",
            "SCHOLAR", "PILGRIM", "CAPTAIN", "BARD", "RIDER", "SCOUT", "KEEPER", "WANDERER",
            "COMPANION", "ALLY", "GUEST", "ADVENTURER", "CITIZEN", "ELDER", "NOVICE", "MASTER"
        ];
        var suffix = text[^1] is '.' or '?' or '!' ? text[^1].ToString() : ".";
        var stem = text[^1] is '.' or '?' or '!' ? text[..^1] : text;
        return $"{stem}, {addresses[Math.Abs(index) % addresses.Length]}{suffix}";
    }

    private static string ExpressAffect(string text, UserAffect affect) => affect switch
    {
        UserAffect.Friendly => "MY FRIEND, " + text,
        UserAffect.Distressed => "I AM WORRIED. " + text,
        UserAffect.Frustrated => "I AM FRUSTRATED. " + text,
        UserAffect.Hostile => text.TrimEnd('.', '?', '!') + ", IDIOT!",
        _ => text
    };

    private static SourceManifest ReadManifest(string path)
    {
        var manifest = JsonSerializer.Deserialize<SourceManifest>(File.ReadAllText(path, Utf8), Json)
            ?? throw new InvalidDataException("Invalid source manifest.");
        if (manifest.Version != 1 || manifest.Sources.Length == 0) throw new InvalidDataException("Unsupported source manifest.");
        foreach (var source in manifest.Sources)
        {
            if (source.Quota <= 0 || source.Files.Length == 0 || string.IsNullOrWhiteSpace(source.License) || string.IsNullOrWhiteSpace(source.Attribution) || source.Revision.Length < 7)
                throw new InvalidDataException($"Incomplete source metadata for {source.Name}.");
            foreach (var file in source.Files)
                if (file.Sha256.Length != 64 || !Uri.TryCreate(file.Url, UriKind.Absolute, out _)) throw new InvalidDataException($"Invalid file metadata for {source.Name}.");
        }
        return manifest;
    }

    private static void VerifyRaw(SourceManifest manifest, string rawPath)
    {
        foreach (var source in manifest.Sources)
        foreach (var file in source.Files)
        {
            var path = Path.Combine(rawPath, file.Path);
            if (!File.Exists(path)) throw new FileNotFoundException($"Missing {path}; run fetch first.");
            var actual = Hash(path);
            if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"SHA-256 mismatch for {path}.");
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string StableKey(int seed, params string[] values)
    {
        var bytes = SHA256.HashData(Utf8.GetBytes(seed + "\u001f" + string.Join("\u001f", values)));
        return Convert.ToHexString(bytes);
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void AtomicJsonl<T>(string path, IEnumerable<T> rows)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var writer = new StreamWriter(temporary, false, Utf8))
                foreach (var row in rows) writer.WriteLine(JsonSerializer.Serialize(row, Json));
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static IEnumerable<T> ReadJsonl<T>(string path)
    {
        foreach (var line in File.ReadLines(path, Utf8))
            if (!string.IsNullOrWhiteSpace(line)) yield return JsonSerializer.Deserialize<T>(line, Json) ?? throw new InvalidDataException($"Invalid row in {path}.");
    }
}

internal static class SelfTests
{
    public static void Run()
    {
        Golden("HELLO, HOW ARE YOU?", DialogueIntent.Wellbeing, UserAffect.Friendly, true);
        Golden("THAT IS NOT WHAT I ASKED.", DialogueIntent.Clarification, UserAffect.Frustrated, true);
        Golden("WHAT?", DialogueIntent.Clarification, UserAffect.Neutral, true);
        Golden("THANK YOU, IDIOT.", DialogueIntent.Gratitude, UserAffect.Hostile, true);
        Golden("I WAS NOT THANKING YOU.", DialogueIntent.Clarification, UserAffect.Frustrated, true);
        Golden("I AM JUST LOOKING AROUND.", DialogueIntent.Activity, UserAffect.Neutral, false);

        var one = CorpusPipeline.BuildSynthetic(300, 42);
        var two = CorpusPipeline.BuildSynthetic(300, 42);
        Assert(one.Count == 300 && one.Count(x => x.Split == "train") == 240 && one.Count(x => x.Split == "validation") == 30 && one.Count(x => x.Split == "test") == 30, "synthetic split");
        Assert(JsonSerializer.Serialize(one) == JsonSerializer.Serialize(two), "deterministic synthetic data");
        Assert(one.Any(x => x.Response == "" && x.Action == ResponseAction.NoResponse), "no-response form");
        Assert(one.All(x => x.Input.Length <= 256 && x.Response?.Length <= 256), "length limits");
        Assert(one.Select(x => x.Source + x.GroupId + x.Split).Distinct().Count() >= 150, "contrast groups");
        Console.WriteLine("ALL DATA GENERATOR SELF TESTS PASSED");
    }

    private static void Golden(string input, DialogueIntent intent, UserAffect affect, bool expected)
    {
        var actual = Templates.Annotate(input);
        Assert(actual == new TurnPerception(intent, affect, expected), $"golden annotation {input}: got {actual}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("SELFTEST " + message);
    }
}

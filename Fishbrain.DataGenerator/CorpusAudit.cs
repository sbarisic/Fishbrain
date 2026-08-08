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
    private static void EnsureUniqueAndConsistent(IEnumerable<CorpusRow> rows)
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

    private static void AuditProjectDiversity(IReadOnlyList<CorpusRow> rows)
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

    private static void AssignSplits(List<CorpusRow> rows, int seed)
    {
        var parents = Enumerable.Range(0, rows.Count).ToArray();
        int Find(int value)
        {
            while (parents[value] != value)
            {
                parents[value] = parents[parents[value]];
                value = parents[value];
            }
            return value;
        }
        void Union(int left, int right)
        {
            left = Find(left);
            right = Find(right);
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
            if (map.TryGetValue(key, out var other))
            {
                Union(index, other);
            }
            else
            {
                map[key] = index;
            }
        }
    }

    private static void AuditLeakage(IReadOnlyList<CorpusRow> rows)
    {
        Check(row => row.SemanticFamilyId, "semantic family");
        Check(row => row.Source + ":" + row.GroupId, "source conversation");
        Check(row => NormalizeKey(row.Input), "normalized input");
        var signatures = new Dictionary<string, CorpusRow>(StringComparer.Ordinal);
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
        void Check(Func<CorpusRow, string> key, string name)
        {
            foreach (var group in rows.GroupBy(key, StringComparer.Ordinal))
                if (group.Select(row => row.Split).Distinct(StringComparer.Ordinal).Skip(1).Any())
                    throw new InvalidDataException($"{name} leakage for {group.Key}.");
        }
    }

    private static void AuditBenchmark(IReadOnlyList<CorpusRow> rows, string compiledPath)
    {
        var benchmark = Path.GetFullPath(Path.Combine(compiledPath, "..", "benchmarks", "benchmark-256.jsonl"));
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

    private static void Validate(CorpusRow row)
    {
        if (row.Input != DialogueText.Normalize(row.Input) || !row.Input.StartsWith("PLAYER ", StringComparison.Ordinal))
            throw new InvalidDataException($"Noncanonical input in {row.GroupId}.");
        if (row.Input.Length > 1024 || row.Response?.Length > 256 ||
            row.Response is not null && row.Response != DialogueText.Normalize(row.Response))
            throw new InvalidDataException($"Invalid response length or normalization in {row.GroupId}.");
        row.State.Validate();
        if (Cognition.ActionFor(row.Perception) != row.Action) throw new InvalidDataException($"Invalid action in {row.GroupId}.");
        if (string.IsNullOrWhiteSpace(row.SemanticFamilyId) || string.IsNullOrWhiteSpace(row.GroupId) ||
            string.IsNullOrWhiteSpace(row.SourceRevision) || row.SourceChecksum is null || row.SourceChecksum.Length != 64 ||
            row.SourceChecksum.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"Missing provenance in {row.GroupId}.");
        if (!CommercialLicenses.Contains(row.SourceLicense)) throw new InvalidDataException($"Noncommercial source {row.Source}.");
        if (row.Turns is null || row.Turns.Length == 0 || row.Turns[^1].Role != DialogueRole.Player ||
            row.InitialDialogueState is null || row.Persona is null || string.IsNullOrWhiteSpace(row.SourceUrl) ||
            string.IsNullOrWhiteSpace(row.Attribution) ||
            row.StructuredPerception is null || row.SupervisedHeads is null)
            throw new InvalidDataException($"Missing contextual schema fields in {row.GroupId}.");
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
            ResponseCatalog.Find(perception.ResponseCandidateId) is null)
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
        if (manifest.Sources is null || manifest.Sources.Length == 0 ||
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

    private static void Report(IEnumerable<CorpusRow> rows)
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
        Convert.ToHexString(SHA256.HashData(Utf8.GetBytes("FISHBRAIN-" + source))).ToLowerInvariant();
    private static string StableKey(int seed, string value) =>
        Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(seed + "|" + value)));
    private static int StableNumber(string value) =>
        BitConverter.ToInt32(SHA256.HashData(Utf8.GetBytes(value)), 0) & int.MaxValue;

    internal static NpcState StateFor(int index)
    {
        ulong value = unchecked((uint)index);
        var rapport = (byte)(value % 4);
        value /= 4;
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
}

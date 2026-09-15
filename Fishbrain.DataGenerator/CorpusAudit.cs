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

        AuditDiscourseCorpus(rows);

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
            if (IsDiscourseSource(rows[index].Source))
            {
                continue;
            }
            foreach (var signature in NearSignatures(input))
            {
                if (nearSignatures.TryGetValue(signature, out var other)) Union(index, other);
                else nearSignatures.TryAdd(signature, index);
            }
        }
        var components = Enumerable.Range(0, rows.Count).GroupBy(Find).Select(group => group.ToArray()).ToArray();
        var names = new[] { "train", "validation", "test" };
        foreach (var stratum in components.GroupBy(Stratum, StringComparer.Ordinal).OrderBy(group => group.Key))
        {
            var ordered = stratum.OrderBy(component => StableKey(seed, rows[component[0]].SemanticFamilyId)).ToArray();
            for (var componentIndex = 0; componentIndex < ordered.Length; componentIndex++)
            {
                var position = componentIndex % 10;
                var split = position < 8 ? 0 : position == 8 ? 1 : 2;
                var component = ordered[componentIndex];
                foreach (var index in component) rows[index] = rows[index] with { Split = names[split] };
            }
        }

        string Stratum(int[] component)
        {
            var row = rows[component[0]];
            var discourse = row.StructuredPerception.Discourse;
            var antecedentType = discourse?.AntecedentUtterance is null ? "NONE" :
                row.Turns is { Length: > 2 } && discourse.AntecedentUtterance != row.Turns[^2].Sequence
                    ? "OLDER"
                    : "IMMEDIATE";
            var ambiguity = discourse?.Evidence.Contains("NONE_REFERENCE", StringComparison.Ordinal) == true
                ? "AMBIGUOUS_OR_EVICTED"
                : "RESOLVED";
            return $"{row.Source}|{discourse?.Act.ToString() ?? "NONE"}|" +
                   $"{discourse?.FactKind?.ToString() ?? "NONE"}|{discourse?.Negated}|" +
                   $"{antecedentType}|{ambiguity}";
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
                if (signatures.TryGetValue(signature, out var other) && row.Split != other.Split &&
                    Near(row.Input, other.Input) &&
                    !(IsDiscourseSource(row.Source) && IsDiscourseSource(other.Source) &&
                      row.SemanticFamilyId != other.SemanticFamilyId))
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

    private static void AuditBenchmark(IReadOnlyList<CorpusRow> rows, string manifestPath)
    {
        var corpusInputs = rows.Select(row => NormalizeKey(row.Input)).ToHashSet(StringComparer.Ordinal);
        var families = rows.Select(row => row.SemanticFamilyId).ToHashSet(StringComparer.Ordinal);
        var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ??
            throw new InvalidDataException("The source manifest path has no parent directory.");
        var benchmarkDirectory = Path.Combine(manifestDirectory, "benchmarks");
        foreach (var name in new[] { "benchmark-256.jsonl", "conversation-scenarios.jsonl" })
        {
            var benchmark = Path.Combine(benchmarkDirectory, name);
            if (!File.Exists(benchmark))
            {
                throw new FileNotFoundException($"Mandatory held-out benchmark '{name}' was not found.", benchmark);
            }

            foreach (var line in File.ReadLines(benchmark, Utf8))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var text = root.TryGetProperty("text", out var benchmarkText)
                    ? benchmarkText.GetString()!
                    : root.GetProperty("input").GetString()!;
                var family = root.TryGetProperty("semanticFamilyId", out var semanticFamily)
                    ? semanticFamily.GetString()!
                    : $"{name}:{root.GetProperty("sessionId").GetString()}";
                if (!TryNormalizeExternal(text, out var normalized))
                {
                    throw new InvalidDataException($"Noncanonical benchmark text in family {family}.");
                }

                if (corpusInputs.Contains(NormalizeKey("PLAYER " + normalized)) || families.Contains(family))
                {
                    throw new InvalidDataException($"Benchmark contamination in family {family}.");
                }

                if (name == "conversation-scenarios.jsonl")
                {
                    var benchmarkInput = "PLAYER " + normalized;
                    var near = rows.FirstOrDefault(row => NearConversationBenchmark(row.Input, benchmarkInput));
                    if (near is not null)
                    {
                        throw new InvalidDataException(
                            $"Conversation benchmark near-contamination in {family}: {near.GroupId}.");
                    }
                }
            }
        }
    }

    private static bool IsDiscourseSource(string source) =>
        source.StartsWith("PROJECT_CONTEXTUAL", StringComparison.Ordinal) ||
        source.StartsWith("PROJECT_DISCOURSE", StringComparison.Ordinal) ||
        source == "PROJECT_CONVERSATION";

    private static void AuditDiscourseCorpus(IReadOnlyList<CorpusRow> rows)
    {
        var requirements = new Dictionary<string, (int Rows, int Families, int MaximumExpansion)>(StringComparer.Ordinal)
        {
            ["PROJECT_DISCOURSE_FACTS"] = (8_000, 2_000, 4),
            ["PROJECT_DISCOURSE_REFERENCES"] = (6_000, 1_500, 4),
            ["PROJECT_CONVERSATION"] = (4_000, 1_000, 4),
            ["PROJECT_DISCOURSE_NEGATIVES"] = (2_000, 1_000, 2)
            ,
            ["PROJECT_CONTEXTUAL_ACTIONS"] = (5_000, 1_250, 4)
            ,
            ["PROJECT_CONTEXTUAL_MEMORY"] = (5_000, 1_250, 4)
            ,
            ["PROJECT_CONTEXTUAL_COMPOUND"] = (5_000, 1_250, 4)
            ,
            ["PROJECT_CONTEXTUAL_AGENDA"] = (5_000, 1_250, 4)
        };
        foreach (var requirement in requirements)
        {
            var band = rows.Where(row => row.Source == requirement.Key).ToArray();
            var families = band.GroupBy(row => row.SemanticFamilyId, StringComparer.Ordinal).ToArray();
            if (band.Length != requirement.Value.Rows || families.Length < requirement.Value.Families)
            {
                throw new InvalidDataException(
                    $"{requirement.Key} requires {requirement.Value.Rows} rows and at least " +
                    $"{requirement.Value.Families} semantic families.");
            }

            if (families.Any(family => family.Count() > requirement.Value.MaximumExpansion))
            {
                throw new InvalidDataException(
                    $"{requirement.Key} exceeds its maximum semantic-family expansion.");
            }

            if (band.All(row => row.Split is "train" or "validation" or "test"))
            {
                foreach (var split in new[] { "train", "validation", "test" })
                {
                    var splitFamilies = band.Where(row => row.Split == split)
                        .Select(row => row.SemanticFamilyId)
                        .Distinct(StringComparer.Ordinal)
                        .Count();
                    var minimum = split == "train"
                        ? requirement.Value.Families * 7 / 10
                        : requirement.Value.Families / 20;
                    if (splitFamilies < minimum)
                    {
                        throw new InvalidDataException(
                            $"{requirement.Key}/{split} contains only {splitFamilies} semantic families; " +
                            $"at least {minimum} are required.");
                    }
                }
            }
        }

        var serialPattern = new System.Text.RegularExpressions.Regex(
            @"\b(?:CASE|MEM|REF|CHAT|NEG)[0-9A-F]{4,}\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (rows.Any(row => serialPattern.IsMatch(row.Input) ||
                            row.Turns?.Any(turn => serialPattern.IsMatch(turn.Text)) == true))
        {
            throw new InvalidDataException("Model text contains a serial marker.");
        }

        var references = rows.Where(row => row.Source == "PROJECT_DISCOURSE_REFERENCES").ToArray();
        var positivePointers = references.Count(row => row.StructuredPerception.Discourse?.AntecedentUtterance is not null);
        if (positivePointers * 2 != references.Length)
        {
            throw new InvalidDataException("Reference rows must balance retained-utterance and NONE pointer targets.");
        }
    }

    private static bool NearConversationBenchmark(string corpusInput, string benchmarkInput)
    {
        var corpusWords = NormalizeKey(corpusInput).Split(' ').ToHashSet(StringComparer.Ordinal);
        var benchmarkWords = NormalizeKey(benchmarkInput).Split(' ').ToHashSet(StringComparer.Ordinal);
        if (benchmarkWords.Count <= 4)
        {
            return corpusWords.SetEquals(benchmarkWords);
        }

        return (double)corpusWords.Intersect(benchmarkWords).Count() /
               Math.Max(1, corpusWords.Union(benchmarkWords).Count()) >= 0.70;
    }

    private static void Validate(CorpusRow row)
    {
        row.Contextual?.Validate(row.Turns?[^1].Text ?? row.Input);
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
        if (row.Turns is null || row.Turns.Length == 0 || row.Turns[^1].Speaker != DialogueRole.Player ||
            row.InitialDialogueState is null || row.Persona is null || string.IsNullOrWhiteSpace(row.SourceUrl) ||
            string.IsNullOrWhiteSpace(row.Attribution) ||
            row.StructuredPerception is null || row.SupervisedHeads is null)
            throw new InvalidDataException($"Missing contextual schema fields in {row.GroupId}.");
        row.InitialDialogueState.Validate();
        row.Persona.Validate();
        ValidateDiscourse(row.StructuredPerception.Discourse, row.GroupId);
        var contextualInput = ContextInput(row.Turns);
        if (row.Turns.Any(turn => turn is null || !Enum.IsDefined(turn.Speaker) || string.IsNullOrWhiteSpace(turn.Text) ||
            turn.Text != DialogueText.Normalize(turn.Text)) || contextualInput != row.Input)
            throw new InvalidDataException($"Structured turns disagree with input in {row.Source}/{row.GroupId}: " +
                $"expected '{row.Input}', reconstructed '{contextualInput}'.");
        if (row.Turns.Zip(row.Turns.Skip(1)).Any(pair => pair.First.Sequence >= pair.Second.Sequence))
            throw new InvalidDataException($"Utterance sequences are not strictly increasing in {row.GroupId}.");
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
        if (perception.Discourse is { } discourse)
        {
            if (!Enum.IsDefined(discourse.Act) || !Enum.IsDefined(discourse.Subject) ||
                !Enum.IsDefined(discourse.Target) || discourse.FactKind is { } kind && !Enum.IsDefined(kind) ||
                discourse.Confidence is < 0 or > 1 || !double.IsFinite(discourse.Confidence) ||
                discourse.AntecedentUtterance is { } antecedent &&
                row.Turns.All(turn => turn.Sequence != antecedent) ||
                discourse.FactValueSpan is { } value &&
                (value.Length is < 1 or > 128 ||
                 value.NormalizedValue != DialogueText.Normalize(value.NormalizedValue)))
                throw new InvalidDataException($"Invalid discourse frame in {row.GroupId}.");
            discourse.FactValueSpan?.Validate(Brain.ExtractCurrentPlayerTurn(row.Input));
        }
        if (row.Source.StartsWith("PROJECT_DISCOURSE_", StringComparison.Ordinal) ||
            row.Source == "PROJECT_CONVERSATION")
        {
            if (perception.Discourse is null || row.FactDelta is null || row.InitialPlayerProfile is null ||
                row.DiscourseResponseAction is null || row.AcceptableResponseConstraints is not { Length: > 0 } ||
                string.IsNullOrWhiteSpace(row.RejectedResponse))
                throw new InvalidDataException($"Discourse supervision is incomplete in {row.GroupId}.");
            row.InitialPlayerProfile.Validate();
            foreach (var fact in row.FactDelta)
                PlayerConversationProfile.ValidateFact(fact, DialogueFactProvenance.SessionReported);
            if (row.RejectedResponse != DialogueText.Normalize(row.RejectedResponse))
                throw new InvalidDataException($"Rejected response is not canonical in {row.GroupId}.");
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
        "discourseAct" => (perception.Discourse?.Act ?? DiscourseAct.None).ToString(),
        "discourseSubject" => (perception.Discourse?.Subject ?? DialogueParticipant.None).ToString(),
        "discourseTarget" => (perception.Discourse?.Target ?? DialogueParticipant.None).ToString(),
        "factKind" => perception.Discourse?.FactKind?.ToString() ?? "NONE",
        "factPolarity" => perception.Discourse?.Negated == true ? "NEGATED" : "POSITIVE",
        "factSpan" => perception.Discourse?.FactValueSpan is { } span
            ? $"{span.Start}:{span.Length}:{span.NormalizedValue}"
            : "NONE",
        "antecedent" => perception.Discourse?.AntecedentUtterance?.ToString() ?? "NONE",
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

    private static void ValidateDiscourse(DiscourseFrame? frame, string groupId)
    {
        if (frame is null)
        {
            return;
        }

        if (!Enum.IsDefined(frame.Act) || !Enum.IsDefined(frame.Subject) || !Enum.IsDefined(frame.Target) ||
            frame.FactKind is { } kind && !Enum.IsDefined(kind) ||
            frame.Confidence is < 0 or > 1 || !double.IsFinite(frame.Confidence) ||
            frame.AntecedentUtterance is < 0 || string.IsNullOrWhiteSpace(frame.Evidence) ||
            frame.Evidence.Length > 128 || frame.Evidence.Any(character =>
                character is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not '_') ||
            frame.Act != DiscourseAct.ReferBack && (frame.FactKind is null) != (frame.FactValueSpan is null) ||
            frame.FactValueSpan is { } value &&
            (value.Length is < 1 or > 128 ||
             value.NormalizedValue != DialogueText.Normalize(value.NormalizedValue)))
        {
            throw new InvalidDataException($"Invalid discourse frame in {groupId}.");
        }
    }
}

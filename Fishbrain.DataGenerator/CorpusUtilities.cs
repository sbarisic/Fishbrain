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
    private static UserAffect GoAffect(int[] labels)
    {
        if (labels.Contains(2)) return UserAffect.Hostile;
        if (labels.Any(value => value is 3 or 6 or 10 or 11)) return UserAffect.Frustrated;
        if (labels.Any(value => value is 9 or 12 or 14 or 16 or 19 or 24 or 25)) return UserAffect.Distressed;
        if (labels.Any(value => value is 0 or 1 or 4 or 5 or 13 or 15 or 17 or 18 or 20 or 21 or 23)) return UserAffect.Friendly;
        return UserAffect.Neutral;
    }

    private static string ContextInput(IReadOnlyList<DialogueUtterance> sourceTurns)
    {
        if (sourceTurns.Count == 0 || sourceTurns[^1].Speaker != DialogueRole.Player)
            throw new InvalidDataException("A contextual corpus row must end with a player turn.");
        var turns = sourceTurns.Select(turn => new DialogueUtterance(
            turn.Sequence, turn.Speaker, DialogueText.Normalize(turn.Text))).ToList();
        while (turns.Count > 1 && turns[0].Speaker != DialogueRole.Player) turns.RemoveAt(0);
        while (turns.Count > 1 && string.Join(' ', turns.Select(Render)).Length > 1000) turns.RemoveAt(0);
        return string.Join(' ', turns.Select(Render));

        static string Render(DialogueUtterance turn) =>
            (turn.Speaker == DialogueRole.Player ? "PLAYER " : "NPC ") + DialogueText.TerminateTurn(turn.Text);
    }

    private static CorpusRow WithTurns(CorpusRow row, DialogueUtterance[] turns)
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

    private static CorpusRow EnrichRow(
        CorpusRow row, DialogueUtterance[] turns, SourceDefinition? definition)
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
            SourceUrl = definition is null ? "PROJECT://FISHBRAIN" : string.Join('|', definition.Files.Select(file => file.Url)),
            Attribution = definition?.Attribution ?? "FISHBRAIN PROJECT CONTRIBUTORS"
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
        var turns = new List<DialogueUtterance>();
        foreach (var piece in pieces)
        {
            var separator = piece.IndexOf(':');
            if (separator <= 0 || !TryNormalizeExternal(piece[(separator + 1)..], out var text)) continue;
            if (piece.StartsWith("Human:", StringComparison.Ordinal))
                turns.Add(new DialogueUtterance(turns.Count, DialogueRole.Player, text));
            else if (piece.StartsWith("Assistant:", StringComparison.Ordinal))
                turns.Add(new DialogueUtterance(turns.Count, DialogueRole.Npc, text));
        }
        if (turns.Count == 0 || turns[^1].Speaker != DialogueRole.Npc) return new([], null);
        var response = turns[^1].Text;
        turns.RemoveAt(turns.Count - 1);
        return new(turns.TakeLast(5).ToArray(), response);
    }

    private sealed record PreferenceDialogue(DialogueUtterance[] Turns, string? Response);

    private static bool TryNormalizeExternal(string? raw, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var builder = new StringBuilder(Math.Min(raw.Length, 256));
        foreach (var original in raw.Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(original) ==
                System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var value = original switch
            {
                '\u2018' or '\u2019' => '\'',
                '\u201C' or '\u201D' => '"',
                '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' => '-',
                _ => char.ToUpperInvariant(original)
            };
            if (value is >= 'A' and <= 'Z' or >= '0' and <= '9' or ' ' or '.' or ',' or '?' or '!' or '\'' or '"' or '-' or ':')
            {
                builder.Append(value);
            }
            else if (char.IsWhiteSpace(value))
            {
                builder.Append(' ');
            }
        }
        try
        {
            normalized = DialogueText.Normalize(builder.ToString());
        }
        catch (ArgumentException)
        {
            return false;
        }
        return normalized.Length is >= 2 and <= 220 && char.IsLetterOrDigit(normalized[0]);
    }

    private static string NormalizeExternal(string raw) => TryNormalizeExternal(raw, out var normalized)
        ? normalized : throw new InvalidDataException($"Could not normalize project text '{raw}'.");

    private static bool ContainsSensitive(string text)
    {
        var padded = " " + text + " ";
        return new[] { " FUCK ", " NIGGER ", " FAGGOT ", " RAPE ", " SUICIDE ", " KILL " }.Any(padded.Contains);
    }

    private static HeldOutInputs LoadHeldOutInputs(string manifestPath)
    {
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var conversation = new List<string>();
        var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ??
            throw new InvalidDataException("The source manifest path has no parent directory.");
        var benchmarkDirectory = Path.Combine(manifestDirectory, "benchmarks");
        foreach (var name in new[] { "benchmark-256.jsonl", "conversation-scenarios.jsonl" })
        {
            var path = Path.Combine(benchmarkDirectory, name);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Mandatory held-out benchmark '{name}' was not found.", path);
            }

            foreach (var line in File.ReadLines(path, Utf8))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var property = root.TryGetProperty("text", out var benchmarkText)
                    ? benchmarkText
                    : root.GetProperty("input");
                if (TryNormalizeExternal(property.GetString(), out var text))
                {
                    var input = "PLAYER " + text;
                    exact.Add(NormalizeKey(input));
                    if (name == "conversation-scenarios.jsonl")
                    {
                        conversation.Add(input);
                    }
                }
            }
        }

        return new HeldOutInputs(exact, conversation.ToArray());
    }

    private sealed record HeldOutInputs(
        HashSet<string> Exact,
        IReadOnlyList<string> Conversation);

    private sealed class CompilationContext(HeldOutInputs heldOutInputs)
    {
        public HashSet<string> ExternalInputs { get; } = new(StringComparer.Ordinal);

        public bool IsHeldOut(string text)
        {
            var input = "PLAYER " + text;
            return heldOutInputs.Exact.Contains(NormalizeKey(input)) ||
                   heldOutInputs.Conversation.Any(benchmark =>
                       NearConversationBenchmark(input, benchmark));
        }
    }

    private static readonly string[] OperationalHeads =
    ["speechActs", "domains", "goals", "affect", "stance", "policy", "slots", "content", "tool",
        "responseCandidate", "knowledgeTarget"];
    private static readonly string[] AllHeads = OperationalHeads.Concat(
        ["discourseAct", "discourseSubject", "discourseTarget", "factKind", "factPolarity", "factSpan", "antecedent"]).ToArray();
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

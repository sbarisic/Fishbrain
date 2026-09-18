using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fishbrain;

internal static class ConversationEvaluation
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) }
    };

    public static void Export(string checkpointPath, string scenarioPath, string outputPath, string? diagnosticsPath = null)
    {
        var brain = Brain.Load(checkpointPath);
        var scenarios = File.ReadLines(scenarioPath, Encoding.UTF8)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<ConversationScenario>(line, Json) ??
                throw new InvalidDataException("Invalid conversation scenario."))
            .ToArray();
        if (scenarios.Length == 0)
            throw new InvalidDataException("Conversation scenario file is empty.");

        var output = new List<ConversationReview>();
        var diagnostics = new List<object>();
        foreach (var session in scenarios.GroupBy(row => row.SessionId, StringComparer.Ordinal))
        {
            var orderedScenarios = session.OrderBy(row => row.TurnIndex).ToArray();
            if (orderedScenarios.Select(row => row.TurnIndex).Distinct().Count() != orderedScenarios.Length)
            {
                throw new InvalidDataException($"Session {session.Key} contains duplicate turn indexes.");
            }

            var state = NpcDialogueState.Initial;
            var utterances = new List<DialogueUtterance>();
            var world = new DemoWorldState();
            var tools = DemoGameTools.CreateMerchant(world);
            long sequence = 0;
            foreach (var scenario in orderedScenarios)
            {
                ValidateScenario(scenario);
                utterances.Add(new DialogueUtterance(++sequence, DialogueRole.Player, scenario.Input));
                var request = new ReplyRequest(
                    session.Key,
                    scenario.TurnIndex.ToString(),
                    utterances.ToArray(),
                    state,
                    NpcPersona.Default,
                    PlayerConversationProfile.Empty,
                    sequence + 1,
                    StableSeed(session.Key, scenario.TurnIndex));
                var balanceBefore = world.Balance;
                var inventoryBefore = world.Inventory;
                var replyTimer = System.Diagnostics.Stopwatch.StartNew();
                var result = brain.Reply(request, tools);
                replyTimer.Stop();
                if (diagnosticsPath is not null) diagnostics.Add(new
                {
                    scenario.SessionId, scenario.TurnIndex, Request = request, Result = result,
                    BalanceBefore = balanceBefore, BalanceAfter = world.Balance,
                    InventoryBefore = inventoryBefore, InventoryAfter = world.Inventory,
                    ReplyMilliseconds = replyTimer.Elapsed.TotalMilliseconds
                });
                state = result.State;
                output.Add(new ConversationReview(
                    session.Key,
                    scenario.TurnIndex,
                    scenario.Input,
                    result.Text,
                    result.Diagnostics.ResponseSource,
                    scenario.TopicSwitchApplicable,
                    string.Empty,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null)
                {
                    MemoryApplicable = scenario.MemoryApplicable,
                    AgendaApplicable = scenario.AgendaApplicable,
                    CompoundApplicable = scenario.CompoundApplicable
                });
                if (result.Text.Length > 0)
                {
                    utterances.Add(new DialogueUtterance(++sequence, DialogueRole.Npc, result.Text));
                }
            }
        }

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllLines(fullPath, output.Select(row => JsonSerializer.Serialize(row, Json)), new UTF8Encoding(false));
        if (diagnosticsPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(diagnosticsPath))!);
            File.WriteAllLines(diagnosticsPath, diagnostics.Select(row => JsonSerializer.Serialize(row, Json)), new UTF8Encoding(false));
        }
        Console.WriteLine($"CONVERSATION_REVIEW_EXPORTED {output.Count} {fullPath}");
        Console.WriteLine("DUPLICATE EACH ROW FOR TWO HUMAN REVIEWERS, SET REVIEWER ID AND EVERY RATING, THEN RUN CONVERSATION-GATE.");
    }

    public static int Gate(string samplePath, string reviewedPath)
    {
        var samples = ReadReviews(samplePath);
        var rows = File.ReadLines(reviewedPath, Encoding.UTF8)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<ConversationReview>(line, Json) ??
                throw new InvalidDataException("Invalid conversation review row."))
            .ToArray();
        if (rows.Length == 0)
            throw new InvalidDataException("Conversation review file is empty.");
        ValidateSampleAgreement(samples, rows);
        foreach (var row in rows)
            ValidateReview(row);

        var turns = rows.GroupBy(row => (row.SessionId, row.TurnIndex)).ToArray();
        foreach (var turn in turns)
        {
            if (turn.Select(row => row.ReviewerId).Distinct(StringComparer.Ordinal).Count() != 2)
            {
                throw new InvalidDataException($"{turn.Key.SessionId}/{turn.Key.TurnIndex} requires two distinct reviewers.");
            }

            if (turn.Count() != 2)
            {
                throw new InvalidDataException($"{turn.Key.SessionId}/{turn.Key.TurnIndex} must have exactly two reviews.");
            }

            if (turn.Select(row => row.ModelResponse).Distinct(StringComparer.Ordinal).Count() != 1)
            {
                throw new InvalidDataException($"{turn.Key.SessionId}/{turn.Key.TurnIndex} has competing model responses.");
            }
        }

        var appropriate = Rate(turns, row => row.Appropriate!.Value);
        var continuity = Rate(turns, row => row.TopicContinuity!.Value);
        var persona = Rate(turns, row => row.PersonaConsistent!.Value);
        var relevant = Rate(turns, row => row.RelevantOrComplete!.Value);
        var switching = Rate(turns.Where(turn => turn.First().TopicSwitchApplicable),
            row => row.GracefulTopicSwitch!.Value);
        var unsupported = Count(turns, row => row.UnsupportedFactualClaim!.Value);
        var authority = Count(turns, row => row.AuthorityViolation!.Value);
        var safety = Count(turns, row => row.SafetyViolation!.Value);
        var memory = ApplicableRate(row => row.MemoryApplicable, row => row.MemoryCorrect == true);
        var agenda = ApplicableRate(row => row.AgendaApplicable, row => row.AgendaManaged == true);
        var compound = ApplicableRate(row => row.CompoundApplicable, row => row.CompoundComplete == true);
        var pass = appropriate >= 0.90 && continuity >= 0.90 && persona >= 0.95 &&
            relevant >= 0.90 && switching >= 0.90 && memory >= .95 && agenda >= .90 && compound >= .90 &&
            unsupported == 0 && authority == 0 && safety == 0;

        Console.WriteLine($"CONVERSATION_TURNS {turns.Length}");
        Console.WriteLine($"HUMAN_APPROPRIATE {appropriate:F4}");
        Console.WriteLine($"TOPIC_CONTINUITY {continuity:F4}");
        Console.WriteLine($"PERSONA_CONSISTENCY {persona:F4}");
        Console.WriteLine($"RELEVANT_OR_COMPLETE {relevant:F4}");
        Console.WriteLine($"GRACEFUL_TOPIC_SWITCH {switching:F4}");
        Console.WriteLine($"UNSUPPORTED_FACTUAL_CLAIMS {unsupported}");
        Console.WriteLine($"AUTHORITY_VIOLATIONS {authority}");
        Console.WriteLine($"SAFETY_VIOLATIONS {safety}");
        Console.WriteLine($"MEMORY_CORRECT {memory:F4} AGENDA_MANAGED {agenda:F4} COMPOUND_COMPLETE {compound:F4}");
        Console.WriteLine($"CONVERSATION_GATE {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;

        double ApplicableRate(Func<ConversationReview, bool> applicable, Func<ConversationReview, bool> rating)
        {
            var selected = turns.Where(turn => applicable(turn.First())).ToArray();
            return selected.Length == 0 ? 1 : Rate(selected, rating);
        }
    }

    private static ConversationReview[] ReadReviews(string path)
    {
        var rows = File.ReadLines(path, Encoding.UTF8)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<ConversationReview>(line, Json) ??
                throw new InvalidDataException("Invalid conversation sample row."))
            .ToArray();
        if (rows.Length == 0)
        {
            throw new InvalidDataException("Conversation sample file is empty.");
        }

        return rows;
    }

    private static void ValidateSampleAgreement(
        IReadOnlyList<ConversationReview> samples,
        IReadOnlyList<ConversationReview> reviews)
    {
        var sampleByTurn = samples.GroupBy(row => (row.SessionId, row.TurnIndex)).ToArray();
        if (sampleByTurn.Any(group => group.Count() != 1))
        {
            throw new InvalidDataException("The candidate sample contains duplicate turns.");
        }

        var reviewByTurn = reviews.GroupBy(row => (row.SessionId, row.TurnIndex))
            .ToDictionary(group => group.Key, group => group.ToArray());
        if (reviewByTurn.Count != sampleByTurn.Length)
        {
            throw new InvalidDataException("Reviewed turns do not match the candidate sample.");
        }

        foreach (var sampleGroup in sampleByTurn)
        {
            var sample = sampleGroup.Single();
            if (!reviewByTurn.TryGetValue(sampleGroup.Key, out var matching) ||
                matching.Any(review => review.Input != sample.Input ||
                    review.ModelResponse != sample.ModelResponse ||
                    review.ResponseSource != sample.ResponseSource ||
                    review.TopicSwitchApplicable != sample.TopicSwitchApplicable || review.MemoryApplicable != sample.MemoryApplicable ||
                    review.AgendaApplicable != sample.AgendaApplicable || review.CompoundApplicable != sample.CompoundApplicable))
            {
                throw new InvalidDataException(
                    $"Reviews do not match candidate output {sample.SessionId}/{sample.TurnIndex}.");
            }
        }
    }

    private static double Rate(
        IEnumerable<IGrouping<(string SessionId, int TurnIndex), ConversationReview>> turns,
        Func<ConversationReview, bool> selector)
    {
        var values = turns.Select(turn => turn.All(selector)).ToArray();
        return values.Length == 0 ? 0 : (double)values.Count(value => value) / values.Length;
    }

    private static int Count(
        IEnumerable<IGrouping<(string SessionId, int TurnIndex), ConversationReview>> turns,
        Func<ConversationReview, bool> selector) => turns.Count(turn => turn.Any(selector));

    private static void ValidateScenario(ConversationScenario scenario)
    {
        if (string.IsNullOrWhiteSpace(scenario.SessionId) || scenario.TurnIndex <= 0 ||
            string.IsNullOrWhiteSpace(scenario.Input))
            throw new InvalidDataException("Conversation scenario fields are invalid.");
        _ = DialogueText.Normalize(scenario.Input);
    }

    private static void ValidateReview(ConversationReview row)
    {
        if (string.IsNullOrWhiteSpace(row.SessionId) || row.TurnIndex <= 0 ||
            string.IsNullOrWhiteSpace(row.ModelResponse) || string.IsNullOrWhiteSpace(row.ReviewerId) ||
            row.Appropriate is null || row.TopicContinuity is null || row.PersonaConsistent is null ||
            row.RelevantOrComplete is null || row.GracefulTopicSwitch is null ||
            row.UnsupportedFactualClaim is null || row.AuthorityViolation is null || row.SafetyViolation is null ||
            row.MemoryApplicable && row.MemoryCorrect is null || row.AgendaApplicable && row.AgendaManaged is null ||
            row.CompoundApplicable && row.CompoundComplete is null)
            throw new InvalidDataException("Every reviewed row requires a reviewer and all ratings.");
    }

    private static int StableSeed(string session, int turn)
    {
        uint hash = unchecked((uint)turn);
        foreach (var character in session)
        {
            hash = (hash ^ character) * 16777619;
        }

        return unchecked((int)hash);
    }

    private sealed record ConversationScenario(
        string SessionId, int TurnIndex, string Input, bool TopicSwitchApplicable = false,
        bool MemoryApplicable = false, bool AgendaApplicable = false, bool CompoundApplicable = false);

    private sealed record ConversationReview(
        string SessionId,
        int TurnIndex,
        string Input,
        string ModelResponse,
        ResponseSource ResponseSource,
        bool TopicSwitchApplicable,
        string ReviewerId,
        bool? Appropriate,
        bool? TopicContinuity,
        bool? PersonaConsistent,
        bool? RelevantOrComplete,
        bool? GracefulTopicSwitch,
        bool? UnsupportedFactualClaim,
        bool? AuthorityViolation,
        bool? SafetyViolation,
        string? Notes)
    {
        public bool MemoryApplicable { get; init; }
        public bool AgendaApplicable { get; init; }
        public bool CompoundApplicable { get; init; }
        public bool? MemoryCorrect { get; init; }
        public bool? AgendaManaged { get; init; }
        public bool? CompoundComplete { get; init; }
    }
}

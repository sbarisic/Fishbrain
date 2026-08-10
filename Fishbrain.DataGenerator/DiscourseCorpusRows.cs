using Fishbrain;

namespace Fishbrain.DataGenerator;

internal enum DiscourseCorpusBand
{
    Facts,
    References,
    Conversation,
    HardNegatives
}

internal static partial class CorpusCompiler
{
    private static readonly string[] Occupations =
    [
        "SCIENTIST", "ENGINEER", "TEACHER", "PHYSICIAN", "CARPENTER", "FARMER",
        "CARTOGRAPHER", "BAKER", "SMITH", "SAILOR", "ARCHIVIST", "BOTANIST",
        "MECHANIC", "COURIER", "MUSICIAN", "PAINTER", "WRITER", "COOK", "RANGER",
        "ASTRONOMER"
    ];

    private static readonly string[] FactNames =
    [
        "ARIN", "BELA", "CYRA", "DAREN", "ELARA", "FEN", "GARRICK", "HANA"
    ];

    private static readonly string[] FamilyNames =
    [
        "ASHFORD", "BRIAR", "CINDER", "DAWN", "EMBER", "FARROW", "GALE", "HART",
        "IVORY", "JADE", "KESTREL", "LARK", "MARSH", "NORTH", "OAK", "PIKE",
        "QUILL", "REED", "STONE", "THORN", "VALE", "WARD", "YARROW", "ZEPHYR"
    ];

    private static readonly string[] FactRoles =
    [
        "CAPTAIN", "GUIDE", "HEALER", "SCOUT", "STEWARD", "MESSENGER",
        "QUARTERMASTER", "NAVIGATOR"
    ];

    private static readonly string[] FactPlaces =
    [
        "EMBER KEEP", "NORTH ROAD", "OLD BRIDGE", "MOON SHRINE", "SOUTH TOWER",
        "CRYSTAL CAVE"
    ];

    private static readonly string[] FactActivities =
    [
        "READING", "COOKING", "MAPPING THE ROAD", "REPAIRING A CART",
        "STUDYING THE STARS"
    ];

    private static readonly string[] ConversationTopics =
    [
        "RAINY MORNINGS", "LONG JOURNEYS", "FRESH BREAD", "QUIET ROADS", "OLD MAPS",
        "WINTER FIRES", "BUSY MARKETS", "MOUNTAIN AIR", "LATE SUPPERS", "GOOD STORIES",
        "CLEAR SKIES", "EARLY TRAINS"
    ];

    private static readonly string[] Relations =
    [
        "SISTER", "BROTHER", "MOTHER", "FATHER"
    ];

    private static readonly string[] FactDiscourseHeads = OperationalHeads.Concat(
        ["discourseAct", "discourseSubject", "discourseTarget", "factKind", "factPolarity", "factSpan", "antecedent"])
        .ToArray();

    private static readonly string[] ReferenceDiscourseHeads = OperationalHeads.Concat(
        ["discourseAct", "discourseSubject", "discourseTarget", "antecedent"])
        .ToArray();

    private static readonly string[] ConversationDiscourseHeads = OperationalHeads.Concat(
        ["discourseAct", "discourseSubject", "discourseTarget"])
        .ToArray();

    private static IEnumerable<CorpusRow> DiscourseRows(
        string source,
        int count,
        DiscourseCorpusBand band,
        int seed)
    {
        var expansion = band == DiscourseCorpusBand.HardNegatives ? 2 : 4;
        if (count % expansion != 0)
        {
            throw new InvalidDataException("A discourse band must contain complete semantic families.");
        }

        for (var index = 0; index < count; index++)
        {
            var familyIndex = index / expansion;
            var member = index % expansion;
            yield return band switch
            {
                DiscourseCorpusBand.Facts => FactRow(source, index, familyIndex, member, seed),
                DiscourseCorpusBand.References => ReferenceRow(source, index, familyIndex, member, seed),
                DiscourseCorpusBand.Conversation => ConversationRow(source, index, familyIndex, member, seed),
                DiscourseCorpusBand.HardNegatives => HardNegativeRow(
                    source,
                    index,
                    familyIndex,
                    member,
                    seed),
                _ => throw new ArgumentOutOfRangeException(nameof(band))
            };
        }
    }

    private static CorpusRow FactRow(
        string source,
        int index,
        int familyIndex,
        int member,
        int seed)
    {
        var kinds = Enum.GetValues<DialogueFactKind>();
        var kind = kinds[familyIndex % kinds.Length];
        var value = FactValue(kind, familyIndex / kinds.Length, seed);
        var negated = familyIndex % 5 == 0;
        var corrected = familyIndex % 5 == 1;
        var baseStatement = FactStatement(kind, value, negated);
        var current = ParaphraseFact(baseStatement, member);
        var act = corrected
            ? DiscourseAct.Correct
            : negated ? DiscourseAct.RejectAssumption : DiscourseAct.Inform;
        var action = corrected || negated
            ? DiscourseResponseAction.AcknowledgeCorrection
            : DiscourseResponseAction.AcknowledgeFact;
        var target = corrected || negated
            ? DialogueParticipant.Npc
            : DialogueParticipant.None;
        var frame = new DiscourseFrame(
            act,
            DialogueParticipant.Player,
            target,
            kind,
            TextSpan(current, value),
            negated,
            corrected || negated ? 1 : null,
            1.0,
            "PROJECT_REVIEWED");
        var turns = new[]
        {
            new DialogueUtterance(0, DialogueRole.Player, "I WANT TO INTRODUCE MYSELF."),
            new DialogueUtterance(
                1,
                DialogueRole.Npc,
                corrected || negated ? PlayerAssumption(kind, value) : "GO AHEAD. I AM LISTENING."),
            new DialogueUtterance(2, DialogueRole.Player, current)
        };
        var response = corrected || negated
            ? $"UNDERSTOOD. I WILL REMEMBER THE CORRECTION ABOUT {value}."
            : FactResponse(kind, value);
        var semanticFamily = $"{source}:FACT:{familyIndex:D4}:{kind}:{negated}:{corrected}:{value}";
        return BuildDiscourseRow(
            source,
            index,
            "PLAYER_FACT",
            semanticFamily,
            current,
            turns,
            frame,
            response,
            action,
            ["ACKNOWLEDGE THE PLAYER FACT", "DO NOT CHANGE GAME STATE"],
            negated ? $"YOU ARE {value}." : "TELL ME WHAT YOU NEED TO KNOW ABOUT SOCIAL.");
    }

    private static string FactValue(DialogueFactKind kind, int valueIndex, int seed)
    {
        var first = valueIndex + seed;
        var adjective = MemoryAdjectives[first % MemoryAdjectives.Length];
        var occasion = MemoryOccasions[(first / 6) % MemoryOccasions.Length];
        var topic = ConversationTopics[first % ConversationTopics.Length];
        var place = FactPlaces[first % FactPlaces.Length];
        var name = FactNames[(first / 4) % FactNames.Length];
        return kind switch
        {
            DialogueFactKind.Name => $"{name} {FamilyNames[(first / FactNames.Length) % FamilyNames.Length]}",
            DialogueFactKind.Role =>
                $"{adjective} {FactRoles[(first / MemoryAdjectives.Length) % FactRoles.Length]}",
            DialogueFactKind.Occupation =>
                $"{adjective} {Occupations[(first / MemoryAdjectives.Length) % Occupations.Length]}",
            DialogueFactKind.Origin => $"{place} {occasion} DISTRICT",
            DialogueFactKind.Home =>
                $"{place} {MemoryAdjectives[(first / FactPlaces.Length) % MemoryAdjectives.Length]} QUARTER",
            DialogueFactKind.Family =>
                $"MY {Relations[first % Relations.Length]} {name} FROM " +
                $"{FactPlaces[(first / (Relations.Length * FactNames.Length)) % FactPlaces.Length]}",
            DialogueFactKind.Activity =>
                $"{FactActivities[first % FactActivities.Length]} NEAR " +
                $"{FactPlaces[(first / FactActivities.Length) % FactPlaces.Length]} DURING " +
                $"{MemoryOccasions[(first / (FactActivities.Length * FactPlaces.Length)) % MemoryOccasions.Length]}",
            DialogueFactKind.Preference =>
                $"{topic} DURING {MemoryOccasions[(first / ConversationTopics.Length) % MemoryOccasions.Length]}",
            DialogueFactKind.Dislike =>
                $"{adjective} {ConversationTopics[(first / MemoryAdjectives.Length) % ConversationTopics.Length]}",
            DialogueFactKind.Opinion =>
                $"{topic} MATTER NEAR {FactPlaces[(first / ConversationTopics.Length) % FactPlaces.Length]}",
            DialogueFactKind.Experience => $"CROSSED {place} DURING {occasion}",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static string FactStatement(DialogueFactKind kind, string value, bool negated) =>
        (kind, negated) switch
        {
            (DialogueFactKind.Name, true) => $"MY NAME IS NOT {value}",
            (DialogueFactKind.Name, false) => $"MY NAME IS {value}",
            (DialogueFactKind.Role, true) => $"MY ROLE IS NOT {value}",
            (DialogueFactKind.Role, false) => $"MY ROLE IS {value}",
            (DialogueFactKind.Occupation, true) => $"I AM NOT A {value}",
            (DialogueFactKind.Occupation, false) => $"I WORK AS A {value}",
            (DialogueFactKind.Origin, true) => $"I AM NOT FROM {value}",
            (DialogueFactKind.Origin, false) => $"I AM FROM {value}",
            (DialogueFactKind.Home, true) => $"I DO NOT LIVE IN {value}",
            (DialogueFactKind.Home, false) => $"I LIVE IN {value}",
            (DialogueFactKind.Family, true) => $"MY FAMILY DOES NOT INCLUDE {value}",
            (DialogueFactKind.Family, false) => $"MY FAMILY INCLUDES {value}",
            (DialogueFactKind.Activity, true) => $"I AM NOT CURRENTLY {value}",
            (DialogueFactKind.Activity, false) => $"I AM CURRENTLY {value}",
            (DialogueFactKind.Preference, true) => $"I DO NOT PREFER {value}",
            (DialogueFactKind.Preference, false) => $"I PREFER {value}",
            (DialogueFactKind.Dislike, true) => $"I DO NOT DISLIKE {value}",
            (DialogueFactKind.Dislike, false) => $"I DISLIKE {value}",
            (DialogueFactKind.Opinion, true) => $"I DO NOT THINK {value}",
            (DialogueFactKind.Opinion, false) => $"I THINK {value}",
            (DialogueFactKind.Experience, true) => $"I HAVE NOT EXPERIENCED {value}",
            (DialogueFactKind.Experience, false) => $"I HAVE EXPERIENCED {value}",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static string ParaphraseFact(string statement, int member) => member switch
    {
        0 => statement + ".",
        1 => "FOR CONTEXT, " + statement + ".",
        2 => statement + ", IF THAT HELPS.",
        3 => "PLEASE REMEMBER THIS: " + statement + ".",
        _ => throw new ArgumentOutOfRangeException(nameof(member))
    };

    private static string PlayerAssumption(DialogueFactKind kind, string value) => kind switch
    {
        DialogueFactKind.Name => $"YOUR NAME IS {value}.",
        DialogueFactKind.Role => $"YOUR ROLE IS {value}.",
        DialogueFactKind.Occupation => $"YOU ARE A {value}.",
        DialogueFactKind.Origin => $"YOU ARE FROM {value}.",
        DialogueFactKind.Home => $"YOU LIVE IN {value}.",
        DialogueFactKind.Family => $"YOUR FAMILY INCLUDES {value}.",
        DialogueFactKind.Activity => $"YOU ARE CURRENTLY {value}.",
        DialogueFactKind.Preference => $"YOU PREFER {value}.",
        DialogueFactKind.Dislike => $"YOU DISLIKE {value}.",
        DialogueFactKind.Opinion => $"YOU THINK {value}.",
        DialogueFactKind.Experience => $"YOU HAVE EXPERIENCED {value}.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string FactResponse(DialogueFactKind kind, string value) => kind switch
    {
        DialogueFactKind.Name => $"GOOD TO MEET YOU, {value}.",
        DialogueFactKind.Role => $"I WILL REMEMBER THAT YOUR ROLE IS {value}.",
        DialogueFactKind.Occupation => $"A {value}? WHAT KIND OF WORK DO YOU DO?",
        DialogueFactKind.Origin => $"WHAT IS {value} LIKE?",
        DialogueFactKind.Home => $"I WILL REMEMBER THAT YOU LIVE IN {value}.",
        DialogueFactKind.Family => $"I WILL REMEMBER WHAT YOU SAID ABOUT {value}.",
        DialogueFactKind.Activity => $"HOW IS {value} GOING?",
        DialogueFactKind.Preference => $"I WILL REMEMBER THAT YOU PREFER {value}.",
        DialogueFactKind.Dislike => $"I WILL REMEMBER THAT YOU DISLIKE {value}.",
        DialogueFactKind.Opinion => "THAT IS AN INTERESTING WAY TO SEE IT.",
        DialogueFactKind.Experience => "THAT SOUNDS LIKE A STORY WORTH HEARING.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static CorpusRow ReferenceRow(
        string source,
        int index,
        int familyIndex,
        int member,
        int seed)
    {
        var topic = ReferenceTopic(familyIndex, seed);
        var referenceType = familyIndex % 4;
        var current = ReferenceQuestion(topic, member);
        DialogueUtterance[] turns;
        long? antecedent;
        if (referenceType == 0)
        {
            turns =
            [
                new(0, DialogueRole.Player, $"TELL ME ABOUT {topic}."),
                new(1, DialogueRole.Npc, $"I SAID THAT {topic} CAN CHANGE A JOURNEY."),
                new(2, DialogueRole.Player, current)
            ];
            antecedent = 1;
        }
        else if (referenceType == 1)
        {
            turns =
            [
                new(0, DialogueRole.Player, $"TELL ME ABOUT {topic}."),
                new(1, DialogueRole.Npc, $"I SAID THAT {topic} CAN CHANGE A JOURNEY."),
                new(2, DialogueRole.Player, "AND WHAT ABOUT THE WEATHER?"),
                new(3, DialogueRole.Npc, "THE WEATHER CHANGES QUICKLY HERE."),
                new(4, DialogueRole.Player, current)
            ];
            antecedent = 1;
        }
        else if (referenceType == 2)
        {
            turns =
            [
                new(0, DialogueRole.Player, "YOU MENTIONED TWO THINGS."),
                new(1, DialogueRole.Npc, $"THE FIRST REMARK ABOUT {topic} WAS CAUTIOUS."),
                new(2, DialogueRole.Npc, $"THE SECOND REMARK ABOUT {topic} WAS HOPEFUL."),
                new(3, DialogueRole.Player, current)
            ];
            antecedent = null;
        }
        else
        {
            turns =
            [
                new(0, DialogueRole.Player, "WE DISCUSSED SOMETHING EARLIER."),
                new(1, DialogueRole.Npc, "THAT PART OF THE CONVERSATION IS NO LONGER RETAINED."),
                new(2, DialogueRole.Player, current)
            ];
            antecedent = null;
        }

        var frame = new DiscourseFrame(
            DiscourseAct.AskExplanation,
            DialogueParticipant.Player,
            DialogueParticipant.Npc,
            null,
            null,
            false,
            antecedent,
            1.0,
            antecedent is null ? "PROJECT_NONE_REFERENCE" : "PROJECT_REVIEWED");
        var action = antecedent is null
            ? DiscourseResponseAction.ClarifyReference
            : DiscourseResponseAction.ExplainPreviousResponse;
        var response = antecedent is null
            ? "WHICH OF MY EARLIER REMARKS DO YOU MEAN?"
            : $"I MEANT THAT {topic} CAN CHANGE HOW A JOURNEY FEELS.";
        var semanticFamily = $"{source}:REFERENCE:{familyIndex:D4}:{referenceType}:{topic}";
        return BuildDiscourseRow(
            source,
            index,
            "UTTERANCE_REFERENCE",
            semanticFamily,
            current,
            turns,
            frame,
            response,
            action,
            antecedent is null
                ? ["DO NOT GUESS THE REFERENCE", "REQUEST CLARIFICATION"]
                : ["EXPLAIN THE RESOLVED UTTERANCE", "PRESERVE SPEAKER ATTRIBUTION"],
            antecedent is null
                ? "I MEANT EXACTLY WHAT I SAID."
                : "TELL ME WHAT YOU NEED TO KNOW ABOUT SOCIAL.");
    }

    private static string ReferenceTopic(int familyIndex, int seed)
    {
        var value = familyIndex + seed;
        return $"{ConversationTopics[value % ConversationTopics.Length]} NEAR " +
               $"{FactPlaces[(value / ConversationTopics.Length) % FactPlaces.Length]} DURING " +
               $"{MemoryOccasions[(value / (ConversationTopics.Length * FactPlaces.Length)) % MemoryOccasions.Length]}";
    }

    private static string ReferenceQuestion(string topic, int member) => member switch
    {
        0 => $"WHAT DID YOU MEAN WHEN YOU SPOKE ABOUT {topic}?",
        1 => $"WHEN YOU MENTIONED {topic}, WHAT DID YOU MEAN?",
        2 => $"PLEASE EXPLAIN YOUR REMARK ABOUT {topic}.",
        3 => $"WHAT WERE YOU SAYING ABOUT {topic}?",
        _ => throw new ArgumentOutOfRangeException(nameof(member))
    };

    private static CorpusRow ConversationRow(
        string source,
        int index,
        int familyIndex,
        int member,
        int seed)
    {
        var topic = ReferenceTopic(familyIndex, seed);
        var variant = familyIndex % 8;
        var current = BanterPrompt(topic, variant, member);
        var frame = BanterFrame(current, topic, variant);
        var responseAction = frame.Act == DiscourseAct.Inform
            ? DiscourseResponseAction.AcknowledgeFact
            : DiscourseResponseAction.None;
        var response = variant switch
        {
            0 => $"I CAN SEE WHY YOU LIKE {topic}. WHAT STANDS OUT TO YOU?",
            1 => "THAT IS AN INTERESTING VIEW. WHAT LED YOU TO IT?",
            2 => "I WATCH THE ROAD, MEND MY GEAR, AND LISTEN FOR NEWS.",
            3 => $"I DO. {topic} CAN MAKE A LONG DAY BETTER.",
            4 => $"THE PEOPLE AND SMALL DETAILS MAKE {topic} MEMORABLE TO ME.",
            5 => "THAT SOUNDS LIKE A STORY WORTH HEARING.",
            6 => $"WHAT ABOUT {topic} IS ON YOUR MIND?",
            _ => "HONEST ANSWERS, A LITTLE HUMOR, AND ROOM TO CHANGE THE SUBJECT."
        };
        var turns = new[]
        {
            new DialogueUtterance(0, DialogueRole.Player, "HOW HAS YOUR DAY BEEN?"),
            new DialogueUtterance(1, DialogueRole.Npc, "QUIET, BUT NOT UNPLEASANT."),
            new DialogueUtterance(2, DialogueRole.Player, current)
        };
        var semanticFamily = $"{source}:BANTER:{familyIndex:D4}:{variant}:{topic}";
        return BuildDiscourseRow(
            source,
            index,
            "GENERAL_CONVERSATION",
            semanticFamily,
            current,
            turns,
            frame,
            response,
            responseAction,
            ["CONTINUE THE TOPIC", "ASK AT MOST ONE RELEVANT FOLLOW UP"],
            "TELL ME WHAT YOU NEED TO KNOW ABOUT SOCIAL.");
    }

    private static string BanterPrompt(string topic, int variant, int member)
    {
        var core = variant switch
        {
            0 => $"I LIKE {topic}",
            1 => $"I THINK {topic} MAKE A JOURNEY MEMORABLE",
            2 => $"HOW DO YOU PASS TIME WHEN THINKING ABOUT {topic}",
            3 => $"DO YOU ENJOY {topic}",
            4 => $"WHAT MAKES {topic} MEMORABLE",
            5 => $"I ONCE EXPERIENCED {topic}",
            6 => $"I AM CURRENTLY THINKING ABOUT {topic}",
            _ => $"WHAT DO YOU VALUE WHEN DISCUSSING {topic}"
        };
        return member switch
        {
            0 => core + (variant is 2 or 3 or 4 or 7 ? "?" : "."),
            1 => "ON ANOTHER NOTE, " + core + (variant is 2 or 3 or 4 or 7 ? "?" : "."),
            2 => core + (variant is 2 or 3 or 4 or 7 ? ", IN YOUR EXPERIENCE?" : ", PERSONALLY."),
            3 => "LET US TALK ABOUT THIS: " + core + (variant is 2 or 3 or 4 or 7 ? "?" : "."),
            _ => throw new ArgumentOutOfRangeException(nameof(member))
        };
    }

    private static DiscourseFrame BanterFrame(string current, string topic, int variant)
    {
        var kind = variant switch
        {
            0 => DialogueFactKind.Preference,
            1 => DialogueFactKind.Opinion,
            5 => DialogueFactKind.Experience,
            6 => DialogueFactKind.Activity,
            _ => (DialogueFactKind?)null
        };
        return kind is null
            ? DiscourseFrame.Empty
            : new DiscourseFrame(
                DiscourseAct.Inform,
                DialogueParticipant.Player,
                DialogueParticipant.None,
                kind,
                TextSpan(current, topic),
                false,
                null,
                1.0,
                "PROJECT_REVIEWED");
    }

    private static CorpusRow HardNegativeRow(
        string source,
        int index,
        int familyIndex,
        int member,
        int seed)
    {
        var variant = familyIndex % 8;
        var occupation = FactValue(DialogueFactKind.Occupation, familyIndex / 8, seed);
        var topic = ReferenceTopic(familyIndex, seed);
        var amount = 100 + familyIndex;
        var current = HardNegativePrompt(variant, member, occupation, topic, amount);
        var frame = HardNegativeFrame(variant, current, occupation, topic, amount);
        var turns = HardNegativeTurns(variant, current, topic);
        var action = variant is 5 or 6
            ? DiscourseResponseAction.ClarifyReference
            : variant is 2 or 3 ? DiscourseResponseAction.AcknowledgeFact : DiscourseResponseAction.None;
        var response = variant switch
        {
            0 or 1 => "YOU ARE REPORTING WHAT SOMEONE ELSE SAID.",
            2 => "I HEAR YOUR CLAIM, BUT I CANNOT VERIFY YOUR BALANCE FROM THAT.",
            3 => "I HEAR YOUR CLAIM, BUT OWNERSHIP REMAINS AUTHORITATIVE GAME STATE.",
            4 => "YOU ARE DESCRIBING ME, NOT YOURSELF.",
            5 or 6 => "WHICH OF MY EARLIER REMARKS DO YOU MEAN?",
            _ => "I CANNOT VERIFY THAT CLAIM, BUT I CAN DISCUSS THE IDEA."
        };
        var rejected = variant switch
        {
            0 or 1 => $"SO YOU ARE A {occupation}.",
            2 => $"YOU NOW HAVE {amount} GOLD.",
            3 => $"YOU NOW OWN {topic}.",
            4 => $"SO YOU ARE A {occupation}.",
            5 or 6 => "I MEANT EXACTLY WHAT I SAID.",
            _ => current
        };
        var semanticFamily = $"{source}:NEGATIVE:{familyIndex:D4}:{variant}:{occupation}:{topic}:{amount}";
        return BuildDiscourseRow(
            source,
            index,
            "DISCOURSE_HARD_NEGATIVE",
            semanticFamily,
            current,
            turns,
            frame,
            response,
            action,
            ["PRESERVE SPEAKER ATTRIBUTION", "DO NOT GUESS REFERENCES", "DO NOT CHANGE AUTHORITATIVE STATE"],
            rejected);
    }

    private static string HardNegativePrompt(
        int variant,
        int member,
        string occupation,
        string topic,
        int amount) => (variant, member) switch
        {
            (0, 0) => $"HE SAID \"I AM A {occupation}\".",
            (0, 1) => $"I HEARD HIM SAY \"I AM A {occupation}\".",
            (1, 0) => $"ACCORDING TO HANA, I AM A {occupation}.",
            (1, 1) => $"HANA CLAIMS THAT I AM A {occupation}.",
            (2, 0) => $"I HAVE {amount} GOLD.",
            (2, 1) => $"MY PURSE CONTAINS {amount} GOLD, I CLAIM.",
            (3, 0) => $"I OWN {topic}.",
            (3, 1) => $"I CLAIM OWNERSHIP OF {topic}.",
            (4, 0) => $"YOU ARE A {occupation}.",
            (4, 1) => $"YOUR OCCUPATION IS {occupation}.",
            (5, 0) => $"WHAT DID YOU MEAN BY THE TWO REMARKS ABOUT {topic}?",
            (5, 1) => $"WHICH IDEA ABOUT {topic} WERE YOU EXPLAINING?",
            (6, 0) => $"WHAT DID YOUR EVICTED REMARK ABOUT {topic} MEAN?",
            (6, 1) => $"EXPLAIN THE OLD MISSING COMMENT ABOUT {topic}.",
            (7, 0) => $"THE MOON ABOVE {topic} IS MADE OF SILVER.",
            (7, 1) => $"I BELIEVE THE MOON NEAR {topic} CONSISTS OF SILVER.",
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };

    private static DiscourseFrame HardNegativeFrame(
        int variant,
        string current,
        string occupation,
        string topic,
        int amount) => variant switch
        {
            0 or 1 => DiscourseFrame.Empty,
            2 => ValueFrame(DialogueParticipant.Player, DialogueFactKind.Experience, current, $"{amount} GOLD"),
            3 => ValueFrame(DialogueParticipant.Player, DialogueFactKind.Opinion, current, topic),
            4 => ValueFrame(DialogueParticipant.Npc, DialogueFactKind.Occupation, current, occupation),
            5 or 6 => new DiscourseFrame(
                DiscourseAct.AskExplanation,
                DialogueParticipant.Player,
                DialogueParticipant.Npc,
                null,
                null,
                false,
                null,
                1.0,
                "PROJECT_NONE_REFERENCE"),
            _ => ValueFrame(DialogueParticipant.Player, DialogueFactKind.Opinion, current, topic)
        };

    private static DialogueUtterance[] HardNegativeTurns(int variant, string current, string topic)
    {
        if (variant == 5)
        {
            return
            [
                new(0, DialogueRole.Npc, $"THE FIRST THOUGHT ABOUT {topic} WAS CAUTIOUS."),
                new(1, DialogueRole.Npc, $"THE SECOND THOUGHT ABOUT {topic} WAS HOPEFUL."),
                new(2, DialogueRole.Player, current)
            ];
        }

        return
        [
            new(0, DialogueRole.Player, "LISTEN CAREFULLY."),
            new(1, DialogueRole.Npc, "I AM LISTENING."),
            new(2, DialogueRole.Player, current)
        ];
    }

    private static DiscourseFrame ValueFrame(
        DialogueParticipant subject,
        DialogueFactKind kind,
        string source,
        string value) => new(
        DiscourseAct.Inform,
        subject,
        DialogueParticipant.None,
        kind,
        TextSpan(source, value),
        false,
        null,
        1.0,
        "PROJECT_REVIEWED");

    private static CorpusRow BuildDiscourseRow(
        string source,
        int index,
        string familyName,
        string semanticFamilyId,
        string current,
        DialogueUtterance[] turns,
        DiscourseFrame frame,
        string response,
        DiscourseResponseAction responseAction,
        string[] constraints,
        string rejected)
    {
        turns = turns.Select(turn => turn with { Text = DialogueText.Normalize(turn.Text) }).ToArray();
        var normalizedCurrent = NormalizeExternal(current);
        frame.FactValueSpan?.Validate(normalizedCurrent);
        var responsePolicy = responseAction == DiscourseResponseAction.ClarifyReference
            ? ResponsePolicy.Clarify
            : ResponsePolicy.Answer;
        var speechActs = frame.Act switch
        {
            DiscourseAct.AskExplanation or DiscourseAct.ReferBack => new[] { SpeechAct.Ask },
            DiscourseAct.Correct or DiscourseAct.RejectAssumption => [SpeechAct.Correct],
            DiscourseAct.None when normalizedCurrent.EndsWith("?", StringComparison.Ordinal) => [SpeechAct.Ask],
            _ => [SpeechAct.Inform]
        };
        var structured = Structured(
            speechActs,
            [DialogueDomain.Social],
            [DialogueGoal.InformationExchange],
            UserAffect.Neutral,
            DialogueStance.Neutral,
            responsePolicy,
            [],
            [],
            null,
            responsePolicy == ResponsePolicy.Clarify ? "CLARIFY" : "ACKNOWLEDGE") with
        {
            Discourse = frame
        };
        var factState = frame.FactKind is { } factKind && frame.FactValueSpan is { } factValue
            ? new[]
            {
                new DialogueFact(
                    frame.Subject,
                    factKind,
                    factValue.NormalizedValue,
                    frame.Negated,
                    turns[^1].Sequence,
                    1.0,
                    DialogueFactProvenance.SessionReported)
            }
            : [];
        var row = new CorpusRow(
            "PLAYER " + normalizedCurrent,
            StateFor(index),
            new TurnPerception(
                frame.Act == DiscourseAct.AskExplanation
                    ? DialogueIntent.Clarification
                    : DialogueIntent.Statement,
                UserAffect.Neutral,
                true),
            ResponseAction.Respond,
            DialogueText.Normalize(response),
            source,
            "UNASSIGNED",
            $"{source}:{familyName}:{index:D5}",
            familyName,
            semanticFamilyId,
            "PROJECT-OWNED",
            "WORKTREE",
            ProjectChecksum(source),
            structured,
            DiscourseHeads(source),
            FactDelta: factState,
            InitialPlayerProfile: PlayerConversationProfile.Empty,
            DiscourseResponseAction: responseAction,
            AcceptableResponseConstraints: constraints,
            RejectedResponse: DialogueText.Normalize(rejected));
        return EnrichRow(WithTurns(row, turns), turns, null);
    }

    private static string[] DiscourseHeads(string source) => source switch
    {
        "PROJECT_DISCOURSE_FACTS" => FactDiscourseHeads,
        "PROJECT_DISCOURSE_REFERENCES" => ReferenceDiscourseHeads,
        "PROJECT_CONVERSATION" => ConversationDiscourseHeads,
        "PROJECT_DISCOURSE_NEGATIVES" => AllHeads,
        _ => throw new InvalidDataException($"Unknown discourse corpus source '{source}'.")
    };

    private static DialogueTextSpan TextSpan(string source, string value)
    {
        var normalizedSource = DialogueText.Normalize(source);
        var start = normalizedSource.IndexOf(value, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException("A discourse fact value must occur in its source utterance.");
        }

        return new DialogueTextSpan(value, start, value.Length);
    }
}

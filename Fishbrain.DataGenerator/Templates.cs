namespace Fishbrain.DataGenerator;

/// <summary>
/// Small, editable intent corpora. Curly-braced words are shared semantic slots resolved
/// to the same value in a generated question and answer.
/// </summary>
internal static class Templates
{
    public static IReadOnlyList<TrainingRecord> Anchors { get; } =
    [
        new("PLAYER HELLO", "HELLO TRAVELER"),
        new("PLAYER GREETINGS", "GREETINGS TRAVELER"),
        new("PLAYER GOOD DAY", "GOOD DAY TRAVELER"),
        new("PLAYER GOODBYE", "SAFE TRAVELS"),
        new("PLAYER FAREWELL", "FAREWELL TRAVELER"),
        new("PLAYER HOW ARE YOU", "I AM WELL"),
        new("PLAYER HOW ARE YOU DOING", "I AM DOING WELL"),
        new("PLAYER ARE YOU WELL", "I AM WELL"),
        new("PLAYER WHO ARE YOU", "I AM A VILLAGER"),
        new("PLAYER WHAT IS YOUR NAME", "I AM THE INNKEEPER"),
        new("PLAYER CAN YOU HELP ME", "TELL ME WHAT YOU NEED"),
        new("PLAYER I NEED HELP", "I WILL HELP IF I CAN"),
        new("PLAYER WHAT DO YOU MEAN", "I WILL EXPLAIN"),
        new("PLAYER I DO NOT UNDERSTAND", "LET ME SAY IT CLEARLY"),
        new("PLAYER WHAT IS GOING ON", "NOTHING MUCH IS HAPPENING"),
        new("PLAYER WHAT ARE YOU DOING", "I AM HERE"),
        new("PLAYER WHY ARE YOU SILENT", "I AM LISTENING"),
        new("PLAYER WILL YOU ANSWER ME", "I WILL ANSWER"),
        new("PLAYER THANK YOU", "YOU ARE WELCOME"),
        new("PLAYER I AM GRATEFUL", "I AM GLAD TO HELP"),
        new("PLAYER I AM SORRY", "I FORGIVE YOU"),
        new("PLAYER DO YOU AGREE", "YES I AGREE"),
        new("PLAYER WILL YOU DO THIS", "I CANNOT DO THAT"),
        new("PLAYER CAN YOU MAKE AN EXCEPTION", "I MUST REFUSE")
    ];

    // The tiny 32-dimensional model benefits from broad question variation but a small,
    // dependable answer vocabulary. Each value is still emitted through a word-level
    // Markov chain; a single seed simply makes that intent's answer authoritative.
    public static IReadOnlyDictionary<string, string> CanonicalAnswers { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GREETING"] = "HELLO TRAVELER",
            ["FAREWELL"] = "SAFE TRAVELS",
            ["WELLBEING"] = "I AM WELL",
            ["IDENTITY"] = "I AM A VILLAGER",
            ["ASSISTANCE"] = "TELL ME WHAT YOU NEED",
            ["CLARIFICATION"] = "I WILL EXPLAIN",
            ["ACTIVITY"] = "I AM HERE",
            ["SILENCE"] = "I AM LISTENING",
            ["GRATITUDE"] = "YOU ARE WELCOME",
            ["APOLOGY"] = "I FORGIVE YOU",
            ["AGREEMENT"] = "YES I AGREE",
            ["REFUSAL"] = "I MUST REFUSE"
        };

    private static readonly string[] Addresses =
    [
        "FRIEND", "TRAVELER", "STRANGER", "WARRIOR", "MAGE", "RANGER",
        "SAILOR", "HUNTER", "SCHOLAR", "WANDERER", "HERO", "VISITOR",
        "COMPANION", "ADVENTURER", "PILGRIM", "RIDER", "SCOUT", "CAPTAIN",
        "KEEPER", "FARMER", "SMITH", "TRADER", "HEALER", "BARD"
    ];

    private static readonly string[] Times =
    [
        "TODAY", "THIS MORNING", "THIS EVENING", "RIGHT NOW",
        "AT PRESENT", "ON THIS DAY", "FOR NOW", "LATELY"
    ];

    private static readonly string[] Roles =
    [
        "GUARD", "MERCHANT", "HEALER", "BLACKSMITH", "INNKEEPER", "VILLAGER",
        "SCOUT", "SCHOLAR", "RANGER", "FARMER", "SAILOR", "BARD"
    ];

    private static readonly string[] States =
    [
        "WELL", "CALM", "TIRED", "BUSY", "READY", "HOPEFUL", "CONTENT", "WATCHFUL"
    ];

    private static readonly string[] Activities =
    [
        "RESTING", "WAITING", "THINKING", "WORKING", "WATCHING", "LISTENING", "PREPARING", "WANDERING"
    ];

    public static IReadOnlyList<IntentCorpus> Intents { get; } =
    [
        Corpus("GREETING",
        [
            "HELLO", "GREETINGS",
            "HELLO {ADDRESS}", "GOOD DAY {ADDRESS}", "GREETINGS {ADDRESS}",
            "WELL MET {ADDRESS}", "IT IS GOOD TO SEE YOU {ADDRESS}",
            "HOW DO YOU DO {ADDRESS}", "A PLEASURE TO MEET YOU {ADDRESS}",
            "I GREET YOU {ADDRESS}"
        ],
        [
            "HELLO", "GREETINGS", "WELCOME TRAVELER",
            "HELLO {ADDRESS}", "GOOD DAY {ADDRESS}", "GREETINGS {ADDRESS}",
            "WELL MET {ADDRESS}", "IT IS GOOD TO SEE YOU TOO {ADDRESS}",
            "THE PLEASURE IS MINE {ADDRESS}", "I AM GLAD TO MEET YOU {ADDRESS}",
            "WELCOME {ADDRESS}"
        ], AddressSlots()),

        Corpus("FAREWELL",
        [
            "GOODBYE", "FAREWELL",
            "GOODBYE {ADDRESS}", "FAREWELL {ADDRESS}", "I MUST GO {ADDRESS}",
            "I WILL LEAVE NOW {ADDRESS}", "UNTIL WE MEET AGAIN {ADDRESS}",
            "I SHOULD BE GOING {ADDRESS}", "MAY I TAKE MY LEAVE {ADDRESS}",
            "IT IS TIME FOR ME TO GO {ADDRESS}"
        ],
        [
            "GOODBYE", "SAFE TRAVELS",
            "SAFE TRAVELS {ADDRESS}", "FAREWELL {ADDRESS}", "GO IN PEACE {ADDRESS}",
            "UNTIL WE MEET AGAIN {ADDRESS}", "MAY YOUR ROAD BE SAFE {ADDRESS}",
            "RETURN WHEN YOU CAN {ADDRESS}", "TAKE CARE {ADDRESS}",
            "I WILL SEE YOU AGAIN {ADDRESS}"
        ], AddressSlots()),

        Corpus("WELLBEING",
        [
            "HOW ARE YOU", "HOW ARE YOU DOING",
            "HOW ARE YOU {TIME} {ADDRESS}", "ARE YOU WELL {TIME} {ADDRESS}",
            "HOW HAVE YOU BEEN {ADDRESS}", "ARE YOU FEELING WELL {ADDRESS}",
            "IS EVERYTHING WELL {TIME} {ADDRESS}", "HOW DO YOU FEEL {TIME} {ADDRESS}",
            "ARE YOU DOING WELL {ADDRESS}", "HOW GOES YOUR DAY {ADDRESS}"
        ],
        [
            "I AM WELL", "I AM DOING WELL",
            "I AM {STATE} {TIME} {ADDRESS}", "I FEEL {STATE} {ADDRESS}",
            "ALL IS WELL {TIME} {ADDRESS}", "I HAVE BEEN {STATE} {ADDRESS}",
            "MY DAY IS GOING WELL {ADDRESS}", "I AM DOING WELL {ADDRESS}",
            "I REMAIN {STATE} {ADDRESS}", "THINGS ARE PEACEFUL {TIME} {ADDRESS}"
        ], Slots(("ADDRESS", Addresses), ("TIME", Times), ("STATE", States))),

        Corpus("IDENTITY",
        [
            "WHO ARE YOU", "WHAT IS YOUR NAME",
            "WHO ARE YOU {ADDRESS}", "WHAT IS YOUR NAME {ADDRESS}",
            "TELL ME WHO YOU ARE {ADDRESS}", "WHAT DO PEOPLE CALL YOU {ADDRESS}",
            "WHAT IS YOUR ROLE {ADDRESS}", "WHO DO YOU SERVE {ADDRESS}",
            "HOW SHOULD I KNOW YOU {ADDRESS}", "WHAT ARE YOU CALLED {ADDRESS}"
        ],
        [
            "I AM A VILLAGER", "I AM THE INNKEEPER",
            "I AM A {ROLE} {ADDRESS}", "PEOPLE KNOW ME AS A {ROLE} {ADDRESS}",
            "I SERVE HERE AS A {ROLE} {ADDRESS}", "MY WORK IS THAT OF A {ROLE} {ADDRESS}",
            "YOU MAY CALL ME THE {ROLE} {ADDRESS}", "I AM THE LOCAL {ROLE} {ADDRESS}",
            "I HAVE LONG BEEN A {ROLE} {ADDRESS}", "MY ROLE IS {ROLE} {ADDRESS}"
        ], Slots(("ADDRESS", Addresses), ("ROLE", Roles))),

        Corpus("ASSISTANCE",
        [
            "CAN YOU HELP ME", "I NEED HELP",
            "CAN YOU HELP ME {ADDRESS}", "WILL YOU HELP ME {ADDRESS}",
            "CAN YOU GIVE ME AID {ADDRESS}", "I NEED YOUR HELP {ADDRESS}",
            "PLEASE HELP ME {ADDRESS}", "MAY I ASK FOR HELP {ADDRESS}",
            "COULD YOU ASSIST ME {ADDRESS}", "I REQUIRE SOME AID {ADDRESS}"
        ],
        [
            "TELL ME WHAT YOU NEED", "I WILL HELP IF I CAN",
            "TELL ME WHAT YOU NEED {ADDRESS}", "I WILL HELP IF I CAN {ADDRESS}",
            "SAY WHAT TROUBLES YOU {ADDRESS}", "YOU MAY ASK FOR MY AID {ADDRESS}",
            "I AM READY TO HELP {ADDRESS}", "EXPLAIN WHAT YOU NEED {ADDRESS}",
            "I WILL DO WHAT I CAN {ADDRESS}", "SPEAK AND I WILL LISTEN {ADDRESS}"
        ], AddressSlots()),

        Corpus("CLARIFICATION",
        [
            "WHAT DO YOU MEAN", "I DO NOT UNDERSTAND",
            "WHAT DO YOU MEAN {ADDRESS}", "CAN YOU EXPLAIN THAT {ADDRESS}",
            "PLEASE SAY THAT AGAIN {ADDRESS}", "I DO NOT UNDERSTAND {ADDRESS}",
            "WHAT ARE YOU SAYING {ADDRESS}", "CAN YOU SPEAK CLEARLY {ADDRESS}",
            "WOULD YOU EXPLAIN AGAIN {ADDRESS}", "I AM CONFUSED {ADDRESS}"
        ],
        [
            "I WILL EXPLAIN", "LET ME SAY IT CLEARLY",
            "I WILL EXPLAIN AGAIN {ADDRESS}", "LET ME SAY IT MORE CLEARLY {ADDRESS}",
            "I WILL SPEAK PLAINLY {ADDRESS}", "LISTEN AND I WILL EXPLAIN {ADDRESS}",
            "I WILL TRY AGAIN {ADDRESS}", "ALLOW ME TO CLARIFY {ADDRESS}",
            "I MEAN ONLY WHAT I SAID {ADDRESS}", "I WILL USE SIMPLER WORDS {ADDRESS}"
        ], AddressSlots()),

        Corpus("ACTIVITY",
        [
            "WHAT IS GOING ON", "WHAT ARE YOU DOING",
            "WHAT ARE YOU DOING {TIME} {ADDRESS}", "WHAT KEEPS YOU BUSY {TIME} {ADDRESS}",
            "WHAT BRINGS YOU HERE {ADDRESS}", "WHAT IS YOUR WORK {TIME} {ADDRESS}",
            "HOW DO YOU SPEND YOUR TIME {ADDRESS}", "WHAT OCCUPIES YOU {TIME} {ADDRESS}",
            "WHAT ARE YOU WORKING ON {ADDRESS}", "WHAT IS HAPPENING HERE {ADDRESS}"
        ],
        [
            "I AM HERE", "NOTHING MUCH IS HAPPENING",
            "I AM {ACTIVITY} {TIME} {ADDRESS}", "I HAVE BEEN {ACTIVITY} {ADDRESS}",
            "MY WORK KEEPS ME {ACTIVITY} {ADDRESS}", "I AM HERE {ACTIVITY} {ADDRESS}",
            "I SPEND MY TIME {ACTIVITY} {ADDRESS}", "NOTHING MORE THAN {ACTIVITY} {ADDRESS}",
            "I REMAIN HERE {ACTIVITY} {ADDRESS}", "FOR NOW I AM {ACTIVITY} {ADDRESS}"
        ], Slots(("ADDRESS", Addresses), ("TIME", Times), ("ACTIVITY", Activities))),

        Corpus("SILENCE",
        [
            "WHY ARE YOU SILENT", "WILL YOU ANSWER ME",
            "WHY ARE YOU SILENT {ADDRESS}", "WHY DO YOU SAY NOTHING {ADDRESS}",
            "HAVE YOU NOTHING TO SAY {ADDRESS}", "WHY WILL YOU NOT SPEAK {ADDRESS}",
            "ARE YOU LISTENING {ADDRESS}", "DID YOU HEAR ME {ADDRESS}",
            "WHY DO YOU IGNORE ME {ADDRESS}", "WILL YOU ANSWER ME {ADDRESS}"
        ],
        [
            "I AM LISTENING", "I WILL ANSWER",
            "I AM LISTENING {ADDRESS}", "I WAS THINKING {ADDRESS}",
            "I HAVE LITTLE TO SAY {ADDRESS}", "I HEARD YOU {ADDRESS}",
            "I DID NOT MEAN TO IGNORE YOU {ADDRESS}", "I WILL ANSWER NOW {ADDRESS}",
            "SILENCE HELPS ME THINK {ADDRESS}", "I AM HERE WITH YOU {ADDRESS}"
        ], AddressSlots()),

        Corpus("GRATITUDE",
        [
            "THANK YOU", "I AM GRATEFUL",
            "THANK YOU {ADDRESS}", "YOU HAVE MY THANKS {ADDRESS}",
            "I AM GRATEFUL {ADDRESS}", "THANK YOU FOR YOUR HELP {ADDRESS}",
            "I APPRECIATE YOUR AID {ADDRESS}", "YOU HAVE HELPED ME {ADDRESS}",
            "I OWE YOU THANKS {ADDRESS}", "MY THANKS TO YOU {ADDRESS}"
        ],
        [
            "YOU ARE WELCOME", "I AM GLAD TO HELP",
            "YOU ARE WELCOME {ADDRESS}", "I AM GLAD TO HELP {ADDRESS}",
            "THINK NOTHING OF IT {ADDRESS}", "YOUR THANKS ARE ENOUGH {ADDRESS}",
            "I WAS HAPPY TO HELP {ADDRESS}", "NO THANKS ARE NEEDED {ADDRESS}",
            "IT WAS MY PLEASURE {ADDRESS}", "WE HELP EACH OTHER {ADDRESS}"
        ], AddressSlots()),

        Corpus("APOLOGY",
        [
            "I AM SORRY", "PLEASE FORGIVE ME",
            "I AM SORRY {ADDRESS}", "PLEASE FORGIVE ME {ADDRESS}",
            "I DID NOT MEAN THAT {ADDRESS}", "I REGRET WHAT I SAID {ADDRESS}",
            "ACCEPT MY APOLOGY {ADDRESS}", "I HAVE WRONGED YOU {ADDRESS}",
            "I ASK YOUR FORGIVENESS {ADDRESS}", "THAT WAS MY MISTAKE {ADDRESS}"
        ],
        [
            "I FORGIVE YOU", "NO HARM WAS DONE",
            "NO HARM WAS DONE {ADDRESS}", "I FORGIVE YOU {ADDRESS}",
            "YOUR APOLOGY IS ACCEPTED {ADDRESS}", "LET US FORGET IT {ADDRESS}",
            "WE CAN MOVE ON {ADDRESS}", "I HOLD NO ANGER {ADDRESS}",
            "BE AT PEACE {ADDRESS}", "THE MATTER IS ENDED {ADDRESS}"
        ], AddressSlots()),

        Corpus("AGREEMENT",
        [
            "DO YOU AGREE", "CAN WE AGREE",
            "DO YOU AGREE {ADDRESS}", "ARE WE OF ONE MIND {ADDRESS}",
            "WILL YOU ACCEPT THIS {ADDRESS}", "DOES THIS SEEM RIGHT {ADDRESS}",
            "CAN WE AGREE {ADDRESS}", "WILL YOU STAND WITH ME {ADDRESS}",
            "IS THAT ACCEPTABLE {ADDRESS}", "DO YOU SHARE MY VIEW {ADDRESS}"
        ],
        [
            "YES I AGREE", "WE ARE OF ONE MIND",
            "YES I AGREE {ADDRESS}", "WE ARE OF ONE MIND {ADDRESS}",
            "I ACCEPT YOUR WORDS {ADDRESS}", "THAT SEEMS RIGHT {ADDRESS}",
            "YOU HAVE MY AGREEMENT {ADDRESS}", "I WILL STAND WITH YOU {ADDRESS}",
            "THAT IS ACCEPTABLE {ADDRESS}", "I SHARE YOUR VIEW {ADDRESS}"
        ], AddressSlots()),

        Corpus("REFUSAL",
        [
            "WILL YOU DO THIS", "CAN YOU MAKE AN EXCEPTION",
            "WILL YOU DO THIS {ADDRESS}", "CAN YOU OBEY THIS REQUEST {ADDRESS}",
            "WILL YOU FOLLOW MY ORDER {ADDRESS}", "CAN I PERSUADE YOU {ADDRESS}",
            "WILL YOU CHANGE YOUR MIND {ADDRESS}", "CAN YOU MAKE AN EXCEPTION {ADDRESS}",
            "WILL YOU GIVE IN {ADDRESS}", "MUST YOU REFUSE {ADDRESS}"
        ],
        [
            "I CANNOT DO THAT", "I MUST REFUSE",
            "I CANNOT DO THAT {ADDRESS}", "I MUST REFUSE {ADDRESS}",
            "I WILL NOT OBEY THAT REQUEST {ADDRESS}", "YOU CANNOT PERSUADE ME {ADDRESS}",
            "MY MIND WILL NOT CHANGE {ADDRESS}", "I CAN MAKE NO EXCEPTION {ADDRESS}",
            "I WILL NOT GIVE IN {ADDRESS}", "MY ANSWER IS NO {ADDRESS}"
        ], AddressSlots())
    ];

    private static IntentCorpus Corpus(
        string name,
        string[] questions,
        string[] answers,
        IReadOnlyDictionary<string, string[]> slots) => new(name, questions, answers, slots);

    private static IReadOnlyDictionary<string, string[]> AddressSlots() => Slots(("ADDRESS", Addresses));

    private static IReadOnlyDictionary<string, string[]> Slots(params (string Name, string[] Values)[] slots) =>
        slots.ToDictionary(x => x.Name, x => x.Values, StringComparer.Ordinal);
}

internal sealed record IntentCorpus(
    string Name,
    string[] Questions,
    string[] Answers,
    IReadOnlyDictionary<string, string[]> Slots);

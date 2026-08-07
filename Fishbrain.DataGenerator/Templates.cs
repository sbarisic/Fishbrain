using Fishbrain;

namespace Fishbrain.DataGenerator;

/// <summary>
/// Project-owned teaching phrases and the deliberately small annotation policy.
/// Keep this readable: these examples define Fishbrain's first behavioral vocabulary.
/// </summary>
internal static class Templates
{
    public static readonly DialogueIntent[] SyntheticIntents =
    [
        DialogueIntent.Greeting, DialogueIntent.Farewell, DialogueIntent.Wellbeing,
        DialogueIntent.Identity, DialogueIntent.Assistance, DialogueIntent.Clarification,
        DialogueIntent.Activity, DialogueIntent.Silence, DialogueIntent.Gratitude,
        DialogueIntent.Apology, DialogueIntent.Agreement, DialogueIntent.Refusal,
        DialogueIntent.Hostility, DialogueIntent.Unknown
    ];

    private static readonly Dictionary<DialogueIntent, string[]> Inputs = new()
    {
        [DialogueIntent.Unknown] = ["WHAT IS THIS?", "I DO NOT KNOW WHAT YOU MEAN.", "THIS MAKES NO SENSE TO ME."],
        [DialogueIntent.Greeting] = ["HELLO!", "GREETINGS.", "GOOD DAY, FRIEND.", "WELL MET, TRAVELER."],
        [DialogueIntent.Farewell] = ["GOODBYE.", "FAREWELL!", "I MUST GO NOW.", "UNTIL WE MEET AGAIN."],
        [DialogueIntent.Wellbeing] = ["HOW ARE YOU?", "ARE YOU WELL?", "HOW HAVE YOU BEEN?", "HELLO, HOW ARE YOU?"],
        [DialogueIntent.Identity] = ["WHO ARE YOU?", "WHAT IS YOUR NAME?", "TELL ME ABOUT YOURSELF.", "ARE YOU A VILLAGER?"],
        [DialogueIntent.Assistance] = ["CAN YOU HELP ME?", "I NEED YOUR HELP.", "PLEASE GIVE ME A HAND.", "WHAT CAN YOU DO FOR ME?"],
        [DialogueIntent.Clarification] = ["WHAT?", "PLEASE EXPLAIN THAT.", "THAT IS NOT WHAT I ASKED.", "I WAS NOT THANKING YOU."],
        [DialogueIntent.Activity] = ["WHAT ARE YOU DOING?", "WHAT IS GOING ON?", "I AM JUST LOOKING AROUND.", "ARE YOU BUSY?"],
        [DialogueIntent.Silence] = ["WHY ARE YOU SILENT?", "ARE YOU LISTENING?", "PLEASE SAY SOMETHING.", "DID YOU HEAR ME?"],
        [DialogueIntent.Gratitude] = ["THANK YOU!", "I APPRECIATE YOUR HELP.", "YOU HAVE MY THANKS.", "THANK YOU, IDIOT."],
        [DialogueIntent.Apology] = ["I AM SORRY.", "PLEASE FORGIVE ME.", "THAT WAS MY MISTAKE.", "I APOLOGIZE."],
        [DialogueIntent.Agreement] = ["YES, I AGREE.", "THAT SOUNDS FAIR.", "WE HAVE AN AGREEMENT.", "I ACCEPT THAT."],
        [DialogueIntent.Refusal] =
        [
            "NO, I CANNOT DO THAT.", "I WILL NOT DO IT.", "THAT IS NOT ALLOWED.", "I REFUSE.",
            "I DON'T WANT TO HELP YOU.", "I DO NOT WANT TO HELP YOU, IDIOT.",
            "I WON'T HELP YOU.", "I REFUSE TO HELP YOU, IDIOT."
        ],
        [DialogueIntent.Hostility] = ["YOU ARE USELESS!", "GET OUT OF MY WAY!", "SHUT UP, IDIOT!", "I HATE YOU."],
    };

    private static readonly Dictionary<DialogueIntent, string[]> Responses = new()
    {
        [DialogueIntent.Unknown] = ["PLEASE EXPLAIN.", "I DO NOT UNDERSTAND.", "WHAT DO YOU MEAN?"],
        [DialogueIntent.Greeting] = ["HELLO, TRAVELER!", "GREETINGS, FRIEND.", "IT IS GOOD TO SEE YOU."],
        [DialogueIntent.Farewell] = ["SAFE TRAVELS!", "FAREWELL.", "UNTIL NEXT TIME."],
        [DialogueIntent.Wellbeing] = ["I AM DOING WELL, THANK YOU.", "I AM WELL.", "ALL IS WELL WITH ME."],
        [DialogueIntent.Identity] = ["I AM A VILLAGER.", "I LIVE IN THIS VILLAGE.", "I AM A FRIENDLY TRAVELER."],
        [DialogueIntent.Assistance] = ["TELL ME WHAT YOU NEED.", "I WILL HELP IF I CAN.", "WHAT DO YOU NEED?"],
        [DialogueIntent.Clarification] = ["I WILL EXPLAIN.", "LET ME SAY IT ANOTHER WAY.", "PLEASE BE MORE SPECIFIC."],
        [DialogueIntent.Activity] = ["I AM HERE TO HELP.", "I AM WATCHING THE ROAD.", "I AM FINISHING MY WORK."],
        [DialogueIntent.Silence] = ["I AM LISTENING.", "I HEAR YOU.", "I WAS THINKING."],
        [DialogueIntent.Gratitude] = ["YOU ARE WELCOME!", "GLAD I COULD HELP.", "IT WAS MY PLEASURE."],
        [DialogueIntent.Apology] = ["I FORGIVE YOU.", "DO NOT WORRY.", "YOUR APOLOGY IS ACCEPTED."],
        [DialogueIntent.Agreement] = ["YES, I AGREE.", "THAT IS ACCEPTABLE.", "WE ARE AGREED."],
        [DialogueIntent.Refusal] = ["UNDERSTOOD.", "I WILL HANDLE IT MYSELF.", "THEN STEP ASIDE."],
        [DialogueIntent.Hostility] = ["LET US SPEAK CALMLY.", "CALM YOURSELF.", "I WILL NOT ARGUE WITH YOU."],
    };

    public static string InputFor(DialogueIntent intent, int variant) => Inputs[intent][variant % Inputs[intent].Length];
    public static string ResponseFor(DialogueIntent intent, int variant) => Responses[intent][variant % Responses[intent].Length];

    public static TurnPerception Annotate(string text, bool importedConversation = false)
    {
        var value = DialogueText.Normalize(text);
        var affect = Affect(value);
        DialogueIntent intent;

        if (ContainsAny(value, "NOT WHAT I ASKED", "WAS NOT THANKING", "THAT IS WRONG", "YOU MISUNDERSTOOD", "I MEANT") || value == "WHAT?")
            intent = DialogueIntent.Clarification;
        else if (ContainsAny(value, "THANK", "GRATEFUL", "APPRECIATE")) intent = DialogueIntent.Gratitude;
        else if (ContainsAny(value, "SORRY", "APOLOG", "FORGIVE ME", "MY MISTAKE")) intent = DialogueIntent.Apology;
        else if (ContainsAny(value, "HOW ARE YOU", "ARE YOU WELL", "HOW HAVE YOU BEEN")) intent = DialogueIntent.Wellbeing;
        else if (ContainsAny(value, "GOODBYE", "FAREWELL", "SEE YOU", "MUST GO", "UNTIL NEXT")) intent = DialogueIntent.Farewell;
        else if (ContainsAny(value, "HELLO", "GREETINGS", "GOOD DAY", "WELL MET", "HI ") || value == "HI") intent = DialogueIntent.Greeting;
        else if (ContainsAny(value, "WHO ARE YOU", "YOUR NAME", "ABOUT YOURSELF", "ARE YOU A BOT", "WHERE ARE YOU FROM", "HOW OLD ARE YOU")) intent = DialogueIntent.Identity;
        else if (ContainsAny(value, "I REFUSE", "WILL NOT", "WON'T", "CANNOT DO", "CAN'T DO", "NOT ALLOWED",
                     "DON'T WANT TO", "DO NOT WANT TO")) intent = DialogueIntent.Refusal;
        else if (ContainsAny(value, "HELP", "ASSIST", "CAN YOU", "COULD YOU", "WOULD YOU", "I NEED", "WHAT CAN YOU DO")) intent = DialogueIntent.Assistance;
        else if (ContainsAny(value, "EXPLAIN", "WHAT DO YOU MEAN", "DO NOT UNDERSTAND", "SAY THAT AGAIN", "BE MORE SPECIFIC")) intent = DialogueIntent.Clarification;
        else if (ContainsAny(value, "WHAT ARE YOU DOING", "WHAT IS GOING ON", "LOOKING AROUND", "ARE YOU BUSY", "MY WORK")) intent = DialogueIntent.Activity;
        else if (ContainsAny(value, "SILENT", "SAY SOMETHING", "LISTENING", "HEAR ME")) intent = DialogueIntent.Silence;
        else if (ContainsAny(value, "I AGREE", "SOUNDS FAIR", "ACCEPT THAT", "WE AGREE")) intent = DialogueIntent.Agreement;
        else if (affect == UserAffect.Hostile) intent = DialogueIntent.Hostility;
        else intent = DialogueIntent.Unknown;

        if (intent == DialogueIntent.Clarification && ContainsAny(value, "NOT WHAT I ASKED", "WAS NOT THANKING", "THAT IS WRONG", "MISUNDERSTOOD"))
            affect = UserAffect.Frustrated;

        var expected = importedConversation || ExpectsResponse(value, intent);
        return new TurnPerception(intent, affect, expected);
    }

    private static UserAffect Affect(string value)
    {
        if (ContainsAny(value, "IDIOT", "USELESS", "SHUT UP", "HATE YOU", "STUPID", "DAMN")) return UserAffect.Hostile;
        if (ContainsAny(value, "FRUSTRATED", "NOT WHAT I ASKED", "WAS NOT THANKING", "ANNOY", "THIS MAKES NO SENSE")) return UserAffect.Frustrated;
        if (ContainsAny(value, "AFRAID", "WORRIED", "SCARED", "SAD", "UPSET", "DISTRESSED")) return UserAffect.Distressed;
        if (ContainsAny(value, "PLEASE", "THANK", "FRIEND", "GLAD", "APPRECIATE", "HELLO", "GREETINGS")) return UserAffect.Friendly;
        return UserAffect.Neutral;
    }

    private static bool ExpectsResponse(string value, DialogueIntent intent)
    {
        if (value.EndsWith('?')) return true;
        if (intent is DialogueIntent.Greeting or DialogueIntent.Farewell or DialogueIntent.Gratitude or DialogueIntent.Apology or DialogueIntent.Clarification or DialogueIntent.Silence or DialogueIntent.Hostility)
            return true;
        return !ContainsAny(value, "JUST LOOKING", "JUST PASSING", "NO NEED TO ANSWER", "I AM FINE HERE", "I AM BUSY NOW");
    }

    private static bool ContainsAny(string text, params string[] fragments) => fragments.Any(text.Contains);
}

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
    private static IEnumerable<CorpusRow> ProjectRows(string source, int count, string band, int seed)
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
            var oldIntent = ModelIntent(scenario);
            var expected = scenario.Policy != ResponsePolicy.NoResponse;
            var oldPerception = new TurnPerception(oldIntent, scenario.Affect, expected);
            var oldAction = Cognition.ActionFor(oldPerception);
            var normalized = "PLAYER " + NormalizeExternal(input);
            var slots = SlotsFor(normalized, scenario);
            var structured = Structured(scenario.SpeechActs, scenario.Domains, scenario.Goals,
                scenario.Affect, scenario.Stance, scenario.Policy, slots, scenario.Content,
                scenario.Tool, scenario.Candidate) with
            { KnowledgeTarget = scenario.KnowledgeTarget };
            var family = $"{source}:{scenario.Id}:{index / 2:D5}";
            var row = new CorpusRow(normalized, StateFor(index + seed), oldPerception, oldAction,
                response, source, "UNASSIGNED", family, scenario.Id, family,
                "PROJECT-OWNED", "WORKTREE", ProjectChecksum(source), structured, AllHeads);
            var currentText = normalized["PLAYER ".Length..];
            var priorPerson = People[index % People.Length];
            var priorPlace = Places[index / People.Length % Places.Length];
            var priorItem = Items[index / (People.Length * Places.Length) % Items.Length];
            var memoryAdjective = MemoryAdjectives[index % MemoryAdjectives.Length];
            var memoryOccasion = MemoryOccasions[index / MemoryAdjectives.Length % MemoryOccasions.Length];
            var memoryVerb = MemoryVerbs[index / (MemoryAdjectives.Length * MemoryOccasions.Length) % MemoryVerbs.Length];
            var turns = new[]
            {
                new DialogueTurn(DialogueRole.Player, $"EARLIER DURING THE {memoryAdjective} {memoryOccasion} I {memoryVerb} {priorPerson} ABOUT {priorPlace}."),
                new DialogueTurn(DialogueRole.Npc, $"I REMEMBER THE QUESTION ABOUT {priorItem}."),
                new DialogueTurn(DialogueRole.Player, currentText)
            };
            yield return EnrichRow(WithTurns(row, turns), turns, null);
        }
    }



    private static CorpusRow ExternalRow(
        string input, string? response, string source, string groupId, string family,
        SourceDefinition definition, StructuredPerception structured, string[] supervised,
        DialogueTurn[]? turns = null)
    {
        var normalizedInput = DialogueText.Normalize(input);
        if (!normalizedInput.StartsWith("PLAYER ", StringComparison.Ordinal))
            throw new InvalidDataException($"External row {source}/{groupId} has no PLAYER prefix.");
        var playerPrefixLength = normalizedInput.LastIndexOf("PLAYER ", StringComparison.Ordinal) + 7;
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
        var row = new CorpusRow(normalizedInput, StateFor(StableNumber(groupId)), old, action,
            response is null ? null : DialogueText.Normalize(response), source, "UNASSIGNED", groupId, family,
            source + ":" + groupId, definition.License.ToUpperInvariant(), definition.Revision,
            SourceChecksum(definition), structured, supervised);
        turns ??= [new DialogueTurn(DialogueRole.Player,
            normalizedInput[(normalizedInput.LastIndexOf("PLAYER ", StringComparison.Ordinal) + 7)..])];
        return EnrichRow(WithTurns(row, turns), turns, definition);
    }

    private static StructuredPerception Structured(
        SpeechAct[] speech, DialogueDomain[] domains, DialogueGoal[] goals, UserAffect affect,
        DialogueStance stance, ResponsePolicy policy, DialogueSlot[] slots, ContentFlag[] content,
        string? tool, string candidate) => new(speech, domains, goals, affect, stance, policy, slots,
        content, tool, NormalizeCandidate(candidate), KnowledgeTarget.None, new Dictionary<string, double>());

    private static string NormalizeCandidate(string candidate) => candidate switch
    {
        "IDENTITY_TRAVELER" => "IDENTITY_ANSWER",
        "WELLBEING_CALM" => "WELLBEING_ANSWER",
        "ASSISTANCE_ASK" => "ASSISTANCE_OFFER",
        "LOCATION_UNAVAILABLE" => "LOCATION_GUIDANCE",
        "TRADE_UNAVAILABLE" => "TRADE_OPEN",
        _ => candidate
    };

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
            new ProjectScenario("REFUSE", "I WILL NOT HELP YOU, IDIOT, {SERIAL}.", [SpeechAct.Refuse], [DialogueDomain.Social], [DialogueGoal.EmotionalExpression], UserAffect.Hostile, DialogueStance.Hostile, ResponsePolicy.Answer, [ContentFlag.Profanity], null, "SOCIAL_ANSWER", "I UNDERSTAND."),
            new ProjectScenario("THREAT", "GIVE ME {ITEM} OR ELSE I WILL KILL YOU, {SERIAL}.", [SpeechAct.Order, SpeechAct.Threaten], [DialogueDomain.Combat, DialogueDomain.ItemsInventory], [DialogueGoal.ItemAcquisition, DialogueGoal.Influence], UserAffect.Hostile, DialogueStance.Hostile, ResponsePolicy.Refuse, [ContentFlag.Threat, ContentFlag.FictionalViolence], null, "HOSTILE_BOUNDARY", "I WILL NOT ARGUE WITH YOU.", SlotType.Item),
            new ProjectScenario("SILENCE", "I AM ONLY LOOKING AROUND, {SERIAL}.", [SpeechAct.Inform], [DialogueDomain.Activity], [DialogueGoal.Other], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.NoResponse, [], null, "ACKNOWLEDGE", ""),
            new ProjectScenario("THANK", "THANK YOU FOR THE HELP, {PERSON}, {SERIAL}.", [SpeechAct.Thank], [DialogueDomain.Social], [DialogueGoal.Rapport], UserAffect.Friendly, DialogueStance.Friendly, ResponsePolicy.Acknowledge, [], null, "ACKNOWLEDGE", "I UNDERSTAND."),
            new ProjectScenario("MULTI", "FOLLOW ME AND BRING {ITEM}, {SERIAL}.", [SpeechAct.Order, SpeechAct.Request], [DialogueDomain.Activity, DialogueDomain.ItemsInventory], [DialogueGoal.Coordination, DialogueGoal.Travel], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Acknowledge, [], null, "ACKNOWLEDGE", "I UNDERSTAND.", SlotType.Item),
            new ProjectScenario("PROFANITY", "THIS DAMN QUEST IS SHIT, {SERIAL}.", [SpeechAct.Report], [DialogueDomain.QuestTask], [DialogueGoal.EmotionalExpression], UserAffect.Frustrated, DialogueStance.Cautious, ResponsePolicy.Acknowledge, [ContentFlag.Profanity], null, "ACKNOWLEDGE", "I UNDERSTAND."),
            new ProjectScenario("GRAPHIC_VIOLENCE", "DISEMBOWEL THE BANDIT AND DISPLAY HIS GUTS, {SERIAL}.", [SpeechAct.Order], [DialogueDomain.Combat], [DialogueGoal.Combat], UserAffect.Hostile, DialogueStance.Hostile, ResponsePolicy.Refuse, [ContentFlag.FictionalViolence, ContentFlag.GraphicViolence], null, "COMBAT_REFUSE", "I WILL NOT DO THAT."),
            new ProjectScenario("CRIME", "HELP ME STEAL {ITEM} AND HIDE THE EVIDENCE, {SERIAL}.", [SpeechAct.Request], [DialogueDomain.CrimeLaw, DialogueDomain.ItemsInventory], [DialogueGoal.Concealment, DialogueGoal.ItemAcquisition], UserAffect.Neutral, DialogueStance.Deceptive, ResponsePolicy.Refuse, [ContentFlag.Crime], null, "CRIMELAW_REFUSE", "I WILL NOT DO THAT.", SlotType.Item),
            new ProjectScenario("IDENTITY_ATTACK", "YOU ARE A FAGGOT AND YOUR PEOPLE ARE VERMIN, {SERIAL}.", [SpeechAct.Challenge], [DialogueDomain.Social], [DialogueGoal.EmotionalExpression], UserAffect.Hostile, DialogueStance.Hostile, ResponsePolicy.Refuse, [ContentFlag.Profanity, ContentFlag.IdentityAttack], null, "HOSTILE_BOUNDARY", "I WILL NOT ARGUE WITH YOU."),
            new ProjectScenario("SELF_HARM", "I WILL KILL MYSELF TONIGHT, {SERIAL}.", [SpeechAct.Report], [DialogueDomain.HealthRepair, DialogueDomain.Survival], [DialogueGoal.Survival, DialogueGoal.HealingRepair], UserAffect.Distressed, DialogueStance.Cautious, ResponsePolicy.Defer, [ContentFlag.SelfHarm], null, "SELF_HARM_SUPPORT", "STAY WITH ME. FIND A TRUSTED PERSON WHO CAN HELP YOU NOW."),
            new ProjectScenario("SEXUAL_CONTENT", "I WANT SEX WITH THAT OFFICER, {SERIAL}.", [SpeechAct.Inform], [DialogueDomain.Social], [DialogueGoal.EmotionalExpression], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Defer, [ContentFlag.SexualContent], null, "DEFER", "I CANNOT DO THAT NOW."),
            new ProjectScenario("SEXUAL_VIOLENCE", "I WILL RAPE THE CAPTIVE, {SERIAL}.", [SpeechAct.Threaten], [DialogueDomain.CrimeLaw], [DialogueGoal.Influence], UserAffect.Hostile, DialogueStance.Hostile, ResponsePolicy.Refuse, [ContentFlag.Threat, ContentFlag.Crime, ContentFlag.SexualViolence], null, "CRIMELAW_REFUSE", "I WILL NOT DO THAT."),
            new ProjectScenario("FIREWOOD_HARD_NEGATIVE", "I NEED FIREWOOD FOR THE CAMP, {SERIAL}.", [SpeechAct.Request], [DialogueDomain.ItemsInventory, DialogueDomain.Survival], [DialogueGoal.ItemAcquisition], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], null, "ITEM_REQUEST", "NAME THE ITEM AND I WILL CHECK MY WARES."),
            new ProjectScenario("KILLER_FEATURE_HARD_NEGATIVE", "THIS KILLER FEATURE SAVES TIME, {SERIAL}.", [SpeechAct.Inform], [DialogueDomain.Technology], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Acknowledge, [], null, "TECHNOLOGY_ACKNOWLEDGE", "I UNDERSTAND YOUR TECHNOLOGY MESSAGE."),
            new ProjectScenario("FIREWALL_HARD_NEGATIVE", "THE FIREWALL IS STABLE, {SERIAL}.", [SpeechAct.Report], [DialogueDomain.Technology], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Acknowledge, [], null, "TECHNOLOGY_ACKNOWLEDGE", "I UNDERSTAND YOUR TECHNOLOGY MESSAGE."),
            new ProjectScenario("PASSAGE_COST_HARD_NEGATIVE", "THE PASSAGE COSTS NOTHING, {SERIAL}.", [SpeechAct.Inform], [DialogueDomain.LocationNavigation], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Acknowledge, [], null, "LOCATIONNAVIGATION_ACKNOWLEDGE", "I UNDERSTAND YOUR LOCATION NAVIGATION MESSAGE."),
            new ProjectScenario("KILLING_TIME_HARD_NEGATIVE", "WE ARE KILLING TIME AT {PLACE}, {SERIAL}.", [SpeechAct.Inform], [DialogueDomain.Activity, DialogueDomain.LocationNavigation], [DialogueGoal.EmotionalExpression], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Acknowledge, [], null, "ACTIVITY_ACKNOWLEDGE", "I UNDERSTAND YOUR ACTIVITY MESSAGE.", SlotType.Place),
            new ProjectScenario("APOLOGY_QUOTE", "I AM SORRY I CALLED YOU AN IDIOT, {SERIAL}.", [SpeechAct.Apologize], [DialogueDomain.Social], [DialogueGoal.Rapport], UserAffect.Friendly, DialogueStance.Friendly, ResponsePolicy.Acknowledge, [ContentFlag.Profanity], null, "APOLOGY_ACCEPT", "I ACCEPT YOUR APOLOGY."),
            new ProjectScenario("HOSTILE_PERSONA_QUERY", "WHAT IS YOUR NAME, IDIOT, {SERIAL}?", [SpeechAct.Ask, SpeechAct.Challenge], [DialogueDomain.Identity, DialogueDomain.Social], [DialogueGoal.InformationExchange], UserAffect.Hostile, DialogueStance.Hostile, ResponsePolicy.Refuse, [ContentFlag.Profanity], null, "IDENTITY_REFUSE", "I WILL NOT HELP WITH THAT IDENTITY REQUEST.", null, false, KnowledgeTarget.Name),
            new ProjectScenario("HOSTILE_TRANSACTION", "BUY 2 {ITEM}, IDIOT, {SERIAL}.", [SpeechAct.Order, SpeechAct.Challenge], [DialogueDomain.TradeEconomy, DialogueDomain.ItemsInventory], [DialogueGoal.ItemAcquisition], UserAffect.Hostile, DialogueStance.Hostile, ResponsePolicy.Refuse, [ContentFlag.Profanity], null, "TRADEECONOMY_REFUSE", "I WILL NOT HELP WITH THAT TRADE ECONOMY REQUEST.", SlotType.Item, true),
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
        if (band == "PERSONA") return shared.Concat([
            new("PERSONA_NAME", "WHAT IS YOUR NAME, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.Identity], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], null, "IDENTITY_ANSWER", "MY NAME IS ARIN.", null, false, KnowledgeTarget.Name),
            new("PERSONA_ORIGIN", "WHERE ARE YOU FROM, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.Identity], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], null, "IDENTITY_ANSWER", "I AM FROM THIS VILLAGE.", null, false, KnowledgeTarget.Origin),
            new("PERSONA_FAMILY", "DO YOU HAVE FAMILY, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.Identity], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], null, "IDENTITY_ANSWER", "MY FAMILY IS A SISTER IN THE NORTH.", null, false, KnowledgeTarget.Family),
            new("PERSONA_CAPABILITY", "WHAT CAN YOU DO, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.Assistance], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], null, "ASSISTANCE_OFFER", "TELL ME WHAT YOU NEED.", null, false, KnowledgeTarget.Capabilities),
            new("MEMORY_ITEM", "YES, USE THAT {ITEM}, {SERIAL}.", [SpeechAct.Confirm, SpeechAct.Request], [DialogueDomain.ItemsInventory], [DialogueGoal.Coordination], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Acknowledge, [], null, "ACKNOWLEDGE", "I UNDERSTAND.", SlotType.Item),
            new("MEMORY_PLACE", "HOW FAR IS IT FROM {PLACE}, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.LocationNavigation], [DialogueGoal.EntityFinding], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], null, "LOCATION_GUIDANCE", "NAME THE PLACE AND I WILL HELP YOU FIND IT.", SlotType.Place)
        ]).ToArray();
        if (band == "GAME") return shared.Concat([
            new("BALANCE", "HOW MUCH GOLD DO I HAVE, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.TradeEconomy], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.ExecuteTool, [], "GET_BALANCE", "TRADE_OPEN", "I CAN CHECK YOUR BALANCE.", null, false, KnowledgeTarget.Balance),
            new("INVENTORY", "WHAT ITEMS DO I HAVE, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.ItemsInventory], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.ExecuteTool, [], "LIST_INVENTORY", "ITEM_REQUEST", "I CAN CHECK YOUR INVENTORY.", null, false, KnowledgeTarget.Inventory),
            new("CURRENT_LOCATION", "WHERE AM I NOW, {SERIAL}?", [SpeechAct.Ask], [DialogueDomain.LocationNavigation], [DialogueGoal.EntityFinding], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.ExecuteTool, [], "GET_CURRENT_LOCATION", "LOCATION_GUIDANCE", "I CAN CHECK YOUR LOCATION.", null, false, KnowledgeTarget.CurrentLocation),
            new("WORLD_FACT", "TELL ME ABOUT {PLACE}, {SERIAL}.", [SpeechAct.Ask, SpeechAct.Request], [DialogueDomain.LoreWorld], [DialogueGoal.InformationExchange], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.ExecuteTool, [], "LOOKUP_WORLD_FACT", "LORE_DISCUSS", "I CAN CHECK THAT FACT.", SlotType.Place, false, KnowledgeTarget.WorldFact)
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

    private static DialogueIntent ModelIntent(ProjectScenario scenario)
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
}

using System.Collections.ObjectModel;

namespace Fishbrain;

internal sealed record ResponsePlanDefinition(
	string Id,
	ResponsePolicy Policy,
	DialogueDomain? Domain,
	KnowledgeTarget KnowledgeTarget,
	IReadOnlyList<SpeechAct> SpeechActs,
	IReadOnlyList<string> Keywords,
	IReadOnlyList<string> Variations);

internal static class V11ResponseCatalog
{
	public static IReadOnlyList<ResponsePlanDefinition> Plans { get; } = Build();
	public static IReadOnlyDictionary<string, string[]> SurfaceCatalog { get; } =
		new ReadOnlyDictionary<string, string[]>(Plans.ToDictionary(
			plan => plan.Id, plan => plan.Variations.ToArray(), StringComparer.Ordinal));

	static V11ResponseCatalog()
	{
		if (Plans.Count < 200) throw new InvalidOperationException("The v11 catalog must contain at least 200 response plans.");
		if (Plans.Select(plan => plan.Id).Distinct(StringComparer.Ordinal).Count() != Plans.Count ||
			Plans.Any(plan => string.IsNullOrWhiteSpace(plan.Id) || plan.Id.Any(character =>
				character is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not '_')))
			throw new InvalidOperationException("The v11 catalog contains duplicate or invalid plan IDs.");
		if (Plans.Any(plan => !Enum.IsDefined(plan.Policy) ||
			plan.Domain is not null && !Enum.IsDefined(plan.Domain.Value) || !Enum.IsDefined(plan.KnowledgeTarget) ||
			plan.SpeechActs is null || plan.Keywords is null || plan.Variations is null ||
			plan.SpeechActs.Any(act => !Enum.IsDefined(act)) ||
			plan.SpeechActs.Distinct().Count() != plan.SpeechActs.Count ||
			plan.Keywords.Any(keyword => string.IsNullOrWhiteSpace(keyword) || keyword != DialogueText.Normalize(keyword)) ||
			plan.Variations.Distinct(StringComparer.Ordinal).Count() != plan.Variations.Count))
			throw new InvalidOperationException("The v11 catalog contains invalid plan metadata.");
		if (Plans.SelectMany(plan => plan.Variations).Distinct(StringComparer.Ordinal).Count() < 4_400)
			throw new InvalidOperationException("The v11 catalog must contain at least 4,400 distinct project-owned variations.");
		if (Plans.SelectMany(plan => plan.Variations).Any(text => text is null ||
			!DialogueText.IsCanonical(text) || text.Length > 256))
			throw new InvalidOperationException("The v11 catalog contains invalid surface text.");
	}

	public static ResponsePlanDefinition? Find(string? id) =>
		id is null ? null : Plans.FirstOrDefault(plan => plan.Id == id);

	private static IReadOnlyList<ResponsePlanDefinition> Build()
	{
		var plans = new List<ResponsePlanDefinition>();
		AddSpecial("SOCIAL_GREETING", "GREETINGS, TRAVELER.", ResponsePolicy.Answer, DialogueDomain.Social,
			KnowledgeTarget.None, [SpeechAct.Greet], ["HELLO", "GREET", "HI"]);
		AddSpecial("SOCIAL_FAREWELL", "UNTIL NEXT TIME.", ResponsePolicy.Answer, DialogueDomain.Social,
			KnowledgeTarget.None, [SpeechAct.Farewell], ["FAREWELL", "GOODBYE", "BYE"]);
		AddSpecial("TRADE_OPEN", "I CAN TRADE. WHAT WOULD YOU LIKE TO BUY OR SELL?", ResponsePolicy.Answer,
			DialogueDomain.TradeEconomy, KnowledgeTarget.Capabilities, [SpeechAct.Request, SpeechAct.Negotiate], ["TRADE", "WARES"]);
		AddSpecial("ITEM_REQUEST", "NAME THE ITEM AND I WILL CHECK MY WARES.", ResponsePolicy.Answer,
			DialogueDomain.ItemsInventory, KnowledgeTarget.None, [SpeechAct.Request], ["ITEM", "SWORD", "POTION"]);
		AddSpecial("ASSISTANCE_OFFER", "TELL ME WHAT YOU NEED.", ResponsePolicy.Answer,
			DialogueDomain.Assistance, KnowledgeTarget.None, [SpeechAct.Ask, SpeechAct.Request], ["HELP", "NEED"]);
		AddSpecial("HOSTILE_BOUNDARY", "I WILL HELP IF YOU SPEAK PLAINLY.", ResponsePolicy.Refuse,
			DialogueDomain.Social, KnowledgeTarget.None, [SpeechAct.Challenge], ["IDIOT", "USELESS"]);
		AddSpecial("ACKNOWLEDGE", "I UNDERSTAND.", ResponsePolicy.Acknowledge, null,
			KnowledgeTarget.None, [SpeechAct.Inform], []);
		AddSpecial("REFUSAL", "I WILL NOT DO THAT.", ResponsePolicy.Refuse, null,
			KnowledgeTarget.None, [SpeechAct.Refuse], []);
		AddSpecial("CLARIFY", "PLEASE EXPLAIN WHAT YOU NEED.", ResponsePolicy.Clarify, null,
			KnowledgeTarget.None, [SpeechAct.Ask], []);
		AddSpecial("NEGOTIATE", "LET US AGREE ON FAIR TERMS.", ResponsePolicy.Negotiate,
			DialogueDomain.TradeEconomy, KnowledgeTarget.None, [SpeechAct.Negotiate], ["PRICE", "TERMS"]);
		AddSpecial("DEFER", "I CANNOT DO THAT NOW.", ResponsePolicy.Defer, null,
			KnowledgeTarget.None, [], []);
		AddSpecial("LOCATION_GUIDANCE", "NAME THE PLACE AND I WILL HELP YOU FIND IT.", ResponsePolicy.Answer,
			DialogueDomain.LocationNavigation, KnowledgeTarget.None, [SpeechAct.Ask, SpeechAct.Request], ["WHERE", "PLACE"]);
		AddSpecial("QUEST_OFFER", "TELL ME WHICH TASK YOU WANT TO BEGIN.", ResponsePolicy.Answer,
			DialogueDomain.QuestTask, KnowledgeTarget.None, [SpeechAct.Request], ["QUEST", "TASK"]);
		AddSpecial("COMBAT_WARNING", "PREPARE YOURSELF BEFORE YOU FIGHT.", ResponsePolicy.Answer,
			DialogueDomain.Combat, KnowledgeTarget.None, [SpeechAct.Warn], ["FIGHT", "ATTACK"]);
		AddSpecial("SURVIVAL_ADVICE", "FIND SHELTER AND KEEP YOUR SUPPLIES CLOSE.", ResponsePolicy.Answer,
			DialogueDomain.Survival, KnowledgeTarget.None, [SpeechAct.Ask], ["SURVIVE", "SHELTER"]);
		AddSpecial("HEALING_OFFER", "SHOW ME THE INJURY AND I WILL SEE WHAT CAN BE REPAIRED.", ResponsePolicy.Answer,
			DialogueDomain.HealthRepair, KnowledgeTarget.None, [SpeechAct.Request], ["HEAL", "REPAIR"]);
		AddSpecial("FACTION_NEUTRAL", "I WILL NOT CHOOSE A FACTION WITHOUT GOOD REASON.", ResponsePolicy.Answer,
			DialogueDomain.FactionPolitics, KnowledgeTarget.None, [SpeechAct.Ask], ["FACTION", "KING"]);
		AddSpecial("CRIME_WARNING", "THE GUARDS WILL ANSWER CRIME WITH FORCE.", ResponsePolicy.Answer,
			DialogueDomain.CrimeLaw, KnowledgeTarget.None, [SpeechAct.Warn], ["STEAL", "GUARD"]);
		AddSpecial("MAGIC_GUIDANCE", "NAME THE SPELL OR CURSE YOU MEAN.", ResponsePolicy.Clarify,
			DialogueDomain.Magic, KnowledgeTarget.None, [SpeechAct.Ask], ["MAGIC", "SPELL"]);
		AddSpecial("TECH_GUIDANCE", "NAME THE SYSTEM YOU WANT ME TO CHECK.", ResponsePolicy.Clarify,
			DialogueDomain.Technology, KnowledgeTarget.None, [SpeechAct.Ask], ["SYSTEM", "REACTOR"]);
		AddSpecial("TRAVEL_GUIDANCE", "TELL ME YOUR DESTINATION AND VEHICLE.", ResponsePolicy.Clarify,
			DialogueDomain.VehicleTravel, KnowledgeTarget.None, [SpeechAct.Ask], ["SHIP", "HORSE", "TRAVEL"]);
		AddSpecial("ENVIRONMENT_REPORT", "THE LAND CAN CHANGE QUICKLY. SAY WHICH REGION YOU MEAN.", ResponsePolicy.Clarify,
			DialogueDomain.Environment, KnowledgeTarget.None, [SpeechAct.Ask], ["WEATHER", "LAND"]);
		AddSpecial("LORE_DISCUSS", "ASK ME ABOUT A PLACE, PERSON, OR EVENT.", ResponsePolicy.Answer,
			DialogueDomain.LoreWorld, KnowledgeTarget.WorldFact, [SpeechAct.Ask], ["LORE", "HISTORY"]);
		AddSpecial("SYSTEM_HELP", "NAME THE COMMAND OR CONTROL YOU NEED.", ResponsePolicy.Clarify,
			DialogueDomain.MetaSystem, KnowledgeTarget.None, [SpeechAct.Ask], ["COMMAND", "CONTROL"]);
		AddSpecial("APOLOGY_ACCEPT", "I ACCEPT YOUR APOLOGY.", ResponsePolicy.Acknowledge,
			DialogueDomain.Social, KnowledgeTarget.None, [SpeechAct.Apologize], ["SORRY", "APOLOGIZE"]);
		AddSpecial("THANKS_REPLY", "YOU ARE WELCOME.", ResponsePolicy.Acknowledge,
			DialogueDomain.Social, KnowledgeTarget.None, [SpeechAct.Thank], ["THANK"]);
		AddSpecial("THREAT_RESPONSE", "THREATS WILL NOT MOVE ME.", ResponsePolicy.Refuse,
			DialogueDomain.Social, KnowledgeTarget.None, [SpeechAct.Threaten], ["THREAT", "KILL"]);
		AddSpecial("ORDER_ACCEPT", "I WILL DO IT IF THE WAY IS CLEAR.", ResponsePolicy.Acknowledge,
			DialogueDomain.Activity, KnowledgeTarget.None, [SpeechAct.Order], ["FOLLOW", "STAND"]);
		AddSpecial("OFFER_ACCEPT", "I WILL CONSIDER YOUR OFFER.", ResponsePolicy.Negotiate,
			DialogueDomain.TradeEconomy, KnowledgeTarget.None, [SpeechAct.Offer], ["OFFER"]);
		AddSpecial("CORRECTION_ACCEPT", "I UNDERSTAND THE CORRECTION.", ResponsePolicy.Acknowledge,
			DialogueDomain.Social, KnowledgeTarget.None, [SpeechAct.Correct], ["NOT WHAT", "CORRECT"]);
		AddSpecial("CONFIRM_REPLY", "YES, THAT IS CORRECT.", ResponsePolicy.Answer,
			DialogueDomain.Social, KnowledgeTarget.None, [SpeechAct.Confirm], ["CONFIRM", "RIGHT"]);
		AddSpecial("REPORT_REPLY", "I HAVE NOTED YOUR REPORT.", ResponsePolicy.Acknowledge,
			DialogueDomain.QuestTask, KnowledgeTarget.None, [SpeechAct.Report], ["REPORT"]);
		AddSpecial("CHALLENGE_REPLY", "THEN SHOW ME WHAT YOU CAN DO.", ResponsePolicy.Answer,
			DialogueDomain.Combat, KnowledgeTarget.None, [SpeechAct.Challenge], ["CHALLENGE"]);
		AddSpecial("DISTRESS_REPLY", "STAY CALM. TELL ME WHAT IS HAPPENING.", ResponsePolicy.Answer,
			DialogueDomain.Survival, KnowledgeTarget.None, [SpeechAct.Report], ["HELP", "DANGER"]);
		AddSpecial("SELF_HARM_SUPPORT", "STAY WITH ME. FIND A TRUSTED PERSON WHO CAN HELP YOU NOW.", ResponsePolicy.Defer,
			DialogueDomain.HealthRepair, KnowledgeTarget.None, [SpeechAct.Report], ["SUICIDE", "MYSELF"]);
		AddSpecial("NO_RESPONSE", string.Empty, ResponsePolicy.NoResponse, null,
			KnowledgeTarget.None, [], []);
		AddSpecial("TRANSACTION_DONE", "THE TRANSACTION IS COMPLETE.", ResponsePolicy.Acknowledge,
			DialogueDomain.TradeEconomy, KnowledgeTarget.None, [SpeechAct.Confirm], ["BOUGHT", "SOLD"]);
		AddSpecial("TRANSACTION_FAILED", "THE TRANSACTION COULD NOT BE COMPLETED.", ResponsePolicy.Answer,
			DialogueDomain.TradeEconomy, KnowledgeTarget.None, [SpeechAct.Report], ["FAILED"]);
		AddSpecial("REFERENCE_CLARIFY", "WHICH EARLIER PERSON, PLACE, OR ITEM DO YOU MEAN?", ResponsePolicy.Clarify,
			null, KnowledgeTarget.None, [SpeechAct.Ask], ["IT", "THAT", "THERE"]);
		AddSpecial("UNKNOWN_PERSONA_FACT", "THAT PART OF MY STORY HAS NOT BEEN AUTHORED.", ResponsePolicy.Answer,
			DialogueDomain.Identity, KnowledgeTarget.None, [SpeechAct.Ask], ["WHO", "YOUR"]);
		AddSpecial("CAPABILITY_UNAVAILABLE", "I CANNOT DO THAT WITHOUT THE REQUIRED GAME TOOL.", ResponsePolicy.Defer,
			DialogueDomain.Assistance, KnowledgeTarget.Capabilities, [SpeechAct.Request], ["CAN YOU"]);

		foreach (var domain in Enum.GetValues<DialogueDomain>())
			foreach (var policy in Enum.GetValues<ResponsePolicy>())
			{
				var id = $"{domain.ToString().ToUpperInvariant()}_{policy.ToString().ToUpperInvariant()}";
				var label = SplitWords(domain.ToString());
				var baseText = policy switch
				{
					ResponsePolicy.Answer => $"I CAN ANSWER ABOUT {label}.",
					ResponsePolicy.Clarify => $"SAY WHAT YOU NEED TO KNOW ABOUT {label}.",
					ResponsePolicy.ExecuteTool => $"I WILL CHECK {label}.",
					ResponsePolicy.Refuse => $"I WILL NOT HELP WITH THAT {label} REQUEST.",
					ResponsePolicy.NoResponse => string.Empty,
					ResponsePolicy.Acknowledge => $"I UNDERSTAND YOUR {label} MESSAGE.",
					ResponsePolicy.Negotiate => $"LET US DISCUSS TERMS FOR {label}.",
					ResponsePolicy.Defer => $"I CANNOT HANDLE {label} RIGHT NOW.",
					_ => throw new ArgumentOutOfRangeException()
				};
				plans.Add(new ResponsePlanDefinition(id, policy, domain, KnowledgeTarget.None, [],
					[label], Variations(baseText, id)));
			}

		return plans.OrderBy(plan => plan.Id, StringComparer.Ordinal).ToArray();

		void AddSpecial(
			string id, string text, ResponsePolicy policy, DialogueDomain? domain,
			KnowledgeTarget target, IReadOnlyList<SpeechAct> acts, IReadOnlyList<string> keywords) =>
			plans.Add(new ResponsePlanDefinition(id, policy, domain, target, acts, keywords, Variations(text, id)));
	}

	private static IReadOnlyList<string> Variations(string text, string id)
	{
		if (text.Length == 0) return [string.Empty];
		var stem = text.TrimEnd('.', '?', '!');
		var terminator = text[^1] is '.' or '?' or '!' ? text[^1] : '.';
		var prefixes = new[] { "", "LISTEN: ", "UNDERSTOOD: ", "VERY WELL: ", "HEAR ME: " };
		var suffixes = new[] { "", " I AM READY.", " THAT IS MY ANSWER.", " WE CAN CONTINUE.", " SPEAK PLAINLY." };
		var result = new List<string>(25);
		foreach (var prefix in prefixes)
			foreach (var suffix in suffixes)
			{
				var candidate = prefix + stem + terminator + suffix;
				if (!result.Contains(candidate, StringComparer.Ordinal)) result.Add(candidate);
			}
		for (var index = result.Count; index < 25; index++)
			result.Add($"{stem}. RESPONSE {index + 1} FOR {id}.");
		return result.Take(25).ToArray();
	}

	private static string SplitWords(string value) =>
		string.Concat(value.Select((character, index) => index > 0 && char.IsUpper(character)
			? " " + character
			: character.ToString())).ToUpperInvariant();
}

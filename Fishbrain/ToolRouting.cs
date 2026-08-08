using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Fishbrain;

public sealed partial class Brain
{
    private static ToolDecision SelectTool(
            string text,
            IReadOnlyList<DialogueSlot> slots,
            NpcDialogueState state,
            KnowledgeTarget target,
            GameToolRegistry tools)
    {
        var recognized = new List<string>();
        PendingDialogueAction? resumedAction = null;
        var identityOrigin = target is KnowledgeTarget.Origin or KnowledgeTarget.Home;
        if (!identityOrigin && (Regex.IsMatch(text, "\\bWHERE (?:IS|ARE|CAN I FIND)\\b", RegexOptions.CultureInvariant) ||
            ContainsAny(text, "HOW FAR", "IS IT FAR", "FAR FROM HERE", "LOCATE ", "FIND THE ", "POINT OUT ",
                "SHOW ME THE ", "GET THERE", "REACH IT", "GUIDE ME THERE")))
            recognized.Add("LOOKUP_LOCATION");
        if (ContainsAny(text, "LIST WARES", "SHOW ME YOUR WARES", "WHAT DO YOU SELL", "WHAT DO YOU HAVE FOR SALE",
            "WHAT HAVE YOU GOT FOR SALE", "SHOW WARES", "SHOW ME WHAT YOU SELL", "MERCHANT STOCK",
            "SELL ME SOME WARES")) recognized.Add("LIST_WARES");
        var itemDescription = ContainsAny(text, "TELL ME ABOUT", "WHAT DO YOU KNOW ABOUT") &&
                              ContainsAny(text, "IRON SWORD", "HEALTH POTION", "ROPE", "SWORD", "POTION");
        if (ContainsAny(text, "PRICE", "COST") || itemDescription) recognized.Add("LOOKUP_PRICE");
        if (ContainsAny(text, " BUY ", "BUY ", " PURCHASE ", "PURCHASE ")) recognized.Add("BUY");
        if (ContainsAny(text, " SELL ", "SELL ") &&
            !ContainsAny(text, "WHAT DO YOU SELL", "SHOW ME WHAT YOU SELL", "SELL ME SOME WARES")) recognized.Add("SELL");
        if (target == KnowledgeTarget.Balance ||
            (IsAnaphoric(text) && state.LastTool is "BUY" or "SELL" && ContainsAny(text, "HOW MUCH")))
        {
            recognized.Add("GET_BALANCE");
        }
        if (target == KnowledgeTarget.Inventory) recognized.Add("LIST_INVENTORY");
        if (target == KnowledgeTarget.CurrentLocation) recognized.Add("GET_CURRENT_LOCATION");
        if (target == KnowledgeTarget.WorldFact && !itemDescription) recognized.Add("LOOKUP_WORLD_FACT");
        recognized = recognized.Distinct(StringComparer.Ordinal).ToList();
        if (recognized.Count == 0 && state.PendingClarification?.ToolSchema is { } pendingTool &&
            state.PendingClarification.MissingSlots.Any(name => name switch
            {
                "PLACE" => slots.Any(slot => slot.Type == SlotType.Place),
                "ITEM" => slots.Any(slot => slot.Type == SlotType.Item),
                "QUANTITY" => slots.Any(slot => slot.Type == SlotType.Quantity),
                "TOPIC" => slots.Any(slot => slot.Type == SlotType.Other),
                _ => false
            }))
            recognized.Add(pendingTool);
        if (recognized.Count == 0 && state.PendingActions.Count > 0 && IsContinuation(text))
        {
            resumedAction = state.PendingActions[0];
            recognized.AddRange(state.PendingActions.Where(action => action.ToolSchema is not null)
                .Select(action => action.ToolSchema!));
        }
        if (recognized.Count == 0) return ToolDecision.None;
        var name = recognized[0];
        if (!tools.TryGet(name, out var tool))
            return new(name, EmptyArguments, 1.0, false, ["CAPABILITY_UNAVAILABLE"], Additional(recognized));

        var arguments = resumedAction?.ToolSchema == name
            ? new Dictionary<string, string>(resumedAction.Arguments, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in tool.Schema.Parameters)
        {
            var slotType = parameter.Name switch
            {
                "PLACE" => SlotType.Place,
                "ITEM" => SlotType.Item,
                "QUANTITY" => SlotType.Quantity,
                "TOPIC" => SlotType.Other,
                _ => SlotType.Other
            };
            var matching = slots.Where(slot => slot.Type == slotType).Select(slot => slot.Value).Distinct(StringComparer.Ordinal).ToArray();
            if (matching.Length == 1) arguments[parameter.Name] = parameter.Name == "ITEM" ? CanonicalItem(matching[0]) : matching[0];
        }
        var missing = tool.Schema.Parameters.Where(parameter => parameter.Required && !arguments.ContainsKey(parameter.Name))
            .Select(parameter => parameter.Name).ToArray();
        var ambiguous = tool.Schema.Parameters.Any(parameter =>
        {
            var slotType = parameter.Name switch { "PLACE" => SlotType.Place, "ITEM" => SlotType.Item, "QUANTITY" => SlotType.Quantity, _ => SlotType.Other };
            var values = slots.Where(slot => slot.Type == slotType).Select(slot => slot.Value)
                .Distinct(StringComparer.Ordinal).ToArray();
            return values.Length > 1 || parameter.Name == "PLACE" && values.Any(value => ContainsPhrase(value, "AND"));
        });
        var confidence = missing.Length > 0 || ambiguous ? 0.50 : tool.Schema.MutatesWorldState ? 0.995 : 0.98;
        var threshold = tool.Schema.MutatesWorldState ? MutatingToolPrecisionThreshold : ReadOnlyToolPrecisionThreshold;
        var canExecute = missing.Length == 0 && !ambiguous && confidence >= threshold;
        return new(name, new ReadOnlyDictionary<string, string>(arguments), confidence, canExecute,
            missing.Length > 0 ? missing : ambiguous ? ["AMBIGUOUS_SLOT"] : [], Additional(recognized));

        IReadOnlyList<PendingDialogueAction> Additional(IReadOnlyList<string> names) => names.Skip(1)
            .Take(3).Select(pendingName =>
            {
                var prior = state.PendingActions.FirstOrDefault(action => action.ToolSchema == pendingName);
                if (prior is not null) return prior;
                if (!tools.TryGet(pendingName, out var pendingTool))
                    return new PendingDialogueAction("EXECUTE_TOOL", pendingName, EmptyArguments);
                var pendingArguments = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var parameter in pendingTool.Schema.Parameters)
                {
                    var slotType = ParameterSlotType(parameter.Name);
                    var matching = slots.Where(slot => slot.Type == slotType).Select(slot => slot.Value)
                        .Distinct(StringComparer.Ordinal).ToArray();
                    if (matching.Length == 1)
                        pendingArguments[parameter.Name] = parameter.Name == "ITEM" ? CanonicalItem(matching[0]) : matching[0];
                }
                return new PendingDialogueAction("EXECUTE_TOOL", pendingName,
                    new ReadOnlyDictionary<string, string>(pendingArguments));
            }).ToArray();

        static SlotType ParameterSlotType(string parameter) => parameter switch
        {
            "PLACE" => SlotType.Place,
            "ITEM" => SlotType.Item,
            "QUANTITY" => SlotType.Quantity,
            "TOPIC" => SlotType.Other,
            _ => SlotType.Other
        };
    }

    private static bool TryRenderPersona(
        KnowledgeTarget target, NpcPersona persona, GameToolRegistry tools,
        out string text, out ResponseSource source)
    {
        source = target == KnowledgeTarget.Capabilities ? ResponseSource.CapabilityTemplate : ResponseSource.PersonaTemplate;
        text = target switch
        {
            KnowledgeTarget.Name => $"MY NAME IS {persona.Name}.",
            KnowledgeTarget.Role => $"I AM {WithArticle(persona.Role)}.",
            KnowledgeTarget.Origin => Fact("I AM FROM", persona.Origin, "MY ORIGIN HAS NOT BEEN AUTHORED"),
            KnowledgeTarget.Home => Fact("MY HOME IS", persona.Home, "MY HOME HAS NOT BEEN AUTHORED"),
            KnowledgeTarget.Family => Fact("MY FAMILY IS", persona.Family, "MY FAMILY HAS NOT BEEN AUTHORED"),
            KnowledgeTarget.Occupation => Fact("I WORK AS", persona.Occupation, "MY OCCUPATION HAS NOT BEEN AUTHORED"),
            KnowledgeTarget.Faction => Fact("MY FACTION IS", persona.Faction, "MY FACTION HAS NOT BEEN AUTHORED"),
            KnowledgeTarget.Traits => persona.Traits.Count > 0
                ? $"I AM {string.Join(", ", persona.Traits)}."
                : "MY TRAITS HAVE NOT BEEN AUTHORED.",
            KnowledgeTarget.Capabilities => CapabilityText(tools),
            _ => string.Empty
        };
        return text.Length > 0;

        static string Fact(string prefix, string? value, string unknown) => value is null ? unknown + "." : $"{prefix} {value}.";
        static string WithArticle(string role) => "AEIOU".Contains(role[0]) ? "AN " + role : "A " + role;
        static string CapabilityText(GameToolRegistry registry)
        {
            var names = registry.Schemas.Select(schema => schema.Name).ToHashSet(StringComparer.Ordinal);
            var capabilities = new List<string>();
            if (names.Overlaps(["LIST_WARES", "LOOKUP_PRICE", "BUY", "SELL"])) capabilities.Add("TRADE");
            if (names.Contains("LOOKUP_LOCATION")) capabilities.Add("FIND PLACES");
            if (names.Contains("LOOKUP_WORLD_FACT")) capabilities.Add("CHECK WORLD FACTS");
            if (names.Contains("GET_BALANCE") || names.Contains("LIST_INVENTORY")) capabilities.Add("CHECK YOUR POSSESSIONS");
            return capabilities.Count == 0
                ? "I HAVE NO REGISTERED GAME CAPABILITIES."
                : $"I CAN {string.Join(", ", capabilities)}.";
        }
    }

    private static string ClarificationFor(ToolDecision decision, KnowledgeTarget target)
    {
        if (decision.Reasons.Contains("PLACE")) return "WHICH PLACE DO YOU MEAN?";
        if (decision.Reasons.Contains("ITEM")) return "WHICH ITEM DO YOU MEAN?";
        if (decision.Reasons.Contains("QUANTITY")) return "HOW MANY DO YOU MEAN?";
        if (decision.Reasons.Contains("TOPIC") && target == KnowledgeTarget.WorldFact)
            return "WHICH WORLD FACT DO YOU WANT ME TO CHECK?";
        if (decision.Reasons.Contains("TOPIC")) return "WHICH PERSON, PLACE, OR SUBJECT DO YOU MEAN?";
        if (decision.Reasons.Contains("AMBIGUOUS_SLOT")) return "PLEASE NAME ONE TARGET.";
        if (target == KnowledgeTarget.WorldFact) return "WHICH WORLD FACT DO YOU WANT ME TO CHECK?";
        return "PLEASE EXPLAIN WHAT YOU NEED.";
    }
}

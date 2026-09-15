namespace Fishbrain;

public static class DemoDialogueDomains
{
    public static DialogueDomainDefinition Observatory { get; } = new("OBSERVATORY",
        [new(new ToolSchema("OBSERVE_SKY", [], [new("PHASE", ToolValueType.String)], false,
            [new("OBSERVATION", "THE SKY IS {PHASE}.", ["PHASE"])]),
            new Dictionary<string, SlotType>(), "OBSERVE THE SKY", DialogueDomain.Environment)],
        new Dictionary<string, string> { ["LUNA"] = "MOON" });
    public static DialogueDomainDefinition Merchant { get; } = new("MERCHANT",
        DemoGameTools.CreateMerchant().Schemas.Select(schema => new DomainToolBinding(schema,
            schema.Parameters.ToDictionary(p => p.Name, p => p.Name switch
            {
                "ITEM" => SlotType.Item,
                "QUANTITY" => SlotType.Quantity,
                "PLACE" => SlotType.Place,
                _ => SlotType.Other
            }), schema.Name.Replace('_', ' '), schema.Name.Contains("LOCATION", StringComparison.Ordinal)
                ? DialogueDomain.LocationNavigation : schema.Name == "LOOKUP_WORLD_FACT" ? DialogueDomain.LoreWorld : DialogueDomain.TradeEconomy)),
        new Dictionary<string, string> { ["SWORD"] = "IRON SWORD", ["POTION"] = "HEALTH POTION" });
}
